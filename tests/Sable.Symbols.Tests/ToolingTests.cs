namespace Sable.Symbols.Tests;

/// <summary>
///     The semantic model as an editor uses it (SemanticModel.Tooling.cs):
///     one member bound at a time, a member's new text bound against the
///     compilation it was edited from, the names in scope at a position,
///     what a value offers after a dot, and every reference to a symbol.
/// </summary>
public sealed class ToolingTests {
    const string Source = """
                          module M

                          /// A point in the plane.
                          public struct Point {
                              public var x: Int
                              public var y: Int

                              public fn sum() -> Int {
                                  return x + y
                              }

                              public fn scaled(by factor: Int) -> Point {
                                  let doubled = factor * 2
                                  return Point { x: x * doubled, y: y * doubled }
                              }
                          }

                          public class Base {
                              public fn greet() -> String => "hi"
                          }

                          public class Derived: Base {
                              public fn shout() -> String => greet()
                          }

                          fn use(_ p: Point, _ d: Derived) -> Int {
                              let total = p.sum()
                              for i in 0..<3 {
                                  let inner = i + total
                                  print(inner)
                              }
                              let later = 1
                              return total + later
                          }
                          """;

    static (Compilation Compilation, SyntaxTree Tree, SemanticModel Model) Compile(string source = Source) {
        var compilation = Compilation.Create(source);
        var tree = compilation.SyntaxTrees[0];
        return (compilation, tree, compilation.GetSemanticModel(tree));
    }

