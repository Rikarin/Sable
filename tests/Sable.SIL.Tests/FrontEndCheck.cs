using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL.Tests;

/// <summary>
///     What the front end owes any input, checked: one source through the
///     parser, the binder and type checker, constant evaluation and, for what
///     type-checks, lowering to SIL, the verifier and the passes.
/// </summary>
/// <remarks>
///     Shared by the mutation fuzzer (<see cref="CompilerFuzzTests" />) and the
///     coverage-guided one (tools/fuzz/Sable.Fuzz, which compiles this file in),
///     so that both hold the compiler to the same thing: every stage returns,
///     with diagnostics or with none, and never throws; and a program that
///     type-checks and lowers with nothing to say lowers to SIL the verifier
///     accepts, before and after the passes. A broken promise is thrown as an
///     <see cref="InvalidOperationException" />; whatever the compiler itself
///     throws is let through.
/// </remarks>
public static class FrontEndCheck {
    /// <summary>
    ///     Checks one source, and sums up what it reached as features: the
    ///     syntax kinds in its tree, the diagnostic codes reported, and how far
    ///     down the pipeline it got (<c>stage:type-checked</c>,
    ///     <c>stage:lowered</c>, <c>stage:verified</c>).
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

        var compilation = Compilation.Create([tree]);
        var diagnostics = compilation.GetAllDiagnostics();
        foreach (var diagnostic in diagnostics) {
            features.Add(diagnostic.Id);
        }

        // The semantic model is what the editor asks, of broken code above all.
        var model = compilation.GetSemanticModel(tree);
        foreach (var node in tree.Root.DescendantNodes()) {
            model.GetSymbolOf(node);
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

        SilPassManager.Run(module, SilPassManager.Default);
        if (SilVerifier.Verify(module) is { Count: > 0 } after) {
            throw new InvalidOperationException("the passes made SIL the verifier rejects:\n" + string.Join("\n", after));
        }

        features.Add("stage:verified");
        return features;
    }
}
