using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>Sable.Core</c>'s source, <c>lib/Sable.Core/*.sab</c>, embedded in
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
}
