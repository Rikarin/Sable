using System.Reflection;
using Sable.Driver.Build;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     The standard library (docs/04 §4.3): Sable source in <c>lib/</c>,
///     embedded in the compiler. <c>Sable.Core</c>'s files are part of every
///     compilation, and come with Sable.Symbols; any other module is part of
///     a program that imports it, with whatever that module imports in turn.
/// </summary>
public static class Library {
    public const string Core = CoreLibrary.ModuleName;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Os, string Arch, string Profile), Lazy<IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>>>> Parsed = new();

    /// <summary>The modules parsed for the machine running the compiler.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> Modules => ModulesFor(ParseOptions.Default);

    /// <summary>
    ///     The modules parsed for the target the options name and their build
    ///     profile, once per target and profile: their <c>#if os(...)</c>
    ///     follows the target, and <c>#if DEBUG</c> the profile. Only the files
    ///     that test the profile are parsed for each (<see cref="CoreLibrary.ParseForProfile" />).
    /// </summary>
    static IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> ModulesFor(ParseOptions options) =>
        Parsed.GetOrAdd((options.TargetOs, options.TargetArch, CoreLibrary.ProfileOf(options)), static key => new(() => Load(key.Os, key.Arch, key.Profile))).Value;

    /// <summary>The library modules there are, by name.</summary>
    public static IReadOnlyCollection<string> ModuleNames => [..Modules.Keys];

    /// <summary>A library module's source, every file of it in order: what a cache of anything built from it hashes.</summary>
    public static string Source(string module) =>
        Modules.TryGetValue(module, out var files) ? string.Join("\n", files.Select(static f => f.Text.Text)) : "";

    static IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> Load(string os, string arch, string profile) {
        var modules = new Dictionary<string, List<SyntaxTree>>(StringComparer.Ordinal);
        var assembly = typeof(Library).Assembly;

        // The standard library is a workspace of its own: what it marks
        // 'package' is shared between its modules, not with programs.
        var options = ParseOptions.Default.ForTarget(os, arch) with { Workspace = "sable" };
        var plain = profile.Length > 0
            ? ModulesFor(options).Values.SelectMany(static files => files).ToDictionary(static t => t.Text.FileName, StringComparer.Ordinal)
            : null;
        foreach (var resource in assembly.GetManifestResourceNames().Where(static n => n.StartsWith("lib/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            // Named lib/<Module>/<File>.sb everywhere: built on Windows, the
            // resource's name has MSBuild's '\' between module and file.
            var fileName = resource.Replace('\\', '/');
            var tree = CoreLibrary.ParseForProfile(
                reader.ReadToEnd(),
                fileName,
                options,
                profile,
                plain is not null && plain.TryGetValue(fileName, out var shared) ? () => shared : null
            );
            if (tree.Root is not CompilationUnitSyntax { Module: { } declaration }) {
                continue;
            }

            var name = NameOf(declaration.Name);
            if (!modules.TryGetValue(name, out var files)) {
                modules[name] = files = [];
            }

            files.Add(tree);
        }

        return modules.ToDictionary(static m => m.Key, static m => (IReadOnlyList<SyntaxTree>)m.Value, StringComparer.Ordinal);
    }

    /// <summary>
    ///     The library's trees a program needs: every module it imports,
    ///     transitively. The core is not among them: every compilation has it
    ///     already (<see cref="CoreLibrary" />).
    /// </summary>
    public static IReadOnlyList<SyntaxTree> For(SyntaxTree program) => For([program]);

    /// <summary>
    ///     The library's trees a program of many files needs, parsed for the
    ///     target its files are. A file of the library the program has of its
    ///     own — <c>sable test --doc lib/Sable.Text</c>, read from disk —
    ///     stands in for the embedded file of its name, as one of Sable.Core
    ///     does (<see cref="CoreLibrary.Brought" />), and the rest of its
    ///     module comes with it.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> For(IReadOnlyList<SyntaxTree> program) {
        var modules = program.Count > 0 ? ModulesFor(program[0].Options) : Modules;
        var brought = Brought(program);
        var needed = new List<SyntaxTree>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { Core };
        var pending = new Queue<string>([..program.SelectMany(ImportsOf), ..brought.Select(static b => b.Module)]);
        while (pending.TryDequeue(out var module)) {
            if (!seen.Add(module) || !modules.TryGetValue(module, out var files)) {
                continue;
            }

            foreach (var file in files.Where(f => !CoreLibrary.IsBrought(f, brought))) {
                needed.Add(file);
                foreach (var imported in ImportsOf(file)) {
                    pending.Enqueue(imported);
                }
            }
        }

        return needed;
    }

    /// <summary>The library files the program brings its own of (<see cref="CoreLibrary.Brought" />), against the embedded modules.</summary>
    public static HashSet<(string Module, string File)> Brought(IReadOnlyList<SyntaxTree> program) {
        var modules = program.Count > 0 ? ModulesFor(program[0].Options) : Modules;
        var core = program.Count > 0 ? CoreLibrary.TreesFor(program[0].Options) : CoreLibrary.Trees;
        return CoreLibrary.Brought(
            program.Where(t => !IsEmbedded(t, modules)),
            m => m == Core ? core : modules.TryGetValue(m, out var files) ? files : []
        );
    }

    // =======================================================================
    // The library, checked once
    // =======================================================================

    /// <summary>
    ///     What is known of the standard library's files of <paramref name="compilation" />
    ///     for a program that cannot change how they bind or lower
    ///     (<see cref="Compilation.ReachesIntoLibrary" />); null for any other
    ///     program, whose compilation checks and lowers the library whole.
    /// </summary>
    public static LibraryCheck? CheckFor(Compilation compilation, IReadOnlyCollection<SyntaxTree> own) {
        if (own.Count == 0 || Environment.GetEnvironmentVariable("SABLE_CHECK_LIBRARY") is { Length: > 0 }) {
            return null;
        }

        var ownSet = own.ToHashSet();
        var options = own.First().Options;
        var modules = ModulesFor(options);
        var library = compilation.SyntaxTrees.Where(t => !ownSet.Contains(t)).ToList();
        if (library.Count == 0 || !library.All(t => IsEmbedded(t, modules)) || compilation.ReachesIntoLibrary(ownSet)) {
            return null;
        }

        // In the process, by the trees themselves, parsed once and kept; on
        // disk, by what they are parsed from: their names and the target and
        // profile, under the compiler's own hash, whose assemblies carry them.
        var key = $"{string.Join(",", library.Select(IdOf).Order())}|{compilation.BindOptions}";
        var lasting = CompilerHash.Value is { } compiler
            ? IncrementalBuild.Hash($"sable-library-checked-1\n{compiler}\n{options.TargetOs}\n{options.TargetArch}\n{CoreLibrary.ProfileOf(options)}\n{string.Join("\n", library.Select(static t => t.Text.FileName).Order(StringComparer.Ordinal))}\n{compilation.BindOptions}")
            : null;
        return new(library, key, lasting);
    }

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> CheckedTrees = new(StringComparer.Ordinal);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxTree, object> Ids = new();

    static long _lastId;

    /// <summary>A number for a tree, its own for as long as the tree lives: the library's are parsed once and kept.</summary>
    static long IdOf(SyntaxTree tree) => (long)Ids.GetValue(tree, static _ => Interlocked.Increment(ref _lastId));

    /// <summary>
    ///     A hash of the compiler's assemblies — which carry the library's
    ///     source — or null when they cannot be read (a single-file build),
    ///     and the library is then only remembered in the process.
    /// </summary>
    static readonly Lazy<string?> CompilerHash = new(static () => {
        try {
            var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (var assembly in new[] { typeof(Library), typeof(SilGen), typeof(Compilation), typeof(CoreLibrary), typeof(SyntaxTree) }.Select(static t => t.Assembly).Distinct()) {
                if (assembly.Location is not { Length: > 0 } location || !File.Exists(location)) {
                    return null;
                }

                hash.AppendData(File.ReadAllBytes(location));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        } catch (IOException) {
            return null;
        }
    });

    /// <summary>Where it is written down that a library was checked, under <c>$SABLE_HOME</c>.</summary>
    internal static string CheckedPath => Path.Combine(CodeGen.NativeBuild.SableHome, "cache", "library-checked");

    static bool IsEmbedded(SyntaxTree tree, IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> modules) =>
        CoreLibrary.Contains(tree) || CoreLibrary.ModuleOf(tree) is { } module && modules.TryGetValue(module, out var files) && files.Contains(tree);

    static IEnumerable<string> ImportsOf(SyntaxTree tree) =>
        tree.Root is CompilationUnitSyntax unit ? unit.Imports.Select(static i => NameOf(i.Name)) : [];

    static string NameOf(SyntaxNode name) => name.ToString().Trim();
}

/// <summary>
///     Whether a program's standard library was found to check cleanly —
///     its every body bound, and every function lowered and verified,
///     without a word — with a program that could not change any of it
///     (<see cref="Compilation.ReachesIntoLibrary" />), so with any other
///     such program too. Once it is known, a build checks
///     only the program's own bodies, and lowers only the library functions
///     it reaches (<see cref="SilGen.LowerByFile" />'s <c>onDemand</c>): a
///     small program binds a few of the library's files, not every one.
/// </summary>
/// <remarks>
///     It is learnt from a build that checked the library whole, and kept for
///     the process — by the library's trees, which are parsed once and never
///     change — and in <c>$SABLE_HOME/cache/library-checked</c>, under a
///     hash of the compiler, so that the next process need not learn it again.
///     With <c>SABLE_CHECK_LIBRARY</c> set, nothing is known, and every
///     program has the library checked with it, as it was before: what a
///     suspected difference is compared against.
/// </remarks>
public sealed class LibraryCheck {
    readonly HashSet<SyntaxTree> _library;
    readonly string _key;
    readonly string? _lasting;

    internal LibraryCheck(IEnumerable<SyntaxTree> library, string key, string? lasting) {
        _library = [..library];
        _key = key;
        _lasting = lasting;
    }

    /// <summary>The library's trees, whose bodies are left alone, when it is known to check cleanly; null when it is not known yet.</summary>
    public Func<SyntaxTree, bool>? Skipped => IsKnown() ? _library.Contains : null;

    bool IsKnown() {
        if (Library.CheckedTrees.TryGetValue(_key, out var known)) {
            return known;
        }

        if (_lasting is not null && File.Exists(Path.Combine(Library.CheckedPath, _lasting))) {
            Library.CheckedTrees[_key] = true;
            return true;
        }

        return false;
    }

    /// <summary>A build that checked and lowered the library whole with the program succeeded: so the library checks cleanly.</summary>
    public void Passed() {
        Library.CheckedTrees[_key] = true;
        if (_lasting is null) {
            return;
        }

        try {
            Directory.CreateDirectory(Library.CheckedPath);
            File.WriteAllText(Path.Combine(Library.CheckedPath, _lasting), "");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Kept for the process only: the next one learns it again.
        }
    }
}

