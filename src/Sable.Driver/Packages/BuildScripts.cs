using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Packages;

/// <summary>What a package's build script left for its build: the Sable it wrote, and what the link needs.</summary>
/// <param name="OutDirectory">Its <c>$OUT_DIR</c>.</param>
/// <param name="Sources">The <c>.sb</c> files under <c>$OUT_DIR</c>, compiled as part of the package's library.</param>
/// <param name="LinkArguments">For the linker: <c>-l</c> and <c>-L</c> from its <c>sable:link-lib</c> and <c>sable:link-search</c> lines.</param>
/// <param name="Fingerprint">A hash of everything it wrote: what a build that links it is keyed by.</param>
public sealed record BuildScriptOutput(string OutDirectory, IReadOnlyList<string> Sources, IReadOnlyList<string> LinkArguments, string Fingerprint);

/// <summary>
///     Runs packages' build scripts (docs/05 §5.6): <c>build.sb</c>, an
///     ordinary program, run before its package compiles — by the SIL
///     interpreter, as a manifest is (§5.2.6), so that what it may touch is
///     enforced at every call that touches anything.
/// </summary>
/// <remarks>
///     <para>
///         The manifest's <c>build: .script("build.sb", allowExec:,
///         allowRead:, allowWrite:)</c> is all the script may do: read under
///         <c>allowRead</c> and <c>allowWrite</c>, write under
///         <c>allowWrite</c>, run the programs <c>allowExec</c> names. Its own
///         <c>$OUT_DIR</c> it may always read and write. Paths are taken from
///         the package's directory, links followed, so a link cannot lead out.
///         Anything else stops the script with an error naming what it asked
///         for — it is never quietly refused. It sees only the environment
///         variables the build gives it (<c>OUT_DIR</c>, <c>SABLE_PACKAGE_DIR</c>,
///         <c>SABLE_PACKAGE_NAME</c>, <c>SABLE_PACKAGE_VERSION</c>,
///         <c>SABLE_PROFILE</c>, <c>SABLE_FEATURES</c>, <c>SABLE_TARGET_OS</c>,
///         <c>SABLE_TARGET_ARCH</c>), so the build stays what its inputs make it.
///         There is no network library for it to reach, so <c>allowNet</c>
///         has nothing yet to allow.
///     </para>
///     <para>
///         A program it runs is a process of its own, which the interpreter
///         cannot confine: <c>allowExec</c> is the line, and the program gets
///         those variables over the build's environment.
///     </para>
///     <para>
///         What it leaves: the <c>.sb</c> files it writes under <c>$OUT_DIR</c>
///         are compiled into the package's library, and lines it prints of the
///         form <c>sable:link-lib=NAME</c>, <c>sable:link-search=DIR</c>,
///         <c>sable:rerun-if-changed=PATH</c> and <c>sable:warning=TEXT</c>
///         reach the link, the cache and the user. It runs again only when
///         something it depends on changed: its text, the manifest, the
///         profile or features, the toolchain, or any file it read or directory
///         it listed — which the sandbox records, so none need be declared.
///     </para>
/// </remarks>
public static class BuildScripts {
    /// <summary>A bound on a script's work, as a manifest has: large, since a script may generate a lot, but not endless.</summary>
    const int StepLimit = 1_000_000_000;

    /// <summary>
    ///     Runs, or reuses the outcome of, the build script of each package in
    ///     <paramref name="graph" /> that has one, for <paramref name="profile" />.
    /// </summary>
    /// <exception cref="PackageException">A script does not compile, breaks its sandbox, or traps.</exception>
    public static IReadOnlyDictionary<string, BuildScriptOutput> Run(PackageGraph graph, Profile profile, PackageEnvironment environment) {
        // Filled as the scripts run, so that a script compiled with a
        // build-dependency's library has what that one's script wrote.
        var outputs = new Dictionary<string, BuildScriptOutput>(StringComparer.Ordinal);
        graph.BuildOutputs = outputs;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(ResolvedPackage package) {
            if (!visited.Add(package.Name)) {
                return;
            }

            foreach (var dependency in graph.BuildClosure(package.Name)) {
                Visit(dependency);
            }

            if (package.Package.Manifest.Build is not null) {
                outputs[package.Name] = RunOne(graph, package, profile, environment);
            }
        }

        foreach (var package in graph.Packages.Values.OrderBy(static p => p.Name, StringComparer.Ordinal)) {
            Visit(package);
        }

        return outputs;
    }

