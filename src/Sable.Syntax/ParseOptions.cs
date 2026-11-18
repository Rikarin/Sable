using System.Collections.Immutable;

namespace Sable.Syntax;

/// <summary>
///     The configuration a source file is lexed and parsed under: which
///     conditional-compilation symbols are defined, and what the target is.
/// </summary>
/// <remarks>
///     Two parses of the same file under different options produce different
///     trees, because inactive <c>#if</c> regions become
///     <see cref="SyntaxKind.DisabledTextTrivia" /> rather than tokens. The text
///     still round-trips either way.
/// </remarks>
public sealed record ParseOptions {
    public static readonly ParseOptions Default = new();

    /// <summary>Symbols defined for <c>#if</c>, e.g. DEBUG or RELEASE.</summary>
    public ImmutableHashSet<string> Symbols { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    /// <summary>The target operating system, as <c>os(...)</c> matches it.</summary>
    public string TargetOs { get; init; } = "macos";

    /// <summary>The target architecture, as <c>arch(...)</c> matches it.</summary>
    public string TargetArch { get; init; } = "arm64";

    /// <summary>Package features enabled, as <c>feature(...)</c> matches them.</summary>
    public ImmutableHashSet<string> Features { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    /// <summary>
    ///     The workspace the file belongs to, which is how far <c>package</c>
    ///     reaches (spec/SYNTAX.md §2): null for the workspace being built,
    ///     otherwise a name for one outside it — a registry dependency, or the
    ///     standard library.
    /// </summary>
    public string? Workspace { get; init; }

    /// <summary>
    ///     The package the file belongs to, when a build knows it: a module is
    ///     one package's (spec/SYNTAX.md §1), so two packages declaring the
    ///     same one is an error rather than one module of both.
    /// </summary>
    public string? Package { get; init; }

    /// <summary>
    ///     The attributes the package's source generators answer to (docs/08):
    ///     each may be written on a type, as the compiler's own are.
    /// </summary>
    public ImmutableHashSet<string> GeneratorAttributes { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    /// <summary>The attributes those generators read on a type's fields and cases.</summary>
    public ImmutableHashSet<string> GeneratorMemberAttributes { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    public ParseOptions WithSymbols(params string[] symbols) => this with { Symbols = [..symbols] };

    public ParseOptions WithFeatures(params string[] features) => this with { Features = [..features] };

    public bool IsDefined(string symbol) => Symbols.Contains(symbol);
}
