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

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Os, string Arch), Lazy<IReadOnlyList<SyntaxTree>>> Parsed = new();

    /// <summary>The core's files, parsed for the machine running the compiler, in name order.</summary>
    public static IReadOnlyList<SyntaxTree> Trees => TreesFor(ParseOptions.Default);

    /// <summary>
    ///     The core's files parsed for the target <paramref name="options" />
    ///     name — what their <c>#if os(...)</c> and <c>#if arch(...)</c> test,
    ///     such as <c>Platform.os</c> — once per target.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> TreesFor(ParseOptions options) =>
        Parsed.GetOrAdd((options.TargetOs, options.TargetArch), static target => new(() => Load(target.Os, target.Arch))).Value;

    static IReadOnlyList<SyntaxTree> Load(string os, string arch) {
        var assembly = typeof(CoreLibrary).Assembly;
        var trees = new List<SyntaxTree>();
        var options = ParseOptions.Default.ForTarget(os, arch);
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(static n => n.StartsWith("lib/Sable.Core/", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            trees.Add(SyntaxTree.Parse(new SourceText(reader.ReadToEnd(), resource), options));
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
    ///     stay. The trees given must be the program's own, none embedded.
    /// </summary>
    public static HashSet<(string Module, string File)> Brought(IEnumerable<SyntaxTree> program) =>
        [..program.Select(static t => (Module: ModuleOf(t), File: FileName(t)))
            .Where(static f => IsStandardLibrary(f.Module))
            .Select(static f => (f.Module!, f.File))];

    /// <summary>True for an embedded file of the library the program brings its own of (<see cref="Brought" />).</summary>
    public static bool IsBrought(SyntaxTree embedded, HashSet<(string Module, string File)> brought) =>
        brought.Count > 0 && ModuleOf(embedded) is { } module && brought.Contains((module, FileName(embedded)));

    /// <summary>A file's name without its directories: <c>Int.sb</c> for <c>lib/Sable.Core/Int.sb</c>.</summary>
    static string FileName(SyntaxTree tree) {
        var name = tree.Text.FileName;
        return name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
    }
}