    static BuildScriptOutput RunOne(PackageGraph graph, ResolvedPackage package, Profile profile, PackageEnvironment environment) {
        var workspace = graph.Workspace;
        var spec = package.Package.Manifest.Build!;
        var directory = package.Package.Directory;
        var script = Path.GetFullPath(Path.Combine(directory, spec.Script));
        if (!script.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(script)) {
            throw new PackageException($"{package.Name}: the build script '{spec.Script}' is not a file in the package");
        }

        var buildDirectory = Path.Combine(workspace.TargetDirectory, profile.Name, "build", $"{package.Name.Replace('/', '~')}-{package.Version}");
        var outDirectory = Path.Combine(buildDirectory, "out");
        var stampPath = Path.Combine(buildDirectory, "stamp.json");
        var features = package.Features.Order(StringComparer.Ordinal).ToList();
        var text = File.ReadAllText(script);

        // Its build-dependencies' libraries, as a debug build parses them: part of what it is.
        var libraries = graph.BuildClosure(package.Name)
            .SelectMany(p => graph.LibrarySources(p).Select(file => PackageBuilder.Parse(graph, p, file, optimise: false)))
            .ToList();
        var key = Hash(string.Join("\n", [
            Hash(string.Concat(libraries.Select(static t => $"{t.Text.FileName}\n{t.Text.Text}\n"))),
            typeof(BuildScripts).Assembly.ManifestModule.ModuleVersionId.ToString(),
            text,
            ManifestFiles.Text(directory),
            profile.Name,
            string.Join(",", features),
            outDirectory,
        ]));

        if (Fresh(stampPath, key) is var (kept, written)) {
            return Output(outDirectory, kept, written);
        }

        if (Directory.Exists(outDirectory)) {
            Directory.Delete(outDirectory, true);
        }

        Directory.CreateDirectory(outDirectory);
        environment.Status.WriteLine($"     Running build script of {package.Name} v{package.Version}");

        var variables = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["OUT_DIR"] = outDirectory,
            ["SABLE_PACKAGE_DIR"] = directory,
            ["SABLE_PACKAGE_NAME"] = package.Name,
            ["SABLE_PACKAGE_VERSION"] = package.Version.ToString(),
            ["SABLE_PROFILE"] = profile.Name,
            ["SABLE_FEATURES"] = string.Join(",", features.Where(static f => f != "default")),
            ["SABLE_TARGET_OS"] = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
            ["SABLE_TARGET_ARCH"] = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64",
        };
        var name = $"{package.Name}/{spec.Script}";
        var sandbox = new BuildSandbox(name, directory, outDirectory, spec, variables);
        var printed = Interpret(script, text, name, features, libraries, sandbox, package.Name);

        // What it printed for the build, a line each.
        var link = new List<string>();
        var rerun = new List<string>();
        foreach (var line in printed.Split('\n').Select(static l => l.TrimEnd('\r'))) {
            if (!line.StartsWith("sable:", StringComparison.Ordinal)) {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            var (directive, value) = equals < 0 ? (line[6..], "") : (line[6..equals], line[(equals + 1)..]);
            switch (directive) {
                case "link-lib" when value.Length > 0 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+'):
                    link.Add("-l" + value);
                    break;
                case "link-search" when value.Length > 0:
                    link.Add("-L" + Path.GetFullPath(value, directory));
                    break;
                case "rerun-if-changed" when value.Length > 0:
                    rerun.Add(Path.GetFullPath(value, directory));
                    break;
                case "warning":
                    environment.Error.WriteLine($"warning: {package.Name}: {value}");
                    break;
                default:
                    throw new PackageException($"{name} printed '{line}', which the build does not understand: it takes sable:link-lib=, sable:link-search=, sable:rerun-if-changed= and sable:warning= (docs/05 §5.6)");
            }
        }

        // What it depends on: every file it read and directory it listed
        // (but its own output), and what it named.
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in sandbox.Read.Concat(rerun).Where(p => !IsUnder(p, BuildSandbox.RealPath(outDirectory)))) {
            inputs[path] = Fingerprint(path);
        }

