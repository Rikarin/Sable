using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>Sable.Core</c>'s source, <c>lib/Sable.Core/*.sb</c>, embedded in
///     this assembly and part of every compilation.
/// </summary>
/// <remarks>
///     <para>
///         The primitive types are the compiler's (<see cref="CoreTypes" />),
///         but their members — a string's <c>count</c>, a number's <c>abs</c>,
///         <c>print</c>, the <c>Runtime</c> calls — are declared here, in Sable,
///         most of them over <c>@builtin</c>s. Nothing can be type-checked
///         without them, so they come with every compilation rather than with
///         the driver's library: a test that binds three lines sees the same
///         <c>String</c> a program does.
///     </para>
///     <para>
///         The files are parsed once per process and their trees shared:
///         syntax trees are immutable and safe to read from several
///         compilations, and on several threads, at once.
///     </para>
/// </remarks>
public static class CoreLibrary {
    public const string ModuleName = "Sable.Core";

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Os, string Arch, string Profile), Lazy<IReadOnlyList<SyntaxTree>>> Parsed = new();

    /// <summary>The core's files, parsed for the machine running the compiler, in name order.</summary>
    public static IReadOnlyList<SyntaxTree> Trees => TreesFor(ParseOptions.Default);

    /// <summary>
    ///     The core's files parsed for the target <paramref name="options" />
    ///     name — what their <c>#if os(...)</c> and <c>#if arch(...)</c> test,
    ///     such as <c>Platform.os</c> — and for its build profile, what
    ///     <c>#if DEBUG</c> and <c>#if RELEASE</c> test, such as
    ///     <c>assert</c>: once per target and profile.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> TreesFor(ParseOptions options) =>
        Parsed.GetOrAdd((options.TargetOs, options.TargetArch, ProfileOf(options)), static key => new(() => Load(key.Os, key.Arch, key.Profile))).Value;

    /// <summary>
    ///     The build profile the options define, as the library's
    ///     <c>#if</c> sees it: <c>DEBUG</c>, <c>RELEASE</c>, or neither (a
    ///     compilation that is not a build, such as a test of the binder's).
    /// </summary>
    public static string ProfileOf(ParseOptions options) =>
        options.IsDefined("DEBUG") ? "DEBUG" : options.IsDefined("RELEASE") ? "RELEASE" : "";

    /// <summary>
    ///     Library source parsed for a profile: a file that never names it
    ///     reads the same under every profile, so it is the tree parsed
    ///     without one, shared; only a file that says <c>DEBUG</c> or
    ///     <c>RELEASE</c> is parsed again. A second profile costs a few
    ///     trees, not the library.
    /// </summary>
    public static SyntaxTree ParseForProfile(string text, string fileName, ParseOptions plain, string profile, Func<SyntaxTree>? shared) =>
        profile.Length > 0 && shared is not null && !text.Contains("DEBUG", StringComparison.Ordinal) && !text.Contains("RELEASE", StringComparison.Ordinal)
            ? shared()
            : SyntaxTree.Parse(new SourceText(text, fileName), profile.Length > 0 ? plain.WithSymbols(profile) : plain);

    static IReadOnlyList<SyntaxTree> Load(string os, string arch, string profile) {
        var assembly = typeof(CoreLibrary).Assembly;
        var trees = new List<SyntaxTree>();
        // The standard library is one workspace, with the modules beside this one (the driver's Library).
        var options = ParseOptions.Default.ForTarget(os, arch) with { Workspace = "sable" };
        var plain = profile.Length > 0 ? TreesFor(options) : null;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(static n => n.StartsWith("lib/Sable.Core/", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var index = trees.Count;
            trees.Add(ParseForProfile(reader.ReadToEnd(), resource, options, profile, plain is null ? null : () => plain[index]));
        }

        return trees;
    }

    /// <summary>True for one of the core's own trees, parsed for any target.</summary>
    public static bool Contains(SyntaxTree tree) =>
        tree.Text.FileName.StartsWith("lib/Sable.Core/", StringComparison.Ordinal) && TreesFor(tree.Options).Contains(tree);

    /// <summary>
    ///     True for a module of the standard library: <c>Sable.Core</c> and
    ///     the others of <c>lib/</c>, every one named <c>Sable.*</c>, a
    ///     namespace programs leave to it. What only the library may do
    ///     (<c>@builtin</c>) is allowed by this name, as Sable.Core's own
    ///     files are recognised by theirs (<see cref="IsBroughtBy" />).
    /// </summary>
    public static bool IsStandardLibrary(string? module) =>
        module is not null && (module == ModuleName || module.StartsWith("Sable.", StringComparison.Ordinal));

    /// <summary>The module a file declares (<c>module A.B</c>), or null when it declares none.</summary>
    public static string? ModuleOf(SyntaxTree tree) =>
        tree.Root is CompilationUnitSyntax { Module: { } declaration } ? declaration.Name.ToString().Trim() : null;

    /// <summary>
    ///     The files of the standard library a program brings of its own, by
    ///     module and file name: the compiler's repository opened in an
    ///     editor, its <c>lib/</c> read from disk, or <c>sable test --doc
    ///     lib/Sable.Core/Int.sb</c>. Each stands in for the embedded file of
    ///     its module and name (<see cref="IsBrought" />) rather than
    ///     declaring everything in it a second time; the module's other files
    ///     stay. A module none of whose files the program has under an
    ///     embedded file's name is one of its own, standing in for the
    ///     embedded module whole (a test's small Sable.Core). The trees given
    ///     must be the program's own, none embedded; <paramref name="embedded" />
    ///     gives a module's embedded files (none for one there is not).
    /// </summary>
    public static HashSet<(string Module, string File)> Brought(IEnumerable<SyntaxTree> program, Func<string, IEnumerable<SyntaxTree>> embedded) {
        var brought = program.Select(static t => (Module: ModuleOf(t), File: FileName(t)))
            .Where(static f => IsStandardLibrary(f.Module))
            .Select(static f => (f.Module!, f.File))
            .ToHashSet();
        foreach (var module in brought.Select(static b => b.Item1).Distinct().ToList()) {
            var names = embedded(module).Select(FileName).ToHashSet(StringComparer.Ordinal);
            if (!brought.Any(b => b.Item1 == module && names.Contains(b.Item2))) {
                brought.Add((module, Whole));
            }
        }

        return brought;
    }

    /// <summary>The file name <see cref="Brought" /> gives for a module brought whole.</summary>
    const string Whole = "*";

    /// <summary>True for an embedded file of the library the program brings its own of (<see cref="Brought" />).</summary>
    public static bool IsBrought(SyntaxTree embedded, HashSet<(string Module, string File)> brought) =>
        brought.Count > 0 && ModuleOf(embedded) is { } module && (brought.Contains((module, FileName(embedded))) || brought.Contains((module, Whole)));

    /// <summary>A file's name without its directories: <c>Int.sb</c> for <c>lib/Sable.Core/Int.sb</c>.</summary>
    static string FileName(SyntaxTree tree) {
        var name = tree.Text.FileName;
        return name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
    }
}
