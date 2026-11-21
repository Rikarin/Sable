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
    ///     What is known of the library files of <paramref name="compilation" />
    ///     — the standard library's, and every other file that is not
    ///     <paramref name="own" />: a dependency's, which the language server
    ///     compiles with a package — for a program that cannot change how they
    ///     bind or lower (<see cref="Compilation.ReachesIntoLibrary" />); null
    ///     for any other program, whose compilation checks and lowers them
    ///     whole.
    /// </summary>
    public static LibraryCheck? CheckFor(Compilation compilation, IReadOnlyCollection<SyntaxTree> own) {
        if (own.Count == 0 || Environment.GetEnvironmentVariable("SABLE_CHECK_LIBRARY") is { Length: > 0 }) {
            return null;
        }

        var ownSet = own.ToHashSet();
        var options = own.First().Options;
        var modules = ModulesFor(options);
        var library = compilation.SyntaxTrees.Where(t => !ownSet.Contains(t)).ToList();
        if (library.Count == 0 || compilation.ReachesIntoLibrary(ownSet)) {
            return null;
        }

        // The standard library's files in the process by the trees
        // themselves, parsed once and kept, and on disk by what they are
        // parsed from — their names and the target and profile — under the
        // compiler's own hash, whose assemblies carry them. Any other file,
        // a dependency's, by its name, features and text: a dependency
        // edited, or another version of it, is another library.
        var others = library.Where(t => !IsEmbedded(t, modules)).Select(ContentOf).Order(StringComparer.Ordinal).ToList();
        var embedded = library.Where(t => IsEmbedded(t, modules)).ToList();
        var key = $"{string.Join(",", embedded.Select(IdOf).Order())}|{string.Join(",", others)}|{compilation.BindOptions}";
        return new(library, key, LastingKey(embedded, others, options, compilation.BindOptions));
    }

    /// <summary>A file that is not the standard library's, as a record of a library checked knows it: its name, features and text.</summary>
    static string ContentOf(SyntaxTree tree) =>
        $"{tree.Text.FileName}:{string.Join(",", tree.Options.Features.Order(StringComparer.Ordinal))}:{IncrementalBuild.Hash(tree.Text.Text)}";

    /// <summary>
    ///     What <c>$SABLE_HOME/cache/library-checked</c> records a library as:
    ///     what the standard library's files are parsed from — their names,
    ///     the target and profile — any other files' contents
    ///     (<see cref="ContentOf" />), and the binding options, under the
    ///     compiler's identity (<see cref="CodeGen.CompilerIdentity" />), whose
    ///     assemblies carry the standard library's files: a single-file build
    ///     of the compiler has one too.
    /// </summary>
    static string LastingKey(IEnumerable<SyntaxTree> library, IReadOnlyList<string> others, ParseOptions options, BindOptions bindOptions) =>
        IncrementalBuild.Hash($"sable-library-checked-2\n{CodeGen.CompilerIdentity.Current}\n{options.TargetOs}\n{options.TargetArch}\n{CoreLibrary.ProfileOf(options)}\n{string.Join("\n", library.Select(static t => t.Text.FileName).Order(StringComparer.Ordinal))}\n{bindOptions}\n{SIL.SilGen.Generics}"
            + (others.Count == 0 ? "" : $"\nother files\n{string.Join("\n", others)}"));

    /// <summary>
    ///     Records that the standard library's <paramref name="library" /> —
    ///     Sable.Core and the modules a program imported, parsed for
    ///     <paramref name="options" /> — checks cleanly, learnt otherwise than
    ///     by <see cref="LibraryCheck.Passed" />: its unit is in the build
    ///     cache, its <c>.sbi</c> beside the archive, compiled with a program
    ///     that extends nothing — an entry written only once every body of it
    ///     bound and lowered without a word. A fresh process — <c>sable
    ///     check</c>, a build of one file, the language server — then checks
    ///     none of it again. Only embedded files are recorded: a program
    ///     bringing its own copy of one has changed the library.
    /// </summary>
    /// <param name="cache">The build cache the entry is in, beside which the record goes: <c>$SABLE_HOME/cache</c>'s, unless a test's.</param>
    public static void MarkChecked(IReadOnlyList<SyntaxTree> library, ParseOptions options, string cache) {
        if (library.Count == 0 || Environment.GetEnvironmentVariable("SABLE_CHECK_LIBRARY") is { Length: > 0 }) {
            return;
        }

        var modules = ModulesFor(options);
        if (!library.All(t => IsEmbedded(t, modules))) {
            return;
        }

        new LibraryCheck(library, $"{string.Join(",", library.Select(IdOf).Order())}||{BindOptions.Default}", LastingKey(library, [], options, BindOptions.Default)).Passed(Path.Combine(cache, CheckedDirectory));
    }

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> CheckedTrees = new(StringComparer.Ordinal);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxTree, object> Ids = new();

    static long _lastId;

    /// <summary>A number for a tree, its own for as long as the tree lives: the library's are parsed once and kept.</summary>
    static long IdOf(SyntaxTree tree) => (long)Ids.GetValue(tree, static _ => Interlocked.Increment(ref _lastId));

    /// <summary>Where it is written down that a library was checked, under <c>$SABLE_HOME</c>.</summary>
    internal static string CheckedPath => Path.Combine(CodeGen.NativeBuild.SableHome, "cache", CheckedDirectory);

    /// <summary>The directory of the build cache the records are in.</summary>
    public const string CheckedDirectory = "library-checked";

    /// <summary>
    ///     Where this compiler's records are among <paramref name="directory" />'s:
    ///     a directory of its identity's, so that <c>sable cache prune</c>
    ///     can tell another compiler's records from its own.
    /// </summary>
    public static string RecordsOf(string directory) => Path.Combine(directory, CodeGen.CompilerIdentity.Current);

    /// <summary>
    ///     <c>sable cache clean</c>: removes every record that a library
    ///     checks cleanly, in the cache at <paramref name="cache" />.
    ///     Returns the paths removed.
    /// </summary>
    public static IReadOnlyList<string> CleanChecked(string cache) => RemoveChecked(cache, static _ => true);

    /// <summary>
    ///     <c>sable cache prune</c>: removes the records another build of the
    ///     compiler wrote, which this one never reads. Returns the paths removed.
    /// </summary>
    public static IReadOnlyList<string> PruneChecked(string cache) =>
        RemoveChecked(cache, static path => Path.GetFileName(path) != CodeGen.CompilerIdentity.Current);

    static IReadOnlyList<string> RemoveChecked(string cache, Func<string, bool> removes) {
        var directory = Path.Combine(cache, CheckedDirectory);
        if (!Directory.Exists(directory)) {
            return [];
        }

        // Each compiler's directory, and what an earlier layout left at the top.
        var removed = new List<string>();
        foreach (var path in Directory.GetFileSystemEntries(directory).Where(removes)) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                } else {
                    File.Delete(path);
                }

                removed.Add(path);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // In use, or gone already: the next prune tries again.
            }
        }

        if (removed.Count > 0 && Directory.GetFileSystemEntries(directory).Length == 0) {
            Directory.Delete(directory);
        }

        return removed;
    }

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
///     It is learnt from a build that checked the library whole, or that
///     compiled the library's unit into the build cache, its <c>.sbi</c>
///     beside its archive (<see cref="Library.MarkChecked" />), and kept for
///     the process — by the library's trees, which are parsed once and never
///     change — and in <c>$SABLE_HOME/cache/library-checked</c>, under the
///     compiler's identity, so that the next process need not learn it again:
///     <c>sable check</c> and the language server read it too.
///     With <c>SABLE_CHECK_LIBRARY</c> set, nothing is known, and every
///     program has the library checked with it, as it was before: what a
///     suspected difference is compared against.
/// </remarks>
public sealed class LibraryCheck {
    readonly HashSet<SyntaxTree> _library;
    readonly string _key;
    readonly string _lasting;

    internal LibraryCheck(IEnumerable<SyntaxTree> library, string key, string lasting) {
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

        if (File.Exists(Path.Combine(Library.RecordsOf(Library.CheckedPath), _lasting))) {
            Library.CheckedTrees[_key] = true;
            return true;
        }

        return false;
    }

    /// <summary>A build that checked and lowered the library whole with the program succeeded: so the library checks cleanly.</summary>
    /// <param name="directory">Where the record goes: <see cref="Library.CheckedPath" /> unless said.</param>
    public void Passed(string? directory = null) {
        Library.CheckedTrees[_key] = true;
        directory = Library.RecordsOf(directory ?? Library.CheckedPath);
        try {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, _lasting), "");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Kept for the process only: the next one learns it again.
        }
    }
}

