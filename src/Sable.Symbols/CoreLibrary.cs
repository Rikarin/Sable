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

    static readonly Lazy<IReadOnlyList<SyntaxTree>> Parsed = new(Load);

    /// <summary>The core's files, parsed, in name order.</summary>
    public static IReadOnlyList<SyntaxTree> Trees => Parsed.Value;

    static IReadOnlyList<SyntaxTree> Load() {
        var assembly = typeof(CoreLibrary).Assembly;
        var trees = new List<SyntaxTree>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(static n => n.StartsWith("lib/Sable.Core/", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            trees.Add(SyntaxTree.Parse(new SourceText(reader.ReadToEnd(), resource)));
        }

        return trees;
    }

    /// <summary>True for one of the core's own trees.</summary>
    public static bool Contains(SyntaxTree tree) => Trees.Contains(tree);

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