    static int At(SyntaxTree tree, string marker, int offset = 0) {
        var index = tree.Text.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"no '{marker}' in the source");
        return index + offset;
    }

    static IReadOnlyList<string> Names(IEnumerable<Symbol> symbols) => [..symbols.Select(static s => s.Name)];

    [Fact]
    public void FindMemberAnswersWithTheOutermostBindableDeclaration() {
        var (_, tree, _) = Compile();
        var member = SemanticModel.FindMember(tree.Root, At(tree, "doubled = factor"));
        Assert.IsType<FunctionDeclarationSyntax>(member);
        Assert.Equal("scaled", ((FunctionDeclarationSyntax)member!).Identifier.Text);
        Assert.Null(SemanticModel.FindMember(tree.Root, At(tree, "public struct Point")));
    }

    [Fact]
    public void FindTokenFindsTheNameEndingAtTheCaret() {
        var (_, tree, _) = Compile();
        var position = At(tree, "doubled = factor", "doubled".Length);
        Assert.Equal("doubled", SemanticModel.FindToken(tree.Root, position).Text);
        Assert.Equal("doubled", SemanticModel.FindToken(tree.Root, position - 3).Text);
    }

    [Fact]
    public void AMemberModelBindsOnlyItsMember() {
        var (_, tree, model) = Compile();
        var member = SemanticModel.FindMember(tree.Root, At(tree, "let total"))!;
        var memberModel = model.GetMemberModel(member);
        Assert.Same(memberModel, model.GetMemberModel(member));

        var call = tree.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.ToString().Trim() == "p.sum()");
        Assert.Equal("Int", memberModel.GetTypeOf(call)?.ToDisplayString());

        // Another member's expressions are not bound by it.
        var product = tree.Root.DescendantNodes().OfType<BinaryExpressionSyntax>().First(b => b.ToString() == "factor * 2");
        Assert.Null(memberModel.GetTypeOf(product));
    }

    [Fact]
    public void LookupSymbolsSeesLocalsDeclaredBeforeThePositionAndNothingAfter() {
        var (_, tree, model) = Compile();
        var names = Names(model.LookupSymbols(At(tree, "print(inner)")));

        Assert.Contains("inner", names);
        Assert.Contains("i", names);
        Assert.Contains("total", names);
        Assert.Contains("p", names);
        Assert.DoesNotContain("later", names);
        Assert.DoesNotContain("doubled", names);

        // Then the module, and the core.
        Assert.Contains("Point", names);
        Assert.Contains("use", names);
        Assert.Contains("print", names);

        // Nearest first.
        Assert.True(names.ToList().IndexOf("inner") < names.ToList().IndexOf("Point"));
    }

    [Fact]
    public void LookupSymbolsInAMethodSeesTheTypesMembers() {
        var (_, tree, model) = Compile();
        var names = Names(model.LookupSymbols(At(tree, "let doubled")));
        Assert.Contains("factor", names);
        Assert.Contains("x", names);
        Assert.Contains("sum", names);
        Assert.DoesNotContain("doubled", names);

        var inherited = Names(model.LookupSymbols(At(tree, "=> greet()", 3)));
        Assert.Contains("greet", inherited);
        Assert.Contains("shout", inherited);
    }

    [Fact]
    public void LookupSymbolsInAnEmptyLineOfABlock() {
        var source = Source.Replace("    let later = 1\n", "    let later = 1\n    \n", StringComparison.Ordinal);
        var (_, tree, model) = Compile(source);
        var names = Names(model.LookupSymbols(At(tree, "let later = 1\n    ", "let later = 1\n    ".Length)));
        Assert.Contains("later", names);
        Assert.Contains("total", names);
        Assert.DoesNotContain("inner", names);
    }

    [Fact]
    public void GetMembersOfIncludesInheritedMembers() {
        var (compilation, _, model) = Compile();
        var derived = compilation.GetTypeByQualifiedName("M.Derived")!;
        var names = Names(model.GetMembersOf(derived));
        Assert.Contains("shout", names);
        Assert.Contains("greet", names);

        var point = compilation.GetTypeByQualifiedName("M.Point")!;
        Assert.Contains("x", Names(model.GetMembersOf(point)));
        Assert.Contains("count", Names(model.GetMembersOf(compilation.Core.String)));
    }

    [Fact]
    public void GetSymbolAtResolvesUsesDeclarationsCallsAndTypeNames() {
        var (_, tree, model) = Compile();

        Assert.Equal(SymbolKind.Function, model.GetSymbolAt(At(tree, "sum()\n", 1))?.Kind);
        var sum = model.GetSymbolAt(At(tree, "p.sum()", 3));
        Assert.Equal("sum", sum?.Name);
        Assert.IsType<FunctionSymbol>(sum);

        var type = model.GetSymbolAt(At(tree, "_ p: Point", 6));
        Assert.Equal("Point", type?.Name);
        Assert.IsType<NamedTypeSymbol>(type);

        var local = model.GetSymbolAt(At(tree, "total + later", 1));
        Assert.Equal(SymbolKind.Local, local?.Kind);

        var parameter = model.GetSymbolAt(At(tree, "factor: Int", 1));
        Assert.Equal(SymbolKind.Parameter, parameter?.Kind);

        var field = model.GetSymbolAt(At(tree, "return x + y", 7));
        Assert.Equal(SymbolKind.Field, field?.Kind);

        var inherited = model.GetSymbolAt(At(tree, "=> greet()", 4));
        Assert.Equal("greet", inherited?.Name);
    }

    [Fact]
    public void GetSymbolAtResolvesImplicitMemberAccesses() {
        var (_, tree, model) = Compile(
            """
            module M

            enum Kind {
                Automatic
                Decimal
            }

            struct Spec {
                public var kind: Kind = .Automatic
            }

            fn pick(_ k: Kind) -> Kind {
                let other: Kind = .Decimal
                if k == .Automatic {
                    return other
                }
                return .Decimal
            }

            fn code(_ k: Kind) -> Int {
                return match k {
                    case .Automatic => 0
                    case _ => if k == Kind.Decimal { 1 } else { 2 }
                }
            }
            """
        );

        var automatic = model.GetSymbolAt(At(tree, ".Automatic\n", 2));
        Assert.Equal("Automatic", automatic?.Name);
        Assert.False(automatic!.DeclaringSyntax.IsEmpty);

        var decimalCase = model.GetSymbolAt(At(tree, ".Decimal\n", 2));
        Assert.Equal("Decimal", decimalCase?.Name);

        Assert.Same(automatic, model.GetSymbolAt(At(tree, ".Automatic {", 2)));
        Assert.Same(automatic, model.GetSymbolAt(At(tree, ".Automatic =>", 2)));
        Assert.Same(decimalCase, model.GetSymbolAt(At(tree, "Kind.Decimal", 6)));

        Assert.Same(automatic, model.GetSymbolAt(At(tree, "Automatic\n    Decimal", 1)));
        // Its declaration, the default, the comparison and the pattern.
        Assert.Equal(4, model.FindReferences(automatic).Count);
    }

    [Fact]
    public void FindReferencesFindsTheDeclarationAndEveryUse() {
        var (_, tree, model) = Compile();
        var total = model.GetSymbolAt(At(tree, "let total", 5))!;
        var spans = model.FindReferences(total);
        Assert.Equal(3, spans.Count);
        Assert.All(spans, s => Assert.Equal("total", tree.Text.ToString(s)));

        var point = model.GetSymbolAt(At(tree, "struct Point", 8))!;
        Assert.Equal(4, model.FindReferences(point).Count);

        // A field: its declaration, its two uses, and the label that initialises it.
        var x = model.GetSymbolAt(At(tree, "var x", 4))!;
        Assert.Equal(SymbolKind.Field, x.Kind);
        Assert.Equal(4, model.FindReferences(x).Count);
        Assert.Same(x, model.GetSymbolAt(At(tree, "{ x: x", 2)));

        var doubled = model.GetSymbolAt(At(tree, "let doubled", 5))!;
        Assert.Equal(3, model.FindReferences(doubled).Count);
    }

    [Fact]
    public void FindReferencesWorksAcrossFiles() {
        var compilation = Compilation.Create(
            "module M\npublic fn helper() -> Int => 1\n",
            "module M\nfn a() -> Int => helper() + helper()\n"
        );
        var first = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        var second = compilation.GetSemanticModel(compilation.SyntaxTrees[1]);
        var helper = first.GetSymbolAt(compilation.SyntaxTrees[0].Text.Text.IndexOf("helper", StringComparison.Ordinal))!;
        Assert.Single(first.FindReferences(helper));
        Assert.Equal(2, second.FindReferences(helper).Count);
    }

    [Fact]
    public void ASpeculativeModelBindsNewTextAgainstTheOldSymbols() {
        var (_, tree, model) = Compile();
        var position = At(tree, "let later = 1");
        var edited = tree.WithChange(TextChange.Insert(position, "let fresh = p.sum() * 2\n    "));
        var original = SemanticModel.FindMember(tree.Root, position)!;
        var replacement = SemanticModel.FindMember(edited.Root, position)!;

        var caret = edited.Text.Text.IndexOf("let later", StringComparison.Ordinal);
        var speculative = model.GetSpeculativeModel(original, replacement, caret);
        Assert.Contains("fresh", Names(speculative.LookupSymbols(caret)));

        var multiply = edited.Root.DescendantNodes().OfType<BinaryExpressionSyntax>().First(b => b.ToString() == "p.sum() * 2");
        Assert.Equal("Int", speculative.GetTypeOf(multiply)?.ToDisplayString());

        // The symbol is the compilation's own.
        var call = edited.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.ToString().Trim() == "p.sum()");
        var sum = speculative.GetSymbolOf(call);
        Assert.Same(model.GetSymbolAt(At(tree, "fn sum", 3)), sum);
    }

    [Fact]
    public void BindingAMemberTwiceDeclaresItsLocalTypesOnce() {
        var (compilation, tree, model) = Compile(
            """
            module M
            fn f() -> Int {
                struct Local { var v: Int }
                let l = Local { v: 1 }
                return l.v
            }
            """
        );
        var member = SemanticModel.FindMember(tree.Root, At(tree, "return l.v"))!;
        _ = model.GetMemberModel(member).GetDiagnostics();
        _ = model.LookupSymbols(At(tree, "return l.v"));
        Assert.Empty(model.GetDiagnostics());
        Assert.Empty(compilation.GetAllDiagnostics());
    }
}