        var output = OutputFingerprint(outDirectory);
        var stamp = new JsonObject {
            ["key"] = key,
            ["inputs"] = new JsonObject(inputs.Select(static i => KeyValuePair.Create(i.Key, (JsonNode?)JsonValue.Create(i.Value)))),
            ["link"] = new JsonArray([..link.Select(static l => (JsonNode)JsonValue.Create(l)!)]),
            ["output"] = output,
        };
        File.WriteAllText(stampPath, stamp.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return Output(outDirectory, link, output);
    }

    /// <summary>Compiles and interprets the script; what it printed. A failure is the package's error, with the script's own output.</summary>
    static string Interpret(string path, string text, string name, IReadOnlyList<string> features, IReadOnlyList<SyntaxTree> libraries, BuildSandbox sandbox, string package) {
        var tree = SyntaxTree.Parse(new SourceText(text, name), Pipeline.ParseOptionsFor(false, features));
        var program = new List<SyntaxTree> { tree };
        program.AddRange(libraries);
        var compilation = Compilation.Create([..program, ..Library.For(program)]);
        var errors = compilation.GetAllDiagnosticsByFile()
            .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static d => d.Tree is { } t ? d.Diagnostic.Format(t.Text) : $"error[{d.Diagnostic.Id}]: {d.Diagnostic.Message}")
            .ToList();
        if (errors.Count > 0) {
            throw new PackageException($"the build script of {package} does not compile:\n\n{string.Join("\n\n", errors)}");
        }

        var (module, lowering) = SilGen.Lower(compilation);
        if (lowering.FirstOrDefault(static d => d.Severity == DiagnosticSeverity.Error) is { } failure) {
            throw new PackageException($"the build script of {package} does not compile: {failure.Message}");
        }

        if (module.EntryPoint is null) {
            throw new PackageException($"the build script of {package} has no 'fn main()' to run");
        }

        if (SilVerifier.Verify(module) is { Count: > 0 } problems) {
            throw new PackageException($"internal error: the build script of {package} lowers to SIL that does not verify: {problems[0]}");
        }

        var interpreter = new SilInterpreter(module, StepLimit) { Sandbox = sandbox };
        try {
            interpreter.Run(module.EntryPoint);
        } catch (SilTrap trap) when (trap is not { Exits: true, ExitStatus: 0 }) {
            var said = interpreter.ErrorOutput.Length > 0 ? $"\nIt wrote to standard error:\n{interpreter.ErrorOutput.ToString().TrimEnd()}" : "";
            var reason = trap.Message.Contains("step limit", StringComparison.Ordinal)
                ? $"it ran for more than {StepLimit:N0} steps"
                : trap.Message;
            throw new PackageException($"the build script of {package} ({path}) failed: {reason}{said}");
        }

