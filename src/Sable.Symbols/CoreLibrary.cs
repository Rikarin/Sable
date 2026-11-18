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

    /// <summary>The module a file declares (<c>module A.B</c>), or null when it declares none.</summary>
    public static string? ModuleOf(SyntaxTree tree) =>
        tree.Root is CompilationUnitSyntax { Module: { } declaration } ? declaration.Name.ToString().Trim() : null;

    /// <summary>
    ///     True when the files bring a Sable.Core of their own — the compiler's
    ///     repository opened in an editor, its <c>lib/Sable.Core</c> read from
    ///     disk — which then stands in for the embedded one rather than
    ///     declaring everything in it a second time.
    /// </summary>
    public static bool IsBroughtBy(IEnumerable<SyntaxTree> trees) =>
        trees.Any(static t => !Contains(t) && ModuleOf(t) == ModuleName);
}
