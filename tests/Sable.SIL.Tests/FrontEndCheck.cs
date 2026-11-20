using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL.Tests;

/// <summary>
///     What the front end owes any input, checked: one source through the
///     parser, the binder and type checker, constant evaluation and, for what
///     type-checks, lowering to SIL, the verifier and the passes, the printed
///     SIL read back, async lowering and the optimising passes.
/// </summary>
/// <remarks>
///     Shared by the mutation fuzzer (<see cref="CompilerFuzzTests" />) and the
///     coverage-guided one (tools/fuzz/Sable.Fuzz, which compiles this file in),
///     so that both hold the compiler to the same thing: every stage returns,
///     with diagnostics or with none, and never throws; and a program that
///     type-checks and lowers with nothing to say lowers to SIL the verifier
///     accepts, before and after the passes, that prints as text that reads
///     back as itself, and that async lowering and the optimising passes
///     keep verified. A broken promise is thrown as an
///     <see cref="InvalidOperationException" />; whatever the compiler itself
///     throws is let through.
/// </remarks>
public static class FrontEndCheck {
    /// <summary>
    ///     Checks one source, and sums up what it reached as features: the
    ///     syntax kinds in its tree, the diagnostic codes reported, and how far
    ///     down the pipeline it got (<c>stage:type-checked</c>,
    ///     <c>stage:lowered</c>, <c>stage:verified</c>,
    ///     <c>stage:round-tripped</c>, <c>stage:async-lowered</c>,
    ///     <c>stage:optimised</c>).
    /// </summary>
    public static HashSet<string> Compile(string source) {
        var features = new HashSet<string>(StringComparer.Ordinal);
        var tree = SyntaxTree.Parse(new SourceText(source, "fuzz.sb"));
        if (tree.ToFullString() != source) {
            throw new InvalidOperationException("the tree does not give back its text");
        }

        foreach (var node in tree.Root.DescendantNodes()) {
            features.Add(node.Kind.ToString());
        }

        // The source's own diagnostics first: its bodies, and the program's
        // declarations and signatures (Sable.Core's among them, which the
        // source can extend). Sable.Core's bodies are most of the program,
        // and binding them costs many times what the source's do, so they are
        // bound only for a source that has no error of its own and goes on:
        // a source with one stops here either way.
        var compilation = Compilation.Create([tree]);
        var model = compilation.GetSemanticModel(tree);
        var own = new List<Diagnostic>(model.GetDiagnostics());
        own.AddRange(compilation.GetDiagnostics());
        foreach (var diagnostic in own) {
            features.Add(diagnostic.Id);
        }

        // The semantic model is what the editor asks, of broken code above all.
        foreach (var node in tree.Root.DescendantNodes()) {
            model.GetSymbolOf(node);
        }

        if (own.Any(static d => d.Severity == DiagnosticSeverity.Error)) {
            return features;
        }

        // Everything, Sable.Core's bodies too, as a build would check it
        // before lowering: what the source declares can reach into them.
        var diagnostics = compilation.GetAllDiagnostics();
        foreach (var diagnostic in diagnostics) {
            features.Add(diagnostic.Id);
        }

        if (diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)) {
            return features;
        }

        features.Add("stage:type-checked");
        foreach (var (diagnostic, _) in SilGen.EvaluateConstants(compilation)) {
            features.Add("const:" + diagnostic.Id);
        }

        var (module, lowering) = SilGen.Lower(compilation);
        foreach (var diagnostic in lowering) {
            features.Add("sil:" + diagnostic.Id);
        }

        if (lowering.Count > 0) {
            return features;
        }

        features.Add("stage:lowered");
        if (SilVerifier.Verify(module) is { Count: > 0 } before) {
            throw new InvalidOperationException("lowering made SIL the verifier rejects:\n" + string.Join("\n", before));
        }

        // As lowered, for the optimising passes below: read back from its
        // text, which is a copy of the module the printer and the parser
        // must agree on.
        var lowered = SilPrinter.Print(module);

        SilPassManager.Run(module, SilPassManager.Default);
        if (SilVerifier.Verify(module) is { Count: > 0 } after) {
            throw new InvalidOperationException("the passes made SIL the verifier rejects:\n" + string.Join("\n", after));
        }

        features.Add("stage:verified");

        // The printed SIL reads back as itself: what a .sil test is written
        // in, and what '--emit sil' shows.
        RoundTrip(module, "after the passes");
        features.Add("stage:round-tripped");

        // Async functions become coroutines and generators' frames are
        // spilled before code is generated or interpreted: the module must
        // still verify, and still print and read back.
        AsyncLowering.Run(module);
        if (SilVerifier.Verify(module) is { Count: > 0 } coroutines) {
            throw new InvalidOperationException("async lowering made SIL the verifier rejects:\n" + string.Join("\n", coroutines));
        }

        RoundTrip(module, "after async lowering");
        features.Add("stage:async-lowered");

        // What an optimised build runs — devirtualising, inlining,
        // simplifying, loop versioning — on the module as lowered.
        var optimised = SilParser.Parse(lowered);
        SilPassManager.Run(optimised, SilPassManager.Optimising);
        if (SilVerifier.Verify(optimised) is { Count: > 0 } optimising) {
            throw new InvalidOperationException("the optimising passes made SIL the verifier rejects:\n" + string.Join("\n", optimising));
        }

        features.Add("stage:optimised");
        return features;
    }

    /// <summary>The module's text reads back as a module that prints as the same text.</summary>
    static void RoundTrip(SilModule module, string when) {
        var printed = SilPrinter.Print(module);
        SilModule reparsed;
        try {
            reparsed = SilParser.Parse(printed);
        } catch (SilParseException failure) {
            throw new InvalidOperationException($"the SIL printed {when} does not read back: {failure.Message}\n{Around(printed, failure.Line)}");
        }

        var again = SilPrinter.Print(reparsed);
        if (again != printed) {
            var lines = printed.Split('\n');
            var other = again.Split('\n');
            var at = Enumerable.Range(0, Math.Min(lines.Length, other.Length)).FirstOrDefault(i => lines[i] != other[i], Math.Min(lines.Length, other.Length));
            throw new InvalidOperationException($"the SIL printed {when} reads back as different SIL, from line {at + 1}:\n"
                + $"printed:  {(at < lines.Length ? lines[at] : "(the end)")}\nread back: {(at < other.Length ? other[at] : "(the end)")}");
        }
    }

    /// <summary>A few lines of the text around a line, numbered.</summary>
    static string Around(string text, int line) =>
        string.Join("\n", text.Split('\n').Select(static (l, i) => (Line: l, Number: i + 1)).Where(l => Math.Abs(l.Number - line) <= 3).Select(static l => $"{l.Number,6}: {l.Line}"));
}
