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

    /// <summary>
    ///     The target operating system, as <c>os(...)</c> matches it:
    ///     <c>macos</c>, <c>linux</c>, <c>windows</c> or <c>wasi</c>. The
    ///     machine running the compiler unless a target says otherwise.
    /// </summary>
    public string TargetOs { get; init; } = HostOs;

    /// <summary>The target architecture, as <c>arch(...)</c> matches it: <c>arm64</c>, <c>x86_64</c> or <c>wasm32</c>.</summary>
    public string TargetArch { get; init; } = HostArch;

    /// <summary>The operating system running the compiler, as <c>os(...)</c> names it.</summary>
    public static string HostOs =>
        OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsWindows() ? "windows"
        : "unknown";

    /// <summary>The architecture running the compiler, as <c>arch(...)</c> names it.</summary>
    public static string HostArch =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Wasm => "wasm32",
            var other => other.ToString().ToLowerInvariant(),
        };

    /// <summary>These options for another target: what <c>os(...)</c> and <c>arch(...)</c> test.</summary>
    public ParseOptions ForTarget(string os, string arch) => this with { TargetOs = os, TargetArch = arch };

    /// <summary>Whether two options are for the same target.</summary>
    public bool SameTarget(ParseOptions other) => TargetOs == other.TargetOs && TargetArch == other.TargetArch;

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

    public ParseOptions WithSymbols(params string[] symbols) => this with { Symbols = [..symbols] };

    public ParseOptions WithFeatures(params string[] features) => this with { Features = [..features] };

    public bool IsDefined(string symbol) => Symbols.Contains(symbol);
}
