using System.Security.Cryptography;
using System.Text;
using Sable.CodeGen;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Build;

/// <summary>
///     A source file of a program: where it is, the name diagnostics give it,
///     the features it is parsed with, for a registry dependency the
///     workspace outside this one it comes from, which <c>package</c> stops
///     at, and the package it belongs to, whose modules are its own.
/// </summary>
public sealed record SourceFile(string Path, string Name, IReadOnlyList<string> Features, string? Workspace = null, string? Package = null) {
    /// <summary>The attributes the package's generators answer to, and those they read on members (docs/08).</summary>
    public GeneratorAttributes Generators { get; init; } = GeneratorAttributes.None;

    public ParseOptions Options => ParseOptions.Default with {
        Features = [..Features],
        Workspace = Workspace,
        Package = Package,
        GeneratorAttributes = [..Generators.Types],
        GeneratorMemberAttributes = [..Generators.Members],
    };
}

/// <summary>The attributes a package's visible source generators answer to on types, and read on members.</summary>
public sealed record GeneratorAttributes(IReadOnlyList<string> Types, IReadOnlyList<string> Members) {
    public static readonly GeneratorAttributes None = new([], []);

    /// <summary>What a cache keyed by a file's options hashes.</summary>
    public override string ToString() => $"{string.Join(",", Types)};{string.Join(",", Members)}";
}

/// <summary>
///     A unit of separate compilation: a package's library, or a binary's
///     entry file. The standard library the program imports is one more,
///     which the build adds.
/// </summary>
public sealed record UnitPlan(string Name, IReadOnlyList<SourceFile> Files);

/// <summary>How a program is built: the profile's settings that change what comes out.</summary>
/// <param name="DebugDirectory">Where debug information's file names are relative to; null for no debug information.</param>
public sealed record BuildSettings(string Profile, bool Optimise, string? DebugDirectory, bool Exclusivity) {
    public bool Debug => DebugDirectory is not null;

    /// <summary>Whether integer overflow traps (<c>overflow-checks</c>); without, the arithmetic wraps.</summary>
    public bool OverflowChecks { get; init; } = true;

    /// <summary>
    ///     What clang is asked for: by default <c>-O2</c> or <c>-O0</c> as
    ///     <see cref="Optimise" /> says, with debug information when there is
    ///     a debug directory; a profile says more.
    /// </summary>
    public NativeOptions Native {
        get => _native ?? NativeOptions.Of(Optimise, Debug);
        init => _native = value;
    }

    readonly NativeOptions? _native;

    /// <summary>
    ///     Separately compiled — each unit to an object of its own, cached —
    ///     when nothing needs to see the whole program at once: an unoptimised
    ///     debug build, which runs no SIL passes and no link-time optimisation;
    ///     or an optimised build whose profile asks for ThinLTO (or none),
    ///     where each unit's SIL passes run on the unit and LLVM optimises
    ///     across units as they link. A profile's default, whole-program
    ///     link-time optimisation (<c>lto = "fat"</c>), compiles the program
    ///     whole, so that the SIL passes see all of it; so does an optimised
    ///     build with debug information, whose link-time objects Apple's
    ///     debugger would need kept. Otherwise only an unchanged build is skipped.
    /// </summary>
    public bool Separate =>
        !Whole && (Debug ? !Optimise : Optimise && Native.Lto != LtoMode.Fat)
        && (Library is not { } kind || NativeObjects.LinksLibrary(kind, Native.TargetOrHost));

    /// <summary>
    ///     Build a library C links rather than an executable: the first unit
    ///     leads it, with no entry point — it sets up the globals as the
    ///     library is loaded (<see cref="SilGen.LowerUnit" />'s
    ///     <c>library</c>). Compiled separately where the target's link can
    ///     make one of objects (<see cref="NativeObjects.LinksLibrary" />).
    /// </summary>
    public LibraryKind? Library { get; init; }

    /// <summary>Compile the program whole even where it could be compiled separately: what a separate build is checked against.</summary>
    public bool Whole { get; init; }

    /// <summary>Built without reflection (docs/06 §6.1): <c>Mirror</c> and <c>TypeInfo</c> are refused, and field descriptors have no names.</summary>
    public bool DenyReflection { get; init; }