        return interpreter.Output.ToString();
    }

    /// <summary>The link arguments and output a stamp kept, when everything the script depended on is as it was.</summary>
    static (IReadOnlyList<string> Link, string Output)? Fresh(string stampPath, string key) {
        if (!File.Exists(stampPath)) {
            return null;
        }

        try {
            var stamp = JsonNode.Parse(File.ReadAllText(stampPath))!.AsObject();
            if (stamp["key"]?.GetValue<string>() != key) {
                return null;
            }

            foreach (var (path, fingerprint) in stamp["inputs"]!.AsObject()) {
                if (Fingerprint(path) != fingerprint!.GetValue<string>()) {
                    return null;
                }
            }

            return ([..stamp["link"]!.AsArray().Select(static l => l!.GetValue<string>())], stamp["output"]!.GetValue<string>());
        } catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException) {
            return null;
        }
    }

    static BuildScriptOutput Output(string outDirectory, IReadOnlyList<string> link, string fingerprint) =>
        new(outDirectory,
            Directory.Exists(outDirectory)
                ? [..Directory.GetFiles(outDirectory, "*.sb", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
                : [],
            link,
            fingerprint);

    /// <summary>A hash of every file under <c>$OUT_DIR</c>, by name and content.</summary>
    static string OutputFingerprint(string outDirectory) =>
        Hash(string.Join("\n", Directory.GetFiles(outDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(outDirectory, f)}={Fingerprint(f)}")));

    /// <summary>What a path holds, as a hash: a file's bytes, a directory's names, or that there is nothing there.</summary>
    static string Fingerprint(string path) {
        try {
            if (File.Exists(path)) {
                return "file:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            }

            if (Directory.Exists(path)) {
                return "directory:" + Hash(string.Join("\n", Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return "unreadable";
        }

        return "missing";
    }

    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static bool IsUnder(string path, string root) =>
        path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

/// <summary>A build script's powers, as its manifest declares them: see <see cref="BuildScripts" />.</summary>
sealed class BuildSandbox : SilSandbox {
    readonly string _script;
    readonly string _packageDirectory;
    readonly BuildSpec _spec;
    readonly IReadOnlyList<string> _readable;
    readonly IReadOnlyList<string> _writable;
    readonly IReadOnlyDictionary<string, string> _variables;

    public BuildSandbox(string script, string packageDirectory, string outDirectory, BuildSpec spec, IReadOnlyDictionary<string, string> variables) {
        _script = script;
        _packageDirectory = packageDirectory;
        _spec = spec;
        _variables = variables;
        CurrentDirectory = packageDirectory;

        string Root(string entry) => RealPath(Path.GetFullPath(entry.Replace("$OUT_DIR", outDirectory, StringComparison.Ordinal), packageDirectory));
        var output = RealPath(outDirectory);
        _writable = [output, ..spec.AllowWrite.Select(Root)];
        _readable = [.._writable, ..spec.AllowRead.Select(Root)];
    }

    /// <summary>Every path it read or listed, as it found it: what decides whether it must run again.</summary>
    public SortedSet<string> Read { get; } = new(StringComparer.Ordinal);

    public override string CurrentDirectory { get; set; }

    public override IReadOnlyDictionary<string, string> ProgramEnvironment => _variables;

    public override string? Environment(string name) => _variables.GetValueOrDefault(name);

    public override IEnumerable<string> EnvironmentNames => _variables.Keys;

    public override string Check(string path, SilAccess access) {
        var full = RealPath(Path.GetFullPath(path, CurrentDirectory));
        var roots = access == SilAccess.Write ? _writable : _readable;
        if (!roots.Any(r => BuildScripts.IsUnder(full, r))) {
            var (verb, list, field) = access == SilAccess.Write
                ? ("write", _spec.AllowWrite, "allowWrite")
                : ("read", [.._spec.AllowRead, .._spec.AllowWrite], "allowRead");
            throw new SilTrap(
                $"{_script} may not {verb} '{path}': it {verb}s only under $OUT_DIR{string.Concat(list.Select(static e => ", " + e))}; " +
                $"if it needs to, add the path to {field} in its manifest's build script (docs/05 §5.6)");
        }

        if (access == SilAccess.Read) {
            Read.Add(full);
        }

        return full;
    }

    public override string CheckProgram(string program) {
        var allowed = program.Contains('/') || program.Contains(Path.DirectorySeparatorChar)
            ? _spec.AllowExec.Any(e => (e.Contains('/') || e.Contains(Path.DirectorySeparatorChar)) && Path.GetFullPath(e, _packageDirectory) == Path.GetFullPath(program, CurrentDirectory))
            : _spec.AllowExec.Contains(program);
        if (!allowed) {
            throw new SilTrap(
                $"{_script} may not run '{program}': it runs only {(_spec.AllowExec.Count == 0 ? "nothing" : string.Join(", ", _spec.AllowExec))}; " +
                "if it needs to, add it to allowExec in its manifest's build script (docs/05 §5.6)");
        }

        return program;
    }

    /// <summary>
    ///     <paramref name="path" /> with every link in it followed: where it
    ///     really is, so that a link inside an allowed directory cannot lead
    ///     out of it. The part that does not exist yet is kept as written.
    /// </summary>
    public static string RealPath(string path, int depth = 0) {
        path = Path.GetFullPath(path);
        if (depth > 40) {
            return path;
        }

        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) {
                next = RealPath(target.FullName, depth + 1);
            }

            current = next;
        }

        return current;
    }
}
