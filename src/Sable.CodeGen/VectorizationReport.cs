using System.Text;
using System.Text.RegularExpressions;

namespace Sable.CodeGen;

/// <summary>
///     What LLVM's loop vectoriser made of a program's loops, in Sable's terms:
///     each loop by its place in the source, vectorised or not, and why not.
/// </summary>
/// <remarks>
///     Vectorising is left to LLVM, and much of what Sable checks — overflow,
///     bounds, ARC — can stop it. This reads the remarks clang gives for
///     <c>-Rpass=loop-vectorize</c> and its <c>-missed</c> and <c>-analysis</c>
///     kinds, which carry the source places the debug information gives the
///     IR, so that whether a loop vectorises is something to look at rather
///     than to guess.
/// </remarks>
public static partial class VectorizationReport {
    /// <summary>The flags that make clang say what the vectoriser did.</summary>
    public static IReadOnlyList<string> Flags { get; } =
        ["-Rpass=loop-vectorize", "-Rpass-missed=loop-vectorize", "-Rpass-analysis=loop-vectorize"];

    /// <summary>One loop: where it is, whether it was vectorised, how (width and interleaving), or the reasons it was not.</summary>
    public sealed record Loop(string File, int Line, int Column, bool Vectorized, string? How, IReadOnlyList<Reason> Reasons) {
        public string Place => Line == 0 ? "(no source place)" : $"{File}:{Line}:{Column}";
    }

    /// <summary>Why a loop was not vectorised, and the place in it that LLVM blames.</summary>
    public sealed record Reason(string Place, string Text);

    // clang puts the place first for source it compiled, and after "remark:" for IR it was given.
    [GeneratedRegex(@"^(remark: )?(?<file>[^:]*?):(?<line>\d+):(?<column>\d+): (remark: )?(?<text>.*?) \[-Rpass(?<kind>(-missed|-analysis)?)=loop-vectorize\]\s*$")]
    private static partial Regex Remark();

    /// <summary>
    ///     The loops clang's remarks describe. The analysis remarks for a loop
    ///     come before the remark that says whether it was vectorised, so each
    ///     loop takes those since the one before. A loop inlined into several
    ///     callers is reported once for each; the same report twice is kept once.
    /// </summary>
    public static IReadOnlyList<Loop> Parse(string output) {
        var loops = new List<Loop>();
        var pending = new List<Reason>();
        foreach (var line in output.Split('\n')) {
            if (Remark().Match(line.TrimEnd('\r')) is not { Success: true } match) {
                continue;
            }

            var file = match.Groups["file"].Value;
            var place = int.Parse(match.Groups["line"].Value) == 0 ? "(no source place)" : $"{file}:{match.Groups["line"].Value}:{match.Groups["column"].Value}";
            var text = match.Groups["text"].Value;
            switch (match.Groups["kind"].Value) {
                case "-analysis":
                    pending.Add(new(place, text.StartsWith("loop not vectorized: ", StringComparison.Ordinal) ? text["loop not vectorized: ".Length..] : text));
                    continue;

                case "-missed" when text.StartsWith("loop not vectorized", StringComparison.Ordinal):
                case "" when text.StartsWith("vectorized loop", StringComparison.Ordinal):
                    var vectorized = match.Groups["kind"].Value == "";
                    var how = vectorized && text.IndexOf('(') is >= 0 and var open ? text[(open + 1)..text.LastIndexOf(')')] : null;
                    var loop = new Loop(file, int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value), vectorized, how,
                        vectorized ? [] : pending.Distinct().ToList());
                    if (!loops.Any(l => Same(l, loop))) {
                        loops.Add(loop);
                    }

                    pending.Clear();
                    break;
            }
        }

        return loops;
    }

    static bool Same(Loop a, Loop b) =>
        a.Place == b.Place && a.Vectorized == b.Vectorized && a.How == b.How && a.Reasons.SequenceEqual(b.Reasons);

    /// <summary>The loops in source order, one line each, and below a loop that was not vectorised, its reasons.</summary>
    public static string Format(IReadOnlyList<Loop> loops) {
        var text = new StringBuilder();
        foreach (var loop in loops.OrderBy(static l => l.Line == 0).ThenBy(static l => l.File, StringComparer.Ordinal).ThenBy(static l => l.Line).ThenBy(static l => l.Column)) {
            if (loop.Vectorized) {
                text.Append($"{loop.Place}: vectorized");
                text.Append(loop.How is null ? "\n" : $" ({loop.How})\n");
                continue;
            }

            text.Append($"{loop.Place}: not vectorized\n");
            foreach (var reason in loop.Reasons) {
                text.Append(reason.Place == loop.Place ? $"    {reason.Text}\n" : $"    {reason.Text} (at {reason.Place})\n");
            }
        }

        var vectorized = loops.Count(static l => l.Vectorized);
        text.Append($"{vectorized} of {loops.Count} loop{(loops.Count == 1 ? "" : "s")} vectorized\n");
        return text.ToString();
    }
}