    /// <summary>A profile's <c>arc-checks</c>: compiled code's retains and releases trap on an object whose deinit has run.</summary>
    public bool ArcChecks { get; init; }

    /// <summary>
    ///     Whether the program's files and the library's are parsed with
    ///     <c>DEBUG</c> defined rather than <c>RELEASE</c> (SYNTAX.md §21): a
    ///     profile's <c>debug-assertions</c>, or else an unoptimised build.
    /// </summary>
    public bool DebugAssertions {
        get => _debugAssertions ?? !Optimise;
        init => _debugAssertions = value;
    }

    readonly bool? _debugAssertions;

    /// <summary>
    ///     The opt-level whose SIL passes run (<see cref="SilPassManager.ForLevel" />):
    ///     the profile's, or <c>2</c> for an optimised build whose native
    ///     options do not say, and <c>0</c> for an unoptimised one.
    /// </summary>
    public string SilLevel => !Optimise ? "0" : Native.Optimise ? Native.OptLevel : "2";
}

/// <summary>What a build did.</summary>
/// <param name="Kind"><c>fresh</c> (nothing to do), <c>incremental</c> or <c>whole</c>.</param>
public sealed record BuildOutcome(bool Succeeded, IReadOnlyList<string> Errors, string Kind, int Compiled, int Reused) {
    public static BuildOutcome Failed(IReadOnlyList<string> errors) => new(false, errors, "failed", 0, 0);

    /// <summary>The front end's warnings about the files it checked: none when nothing was compiled again.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
///     Builds a program of many files into an executable, doing again only
///     what its inputs' changes call for (docs/05 §5.4).
/// </summary>
/// <remarks>
///     <para>
///         <b>The build cache</b>, in <c>target/&lt;profile&gt;/cache</c>. Every
///         key is a SHA-256 over the toolchain (a hash of the compiler's own
///         assemblies, which carry the standard library, and what
///         <c>clang --version</c> says), the settings, the target, and the
///         content of what is compiled — file contents and names and the
///         features each is parsed with, never timestamps.
///     </para>
///     <para>
///         <b>An unchanged build</b> is one whose key over every input is the
///         one the executable was last built from, with the executable as it
///         was left; it reads and hashes the files and does nothing else.
///     </para>
///     <para>
///         <b>Separate compilation.</b> Each unit is lowered from a compilation
///         of the whole program (<see cref="SilGen.LowerUnit" />) to an object
///         file keyed by its own files' contents and the interfaces
///         (<see cref="InterfaceFingerprint" />) of the units it depends on
///         (<see cref="UnitGraph" />). A change to an ordinary function's body
///         changes only its unit's key; a change to what other units can see
///         changes the keys of the units that depend on it — and an
///         extension's, every key, since any unit may see it. Units whose objects
///         are cached are neither type-checked again (their checks passed with
///         the same inputs) nor lowered; the others are, and their objects are
///         compiled by clang in parallel. Before linking, the units' records of
///         the program's types must agree; if they do not, or a unit's lowering
///         reports what the whole program's would not, or the link fails, the
///         program is built whole instead — a stale or inconsistent object never
///         reaches an executable.
///     </para>
/// </remarks>
public static class IncrementalBuild {
    /// <summary>The compiler's identity: its assemblies' contents, which include the embedded library, and the C compiler's version.</summary>
    public static string Toolchain => ToolchainHash.Value;

