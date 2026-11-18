// Lists every type error and every construct SIL lowering does not take yet,
// in tests/corpus and examples: what stands between the corpus and docs/04 §2.5.
// dotnet run --project tools/lowering-probe -c Release [-- <repository root>]

using Sable.Syntax; using Sable.Symbols; using Sable.SIL; using Sable.Driver;
var root = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
foreach (var file in new[] { "tests/corpus", "examples" }.SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.sab")).Order()) {
    var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(file), Path.GetFileName(file)));
    var compilation = Compilation.Create([tree, ..Library.For(tree)]);
    foreach (var d in compilation.GetAllDiagnosticsByFile().Where(d => d.Tree == tree && d.Diagnostic.Severity == DiagnosticSeverity.Error))
        Console.WriteLine($"BIND {Path.GetFileName(file)}:{tree.Text.GetLinePosition(d.Diagnostic.Span.Start).Line} {d.Diagnostic.Id} {d.Diagnostic.Message}");
    var (module, diags) = SilGen.Lower(compilation);
    foreach (var d in diags) Console.WriteLine($"LOWER {Path.GetFileName(file)}:{(d.Span.End <= tree.Text.Length ? tree.Text.GetLinePosition(d.Span.Start).Line : 0)} {d.Message}");
}
