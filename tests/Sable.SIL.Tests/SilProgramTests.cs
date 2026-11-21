namespace Sable.SIL.Tests;

/// <summary>
///     Lowering's index of what another unit can see is built from the
///     compilation's symbols, not by walking the other files' syntax
///     (docs/05 §5.4, #182): it must find exactly what that walk found, in the
///     same order, or units would name and lower their functions differently.
/// </summary>
public sealed class SilProgramTests {
    /// <summary>What the index used to be: each file's declarations outside bodies, by walking its members' syntax.</summary>
    static List<SyntaxNode> SyntaxOutline(SyntaxNode node) {
        var declarations = new List<SyntaxNode>();
        Walk(node);
        return declarations;

        void Walk(SyntaxNode at) {
            IEnumerable<SyntaxNode> members = at switch {
                CompilationUnitSyntax unit => unit.Members,
                StructDeclarationSyntax s => s.Members,
                ClassDeclarationSyntax c => c.Members,
                ActorDeclarationSyntax a => a.Members,
                ProtocolDeclarationSyntax p => p.Members,
                UnionDeclarationSyntax u => u.Members,
                EnumDeclarationSyntax e => e.Members,
                ExtensionDeclarationSyntax x => x.Members,
                FieldDeclarationSyntax { Accessors: { } accessors } => accessors.Accessors,
                SubscriptDeclarationSyntax { Accessors: { } accessors } => accessors.Accessors,
                _ => [],
            };
            foreach (var member in members) {
                if (SilProgram.IsDeclaration(member)) {
                    declarations.Add(member);
                }

                Walk(member);
            }
        }
    }

    static void AssertSameOutline(Compilation compilation) {
        var index = SilProgram.Index(compilation);
        foreach (var tree in compilation.SyntaxTrees) {
            var expected = SyntaxOutline(tree.Root);
            var actual = index.Outline(tree);
            Assert.True(
                expected.SequenceEqual(actual, ReferenceEqualityComparer.Instance),
                $"{tree.Text.FileName}: the symbols' outline differs from the syntax's\n"
                + $"missing: {string.Join(", ", expected.Except(actual, ReferenceEqualityComparer.Instance).Cast<SyntaxNode>().Take(5).Select(static n => n.ToString().Trim().Split('\n')[0]))}\n"
                + $"extra: {string.Join(", ", actual.Except(expected, ReferenceEqualityComparer.Instance).Cast<SyntaxNode>().Take(5).Select(static n => n.ToString().Trim().Split('\n')[0]))}");
        }
    }

    [Fact]
    public void TheCoreLibrarysOutlineComesFromItsSymbols() =>
        AssertSameOutline(Compilation.Create([SyntaxTree.Parse(new SourceText("module M\nfn main() { }", "m.sb"))]));

    [Fact]
    public void AProgramsOutlineComesFromItsSymbols() =>
        AssertSameOutline(Compilation.Create([
            SyntaxTree.Parse(new SourceText("""
                module M

                struct Point {
                    var x: Int
                    var y: Int
                    var sum: Int { get { x + y } }
                    lazy var cached: Int = 3
                    subscript(i: Int) -> Int { get { i } set { } }
                    init(x: Int, y: Int) { self.x = x; self.y = y }
                }

                class Shape {
                    var sides: Int = 0
                    deinit { }
                    class Inner { fn f() { } }
                }

                extension Point {
                    fn twice() -> Int {
                        fn local() -> Int { 2 }
                        return sum * local()
                    }
                }

                extension Int {
                    fn doubled() -> Int { self * 2 }
                }

                protocol Named {
                    fn name() -> String
                }

                union Shape2 {
                    case circle(Int)
                    fn area() -> Int { 0 }
                }

                const limit: Int = 4
                var counter = 0
                fn main() { }
                """, "m.sb")),
        ]));
}