    static readonly Lazy<string> ToolchainHash = new(() => {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var assembly in new[] { typeof(IncrementalBuild), typeof(LlvmEmitter), typeof(SilModule), typeof(Compilation), typeof(SyntaxTree) }.Select(static t => t.Assembly)) {
            hash.AppendData(Encoding.UTF8.GetBytes(assembly.FullName ?? ""));
            if (assembly.Location is { Length: > 0 } location && File.Exists(location)) {
                hash.AppendData(File.ReadAllBytes(location));
            }
        }

        hash.AppendData(Encoding.UTF8.GetBytes(NativeObjects.CompilerIdentity));
        hash.AppendData(Encoding.UTF8.GetBytes($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}|{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    });

    /// <summary>Format version of the cache: bump when what is cached, or how it is keyed, changes meaning.</summary>
    const string CacheVersion = "sable-build-cache-1";

    /// <summary>
    ///     Builds <paramref name="units" /> — the first holding the entry point,
    ///     or leading the library <see cref="BuildSettings.Library" /> asks for —
    ///     into <paramref name="executable" />, keeping what can be kept in
    ///     <paramref name="cacheDirectory" />.
    /// </summary>
    public static BuildOutcome Build(IReadOnlyList<UnitPlan> units, BuildSettings settings, string executable, string cacheDirectory, BuildTimings? timings = null, TextWriter? log = null) {
        units = [..units.Where(static u => u.Files.Count > 0)];
        using (BuildTimings.Of(timings, "toolchain")) {
            _ = Toolchain;
        }

        var settingsKey = $"{CacheVersion}|{Toolchain}|{settings.Profile}|opt={settings.Optimise}|debug={settings.DebugDirectory}|exclusivity={settings.Exclusivity}|overflow={settings.OverflowChecks}|whole={settings.Whole}|deny-reflection={settings.DenyReflection}|arc={settings.ArcChecks}|sil={settings.SilLevel}|debug-assertions={settings.DebugAssertions}|native={settings.Native.Key}|library={settings.Library}";

        // ---- Read and hash every file --------------------------------------
        string[][] texts;
        string[][] fileHashes;
        using (BuildTimings.Of(timings, "fingerprint")) {
            texts = [..units.Select(static u => new string[u.Files.Count])];
            fileHashes = [..units.Select(static u => new string[u.Files.Count])];
            var all = units.SelectMany(static (u, i) => u.Files.Select((f, j) => (i, j, f))).ToList();
            Parallel.ForEach(all, GlobalOptions.Current.ParallelOptions, item => {
                var (i, j, file) = item;
                var text = File.ReadAllText(file.Path);
                texts[i][j] = text;
                fileHashes[i][j] = Hash($"{file.Name}\n{string.Join(",", file.Features.Order(StringComparer.Ordinal))}\n{file.Generators}\n{text}");
            });
        }

        var buildKey = Hash(settingsKey + "\n" + string.Join("\n", units.Select((u, i) => $"{u.Name}:{string.Join(",", fileHashes[i])}")));
        var record = Path.Combine(cacheDirectory, "builds", Sanitise(Path.GetFileName(executable)) + ".txt");
        if (IsFresh(record, buildKey, executable)) {
            return new(true, [], "fresh", 0, units.Count + 1);
        }

        // ---- Parse, for the target ------------------------------------------
        // Separately, a unit whose .sbi is there is parsed from it: what other
        // units bind and lower against, its bodies blanked. Only a unit then
        // found to need compiling again is parsed from its source.
        var target = settings.Native.TargetOrHost;
        // 'DEBUG' or 'RELEASE' follows from the settings, which key the cache.
        var sources = new ProgramSources(units, texts, file => Pipeline.ParseOptionsFor(!settings.DebugAssertions, file.Options.ForTarget(target.Os, target.Arch)));
        var known = new InterfaceFile?[units.Count];
        using (BuildTimings.Of(timings, "parse")) {
            if (settings.Separate) {
                var interfaces = Path.Combine(cacheDirectory, "interfaces");
                Parallel.For(0, units.Count, GlobalOptions.Current.ParallelOptions, i =>
                    known[i] = InterfaceFile.Read(InterfacePath(interfaces, settingsKey, units[i].Name, fileHashes[i]), units[i].Name, [..units[i].Files.Select(static f => f.Name)]));
            }

            sources.Parse(known);
            timings?.Count("files parsed", sources.Parsed);
            timings?.Count("files parsed from interfaces", sources.FromInterfaces);
        }

        var library = Library.For(sources.Program);

        var outcome = settings.Separate
            ? Separately(sources, known, fileHashes, library, settings, settingsKey, executable, cacheDirectory, timings, log)
            : Whole(sources.Program, settings, executable, cacheDirectory, timings);

        if (outcome.Succeeded) {
            using var _ = BuildTimings.Of(timings, "record");
            Record(record, buildKey, executable);
        }

        return outcome;
    }

    /// <summary>Where a unit's <c>.sbi</c> is kept: under its content and the build's settings.</summary>
    static string InterfacePath(string interfaces, string settingsKey, string unit, IEnumerable<string> fileHashes) =>
        Path.Combine(interfaces, Hash($"{settingsKey}\ninterface {unit}\n{UnitContent(fileHashes)}") + ".sbi");

    static string UnitContent(IEnumerable<string> fileHashes) => Hash(string.Join("\n", fileHashes));

    /// <summary>
    ///     The program's trees, each unit's parsed from its source or, where
    ///     its <c>.sbi</c> was found, from its interface — and from its source
    ///     again once that is needed: to compile the unit, to work its
    ///     interface out, or to compile the program whole.
    /// </summary>
    sealed class ProgramSources(IReadOnlyList<UnitPlan> plans, string[][] texts, Func<SourceFile, ParseOptions> options) {
        public IReadOnlyList<UnitPlan> Plans => plans;

        public SyntaxTree[][] Trees { get; } = [..plans.Select(static p => new SyntaxTree[p.Files.Count])];

        readonly bool[] _fromInterface = new bool[plans.Count];

        public int Parsed;

        public int FromInterfaces => _fromInterface.Select((f, i) => f ? plans[i].Files.Count : 0).Sum();

        public List<SyntaxTree> Program => [..Trees.SelectMany(static t => t)];

        public void Parse(IReadOnlyList<InterfaceFile?> known) {
            var all = plans.SelectMany(static (p, i) => p.Files.Select((f, j) => (i, j, f))).ToList();
            for (var i = 0; i < plans.Count; i++) {
                _fromInterface[i] = known[i] is not null;
            }

            Parallel.ForEach(all, GlobalOptions.Current.ParallelOptions, item => {
                var (i, j, file) = item;
                var text = known[i] is { } face ? face.Files[j].Interface.Source : texts[i][j];
                Trees[i][j] = SyntaxTree.Parse(new SourceText(text, file.Name), options(file));
            });
            Parsed += all.Count;
        }

        /// <summary>A unit's trees parsed from its source, as they are or parsed now.</summary>
        public IReadOnlyList<SyntaxTree> Full(int unit) {
            if (_fromInterface[unit]) {
                var files = plans[unit].Files;
                Parallel.For(0, files.Count, GlobalOptions.Current.ParallelOptions, j =>
                    Trees[unit][j] = SyntaxTree.Parse(new SourceText(texts[unit][j], files[j].Name), options(files[j])));
                Interlocked.Add(ref Parsed, files.Count);
                _fromInterface[unit] = false;
            }

            return Trees[unit];
        }

        /// <summary>The whole program from its sources.</summary>
        public List<SyntaxTree> Whole() {
            for (var i = 0; i < plans.Count; i++) {
                Full(i);
            }

            return Program;
        }
    }

    // =======================================================================
    // An unchanged build
    // =======================================================================

    static bool IsFresh(string record, string buildKey, string executable) {
        if (!File.Exists(record) || !File.Exists(executable)) {
            return false;
        }

        var lines = File.ReadAllLines(record);
        if (lines is not [var key, var length, var written, var hash] || key != buildKey) {
            return false;
        }

        // The executable as it was left: its size and time, or failing those its content.
        var info = new FileInfo(executable);
        if (info.Length.ToString() == length && info.LastWriteTimeUtc.Ticks.ToString() == written) {
            return true;
        }

        return info.Length.ToString() == length && HashFile(executable) == hash;
    }

    static void Record(string record, string buildKey, string executable) {
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        var info = new FileInfo(executable);
        File.WriteAllLines(record, [buildKey, info.Length.ToString(), info.LastWriteTimeUtc.Ticks.ToString(), HashFile(executable)]);
    }

    // =======================================================================
    // The whole program
    // =======================================================================

    static BuildOutcome Whole(IReadOnlyList<SyntaxTree> program, BuildSettings settings, string executable, string cacheDirectory, BuildTimings? timings) {
        var result = Pipeline.Compile(program, settings.DebugDirectory, settings.Exclusivity, timings, optimised: settings.Optimise, target: settings.Native.TargetOrHost, checkOverflow: settings.OverflowChecks, denyReflection: settings.DenyReflection, optLevel: settings.SilLevel, checkArc: settings.ArcChecks, library: settings.Library is not null);
        if (!result.Succeeded) {
            return BuildOutcome.Failed(result.Errors) with { Warnings = result.Warnings };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        NativeBuildResult native;
        using (BuildTimings.Of(timings, "clang")) {
            native = settings.Library is { } kind
                ? NativeBuild.BuildLibrary(result.LlvmIr!, executable, kind, settings.Native, Path.Combine(cacheDirectory, "objects"))
                : NativeBuild.Build(result.LlvmIr!, executable, settings.Native, Path.Combine(cacheDirectory, "objects"));
        }

        return (native.Succeeded
            ? new(true, [], "whole", 1, 0)
            : BuildOutcome.Failed([native.Report])) with { Warnings = result.Warnings };
    }

    // =======================================================================
    // Separately
    // =======================================================================

    sealed class Unit(string name, IReadOnlyList<SyntaxTree> trees, string contentHash) {
        public string Name { get; } = name;
        public IReadOnlyList<SyntaxTree> Trees { get; set; } = trees;
        public string ContentHash { get; } = contentHash;
        public string Key { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string AbiPath => Path.ChangeExtension(ObjectPath, ".abi");
        public bool Cached { get; set; }
    }

    static BuildOutcome Separately(
        ProgramSources sources, IReadOnlyList<InterfaceFile?> known, string[][] fileHashes, IReadOnlyList<SyntaxTree> library, BuildSettings settings,
        string settingsKey, string executable, string cacheDirectory, BuildTimings? timings, TextWriter? log
    ) {
        var objects = Path.Combine(cacheDirectory, "objects");
        Directory.CreateDirectory(objects);

        // The units: the program's, then the standard library's — the core,
        // which every compilation has, and the modules the program imports.
        var own = sources.Trees.Select((t, i) => new Unit(sources.Plans[i].Name, t, UnitContent(fileHashes[i]))).ToList();
        var units = own.ToList();
        var standard = new Unit("std", [..library, ..CoreLibrary.TreesFor(sources.Trees[0][0].Options)], Hash(string.Join("\n", library.Select(static t => t.Text.FileName))));
        units.Add(standard);

        // ---- Each unit's interface ---------------------------------------------
        // Read from the unit's .sbi when the unit is as it was, and so is
        // which of the types its extensions extend the program declares
        // concretely; otherwise worked out from the unit's source and the .sbi written.
        var faces = new FileInterface[own.Count][];
        var interfaces = Path.Combine(cacheDirectory, "interfaces");
        var interfaceFiles = new string[own.Count];
        using (BuildTimings.Of(timings, "interface")) {
            var concrete = InterfaceFingerprint.ConcreteTypeNames(units.SelectMany(static u => u.Trees));
            var read = 0;
            Parallel.For(0, own.Count, GlobalOptions.Current.ParallelOptions, i => {
                var unit = own[i];
                IReadOnlyList<string> extended = [..unit.Trees.SelectMany(InterfaceFingerprint.ExtendedNames).Where(concrete.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                interfaceFiles[i] = InterfacePath(interfaces, settingsKey, unit.Name, fileHashes[i]);
                if (known[i] is { } file && file.Assumed.SequenceEqual(extended)) {
                    faces[i] = [..file.Files.Select(static f => f.Interface)];
                    Interlocked.Increment(ref read);
                    return;
                }

                unit.Trees = sources.Full(i);
                faces[i] = [..unit.Trees.AsParallel().AsOrdered().Select(tree => InterfaceFingerprint.Interface(tree, concrete))];
                new InterfaceFile(unit.Name, extended, [..unit.Trees.Select(static t => t.Text.FileName).Zip(faces[i])]).WriteTo(interfaceFiles[i]);
            });
            timings?.Count("interfaces read", read);
        }

        // ---- Each unit's key -------------------------------------------------
        // Its files, and the interfaces of the units it depends on — not the
        // whole program's (UnitGraph) — and every unit's extensions, which
        // any unit may see. The standard library depends on no unit of the
        // program, and every unit on it.
        using (BuildTimings.Of(timings, "unit graph")) {
            var facts = own.Select((u, i) => {
                var declared = u.Trees.Select(UnitFacts.Of).ToList();
                return new UnitFacts(
                    declared.Select(static d => d.Module).OfType<string>().ToHashSet(StringComparer.Ordinal),
                    declared.SelectMany(static d => d.Imports).ToHashSet(StringComparer.Ordinal),
                    faces[i].SelectMany(static f => f.References).ToHashSet(StringComparer.Ordinal));
            }).ToList();
            var dependencies = UnitGraph.Dependencies(facts);
            var unitInterfaces = own.Select((u, i) => Hash(string.Join("\n", u.Trees.Select((t, j) => t.Text.FileName + "=" + faces[i][j].Fingerprint)))).ToList();
            var extensions = Hash(string.Join("\n", faces.SelectMany(static f => f).Select(static f => f.Extensions)));
            standard.Key = Hash($"{settingsKey}\nunit {standard.Name}\n{standard.ContentHash}\n{extensions}");
            for (var i = 0; i < own.Count; i++) {
                var seen = string.Join("\n", dependencies[i].Select(d => $"{own[d].Name}:{unitInterfaces[d]}"));
                own[i].Key = Hash($"{settingsKey}\nunit {own[i].Name}\n{own[i].ContentHash}\n{extensions}\n{standard.ContentHash}\n{seen}");
            }
        }

        foreach (var unit in units) {
            unit.ObjectPath = Path.Combine(objects, unit.Key + ".o");
            unit.Cached = File.Exists(unit.ObjectPath) && File.Exists(unit.AbiPath);
        }

        var stale = units.Where(static u => !u.Cached).ToList();
        timings?.Count("units reused", units.Count - stale.Count);
        timings?.Count("units compiled", stale.Count);

        // A unit compiled again is checked and lowered from its source; the
        // others are bound from their interfaces, which is all of them that
        // another unit sees.
        using (BuildTimings.Of(timings, "parse (compiled units)")) {
            Parallel.For(0, own.Count, GlobalOptions.Current.ParallelOptions, i => {
                if (!own[i].Cached) {
                    own[i].Trees = sources.Full(i);
                }
            });
        }

        var program = sources.Program;

        // The runtime, as an object of its own.
        var runtime = Path.Combine(objects, "runtime-" + Hash(settingsKey + "\nruntime") + ".o");
        var runtimeTask = File.Exists(runtime)
            ? Task.FromResult(new NativeBuildResult(true, ""))
            : Task.Run(() => NativeObjects.CompileRuntime(runtime, settings.Native));

        var compiling = new List<(Unit Unit, Task<NativeBuildResult> Task)>();
        // '-j' bounds how many clang processes run at once.
        var throttle = new SemaphoreSlim(GlobalOptions.Current.Parallelism);
        List<string> warnings = [];
        if (stale.Count > 0) {
            Compilation compilation;
            List<string> errors;
            using (BuildTimings.Of(timings, "bind")) {
                compilation = Compilation.Create([..program, ..library]);
                compilation.GetDiagnostics();
                var checking = stale.SelectMany(static u => u.Trees).ToHashSet();
                errors = Diagnostics(compilation, program, checking, DiagnosticSeverity.Error);

                // The program's own: the standard library's are not the program's to act on.
                checking.IntersectWith(program);
                warnings = Diagnostics(compilation, program, checking, DiagnosticSeverity.Warning);
            }

            if (errors.Count > 0) {
                return BuildOutcome.Failed(errors) with { Warnings = warnings };
            }

            var index = timings is null ? SilProgram.Index([..program, ..standard.Trees]) : timings.Measure("index", () => SilProgram.Index([..program, ..standard.Trees]));
            foreach (var unit in stale) {
                var owned = unit.Trees.ToHashSet();
                SilModule module;
                IReadOnlyList<Diagnostic> lowering;
                using (BuildTimings.Of(timings, "sil")) {
                    var leads = settings.Library is not null && ReferenceEquals(unit, own[0]);
                    (module, lowering) = SilGen.LowerUnit(compilation, new SilUnit(owned.Contains), index, denyReflection: settings.DenyReflection, library: leads);
                    module.CheckExclusivity = settings.Exclusivity;
                    module.CheckArc = settings.ArcChecks;
                    if (!settings.OverflowChecks) {
                        WrappingArithmetic.Apply(module);
                    }
                }

                // A unit sees less than the whole program: what it reports, the
                // whole program's compilation says authoritatively.
                if (lowering.Count > 0) {
                    log?.WriteLine($"note: unit '{unit.Name}' could not be lowered on its own; compiling the program whole");
                    return Whole(sources.Whole(), settings, executable, cacheDirectory, timings);
                }

                var problems = timings is null ? SilVerifier.Verify(module) : timings.Measure("verify", () => SilVerifier.Verify(module));
                if (problems.Count > 0) {
                    return BuildOutcome.Failed([..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
                }

                // Optimised, the opt-level's SIL passes run on the unit as on
                // a whole program. What they learn about a function from its
                // callees — whether a parameter escapes, whether a field is
                // written — they assume the worst of for a function another
                // unit defines, which this one only declares; the inliner
                // copies in only what the unit has the body of (its own
                // functions, and the instances of generic ones it uses), and
                // devirtualisation leaves a unit alone. Across units, ThinLTO
                // inlines as the objects link.
                if (settings.Optimise) {
                    try {
                        using var _ = BuildTimings.Of(timings, "passes");
                        SilPassManager.Run(module, SilPassManager.ForLevel(settings.SilLevel));
                    } catch (SilPassException failure) {
                        return BuildOutcome.Failed([$"internal error: {failure.Message}"]);
                    }
                }

                string ir;
                using (BuildTimings.Of(timings, "emit")) {
                    ir = settings.DebugDirectory is { } directory
                        ? LlvmEmitter.EmitWithDebugInfo(module, directory, settings.Native.TargetOrHost, settings.Optimise, Pipeline.EmbeddedSources(standard.Trees, program))
                        : LlvmEmitter.Emit(module, settings.Native.TargetOrHost);
                }

                var abi = string.Join("\n", module.Unit!.Abi.Select(static p => $"{p.Key}\t{p.Value}"));
                compiling.Add((unit, Task.Run(async () => {
                    await throttle.WaitAsync();
                    try {
                        var started = System.Diagnostics.Stopwatch.GetTimestamp();
                        File.WriteAllText(unit.AbiPath, abi);
                        var result = NativeObjects.CompileIr(ir, unit.ObjectPath, settings.Native);
                        timings?.Add("clang (units, summed)", System.Diagnostics.Stopwatch.GetElapsedTime(started));
                        return result;
                    } finally {
                        throttle.Release();
                    }
                })));
            }
        }

        using (BuildTimings.Of(timings, "clang (wait)")) {
            Task.WaitAll([..compiling.Select(static c => c.Task), runtimeTask]);
        }

        foreach (var (unit, task) in compiling) {
            if (!task.Result.Succeeded) {
                return BuildOutcome.Failed([$"internal error: compiling unit '{unit.Name}' failed:\n{task.Result.Output}"]);
            }
        }

        if (!runtimeTask.Result.Succeeded) {
            return BuildOutcome.Failed([runtimeTask.Result.Setup ? runtimeTask.Result.Output : $"internal error: compiling the runtime failed:\n{runtimeTask.Result.Output}"]);
        }

        // ---- Every unit must have seen the same types -----------------------
        if ((timings is null ? Disagreement(units) : timings.Measure("abi check", () => Disagreement(units))) is { } disagreement) {
            log?.WriteLine($"note: the units disagree about {disagreement}; compiling the program whole");
            return Whole(sources.Whole(), settings, executable, cacheDirectory, timings);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        NativeBuildResult link;
        using (BuildTimings.Of(timings, "link")) {
            List<string> linked = [..units.Select(static u => u.ObjectPath), runtime];
            link = settings.Library is { } kind
                ? NativeObjects.LinkLibrary(linked, executable, kind, settings.Native, Path.Combine(cacheDirectory, "thinlto"))
                : NativeObjects.Link(linked, executable, settings.Native, Path.Combine(cacheDirectory, "thinlto"));
        }

        if (!link.Succeeded) {
            log?.WriteLine($"note: the units did not link ({FirstLine(link.Output)}); compiling the program whole");
            return Whole(sources.Whole(), settings, executable, cacheDirectory, timings);
        }

        using (BuildTimings.Of(timings, "cache upkeep")) {
            Collect(objects, units);
            CollectInterfaces(interfaces, interfaceFiles);
        }

        return new(true, [], "incremental", stale.Count, units.Count - stale.Count) { Warnings = warnings };
    }

    /// <summary>
    ///     Keeps the object cache from growing without end: each build marks
    ///     the objects it used (through their <c>.abi</c> files — an object's
    ///     own time is in the executable's debug map, and must not change),
    ///     and past a limit the least recently used go.
    /// </summary>
    static void Collect(string objects, IReadOnlyList<Unit> used) {
        var now = DateTime.UtcNow;
        foreach (var unit in used) {
            File.SetLastWriteTimeUtc(unit.AbiPath, now);
        }

        var limit = Math.Max(256, 8 * used.Count);
        var markers = new DirectoryInfo(objects).GetFiles("*.abi");
        if (markers.Length <= limit) {
            return;
        }

        foreach (var marker in markers.OrderBy(static m => m.LastWriteTimeUtc).Take(markers.Length - limit)) {
            File.Delete(Path.ChangeExtension(marker.FullName, ".o"));
            marker.Delete();
        }
    }

    /// <summary>The same for the <c>.sbi</c> files: those this build used are marked, and past a limit the least recently used go.</summary>
    static void CollectInterfaces(string interfaces, IReadOnlyList<string> used) {
        if (!Directory.Exists(interfaces)) {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var path in used.Where(File.Exists)) {
            File.SetLastWriteTimeUtc(path, now);
        }

        var limit = Math.Max(256, 8 * used.Count);
        var files = new DirectoryInfo(interfaces).GetFiles("*.sbi");
        foreach (var file in files.OrderBy(static f => f.LastWriteTimeUtc).Take(Math.Max(0, files.Length - limit))) {
            file.Delete();
        }
    }

    /// <summary>A type two units laid out differently, or null when they all agree.</summary>
    static string? Disagreement(IReadOnlyList<Unit> units) {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in units) {
            foreach (var line in File.ReadAllLines(unit.AbiPath)) {
                var tab = line.IndexOf('\t');
                if (tab < 0) {
                    continue;
                }

                var (type, shape) = (line[..tab], line[(tab + 1)..]);
                if (seen.TryGetValue(type, out var other) && other != shape) {
                    return type;
                }

                seen[type] = shape;
            }
        }

        return null;
    }

    /// <summary>
    ///     The errors of the files being compiled, and the program's
    ///     declarations', each formatted against its file, in program order —
    ///     or, asked for warnings, those of the files being compiled alone.
    /// </summary>
    static List<string> Diagnostics(Compilation compilation, IReadOnlyList<SyntaxTree> program, HashSet<SyntaxTree> checking, DiagnosticSeverity severity) {
        var order = program.Select(static (tree, index) => (tree, index)).ToDictionary(static p => p.tree, static p => p.index);
        var all = new List<(Diagnostic Diagnostic, SyntaxTree? Tree)>();
        var parse = new HashSet<Diagnostic>(ReferenceEqualityComparer.Instance);
        foreach (var tree in compilation.SyntaxTrees.Where(t => severity == DiagnosticSeverity.Error || checking.Contains(t))) {
            all.AddRange(tree.Diagnostics.Select(d => (d, (SyntaxTree?)tree)));
            parse.UnionWith(tree.Diagnostics);
        }

        foreach (var tree in compilation.SyntaxTrees.Where(checking.Contains)) {
            all.AddRange(compilation.GetSemanticModel(tree).GetDiagnostics().Select(d => (d, (SyntaxTree?)tree)));
        }

        // Declarations and signatures: the whole program's.
        all.AddRange(compilation.GetDiagnostics().Where(d => !parse.Contains(d)).Select(static d => (d, (SyntaxTree?)null)));
        return [
            ..all.Where(d => d.Diagnostic.Severity == severity && (severity == DiagnosticSeverity.Error || d.Tree is not null))
                .OrderBy(d => d.Tree is { } t && order.TryGetValue(t, out var index) ? index : int.MaxValue)
                .ThenBy(static d => d.Diagnostic.Span.Start)
                .Select(static d => d.Tree is { } file ? d.Diagnostic.Format(file.Text) : $"error[{d.Diagnostic.Id}]: {d.Diagnostic.Message}"),
        ];
    }

    static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    static string Sanitise(string name) => string.Concat(name.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static string HashFile(string path) {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
