namespace Sable.Symbols.Tests;

/// <summary>
///     <c>extension&lt;Element&gt; Element[&gt;..] { … }</c>: members for every
///     array of a kind (Compilation.ArrayExtensions.cs), which is how
///     lib/Sable.Core/Array.sb declares the array family's.
/// </summary>
public sealed class ArrayExtensionTests {
    static Compilation Compile(string source) => Compilation.Create("module M\n" + source);

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compile(source)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static void AssertError(string source, string id, string fragment) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Any(e => e.Id == id && e.Message.Contains(fragment, StringComparison.Ordinal)),
            $"expected {id} with '{fragment}', got:\n" + string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    // ---- The core's: declared in Sable ---------------------------------------

    [Fact]
    public void TheArrayMembersAreSableCoresNotTheCompilers() {
        var compilation = Compile("fn f() { }");
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        var members = model.GetMembersOf(new ArrayTypeSymbol(compilation.Core.Int, ArrayKind.Grow));

        foreach (var name in (string[])["count", "isEmpty", "contains", "slice", "append", "removeLast", "clear", "sort", "firstIndex"]) {
            Assert.Contains(members, m => m.Name == name);
            foreach (var member in members.Where(m => m.Name == name)) {
                Assert.False(member.DeclaringSyntax.IsEmpty, $"'{name}' has no declaration");
                var extension = Assert.IsType<ExtensionSymbol>(member.ContainingSymbol);
                Assert.NotNull(extension.ExtendedType.ExtendedArray);
                Assert.Same(compilation.Core.Module, extension.ExtendedType.ContainingSymbol);
            }
        }

        // 'append(element:)' is the builtin; 'append(contentsOf:)' is Sable over it.
        Assert.Equal("array_count", Assert.IsType<FieldSymbol>(members.Single(m => m.Name == "count")).Builtin);
        Assert.Equal([null, "array_append"], members.Where(m => m.Name == "append").Select(static m => ((FunctionSymbol)m).Builtin).Order());
        Assert.Null(Assert.IsType<FunctionSymbol>(members.Single(m => m.Name == "contains")).Builtin);
    }

    [Fact]
    public void EachKindHasItsOwnAndEverySlicesMembers() {
        var compilation = Compile("fn f() { }");
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        string[] Names(ArrayKind kind, TypeSymbol? element = null) =>
            [..model.GetMembersOf(new ArrayTypeSymbol(element ?? compilation.Core.Int, kind)).Select(static m => m.Name).Order(StringComparer.Ordinal)];

        // A slice's members are every kind's; the growable kinds add their
        // own, and 'T[>..]' and 'T[>..<]' have the same (docs/03 §3.9).
        var everyArray = Names(ArrayKind.Slice);
        foreach (var name in (string[])["contains", "count", "isEmpty", "slice", "first", "firstIndex", "sort", "sorted", "swapAt", "chunks", "toArray"]) {
            Assert.Contains(name, everyArray);
        }

        Assert.Equal(everyArray, Names(ArrayKind.Fixed));
        Assert.Equal(Names(ArrayKind.Grow), Names(ArrayKind.GrowShrink));
        Assert.Equal(
            ["append", "append", "capacity", "clear", "removeLast", "reserveCapacity"],
            Names(ArrayKind.Grow).Where(n => !everyArray.Contains(n))
        );
        foreach (var name in (string[])["append", "clear", "removeLast"]) {
            Assert.Contains(name, Names(ArrayKind.Inline));
        }

        // Nothing inserts or removes in the middle of an array: that is List's.
        Assert.DoesNotContain("insert", Names(ArrayKind.Grow));
        Assert.DoesNotContain("remove", Names(ArrayKind.Grow));

        // Bytes read as text; and 'contains' is only where the element is Equatable.
        Assert.Contains("toString", Names(ArrayKind.Grow, compilation.Core.U8));
        Assert.DoesNotContain("toString", Names(ArrayKind.Grow));
        Assert.DoesNotContain("contains", Names(ArrayKind.Grow, compilation.Core.Region));
    }

    [Fact]
    public void AMemberIsTheElementsType() {
        AssertClean(
            """
            fn f(_ names: String[>..]) -> String {
                var copy = names
                let last: String = copy.removeLast()
                let part: String[:] = names.slice(from: 0, to: 1)
                if names.contains("a") && !part.isEmpty {
                    return last
                }
                copy.append("b")
                copy.append(element: "c")
                return "\(names.count)"
            }
            """
        );

        AssertError(
            """
            fn f(_ xs: Int[>..]) -> String {
                var ys = xs
                return ys.removeLast()
            }
            """,
            "S3004",
            "expected 'String'"
        );
        AssertError("fn f(_ xs: Int[>..]) -> Bool { return xs.contains(\"one\") }", "S3007", "cannot be called");
    }

    [Fact]
    public void TheGrowingMembersAreMutating() {
        AssertError("fn f() { let xs: Int[>..] = []\n xs.append(1) }", "S3041", "'append' is mutating");
        AssertError("fn f(_ xs: Int[>..<]) { xs.clear() }", "S3041", "'clear' is mutating");
        AssertClean("fn f(_ xs: inout Int[>..]) { xs.append(1) }");
        AssertClean("struct Bag { var items: Int[>..]\n mutating fn add(_ i: Int) { items.append(i) } }");
        AssertError("struct Bag { var items: Int[>..]\n fn add(_ i: Int) { items.append(i) } }", "S3041", "'append' is mutating");
    }

    [Fact]
    public void ContainsNeedsAnEquatableElement() {
        AssertClean("fn f(_ xs: Int[], _ cs: Char[:]) -> Bool { return xs.contains(1) || cs.contains('a') }");
        AssertError(
            "struct P { var x: Int }\nfn f(_ ps: P[>..], _ p: P) -> Bool { return ps.contains(p) }",
            "S3023",
            "'P[>..]' has 'contains' only where 'Element: Equatable', and 'P' does not conform"
        );
    }

    // ---- A program's own ---------------------------------------------------------

    [Fact]
    public void SelfIsTheArray() {
        AssertClean(
            """
            extension<T> T[>..] {
                fn me() -> T[>..] { return self }
                fn first() -> T { return self[0] }
                fn doubleCount() -> Int { return count * 2 }
                fn emptyToo() -> Bool { return isEmpty && self.isEmpty }
                mutating fn push(_ item: T) { append(item); self.append(item) }
            }
            fn f(_ xs: Int[>..]) -> Int {
                var ys = xs.me()
                ys.push(1)
                return ys.first() + ys.doubleCount()
            }
            """
        );

        // Self is a grow-only array: another kind is not it, though a slice
        // views any kind (SYNTAX.md §11).
        AssertError("extension<T> T[>..] { fn me() -> T[>..<] { return self } }", "S3004", "T[>..<]");
        AssertClean("extension<T> T[>..] { fn me() -> T[:] { return self } }");
        AssertError("extension<T> T[>..] { fn grow(_ t: T) { self.append(t) } }", "S3041", "'append' is mutating");
    }

    [Fact]
    public void AConstraintIsTheExtensionsAlone() {
        AssertClean(
            """
            extension<T: Comparable> T[:] {
                fn largest() -> T {
                    var best = self[0]
                    for x in self { if best < x { best = x } }
                    return best
                }
            }
            extension<T> T[:] where T: Hashable {
                fn hashes() -> Int[>..] {
                    var all: Int[>..] = []
                    for x in self { all.append(x.hashValue) }
                    return all
                }
            }
            fn f(_ xs: Int[>..], _ ss: String[]) -> Int { return xs.largest() + ss.hashes().count }
            """
        );

        // 'count' is unconstrained, so it says nothing about T.
        AssertError(
            "extension<T> T[:] { fn bad(_ a: T, _ b: T) -> Bool { return self.count > 0 && a < b } }",
            "S3009",
            "'<' is not defined for 'T'"
        );
        AssertError(
            "struct P { var x: Int }\nextension<T> T[:] where T: Hashable { fn h() -> Int { return 0 } }\nfn f(_ ps: P[]) -> Int { return ps.h() }",
            "S3023",
            "'T: Hashable'"
        );
    }

    [Fact]
    public void AConcreteElementExtendsThatArrayAlone() {
        AssertClean(
            """
            struct P { var x: Int }
            extension P[>..] { fn xs() -> Int { var t = 0
             for p in self { t = t + p.x }
             return t } }
            fn f(_ ps: P[>..]) -> Int { return ps.xs() }
            """
        );
        AssertError(
            "struct P { var x: Int }\nextension P[>..] { fn xs() -> Int { return 0 } }\nfn f(_ qs: Int[>..]) -> Int { return qs.xs() }",
            "S2002",
            "has no member named 'xs'"
        );
    }

    [Fact]
    public void AnElementMayBeAPattern() =>
        AssertClean(
            """
            extension<K, V> (K, V)[>..] {
                fn keys() -> K[>..] {
                    var all: K[>..] = []
                    for pair in self { all.append(pair.0) }
                    return all
                }
            }
            extension<T> T?[:] {
                fn present() -> Int {
                    var n = 0
                    for x in self { if x != none { n = n + 1 } }
                    return n
                }
            }
            fn f(_ pairs: (String, Int)[>..], _ maybes: Int?[]) -> Int {
                let ks: String[>..] = pairs.keys()
                return ks.count + maybes.present()
            }
            """
        );

    // ---- Misuse ------------------------------------------------------------------

    [Theory]
    [InlineData("extension<T> Int[>..] { }", "S3083", "'T' is not in 'Int[>..]'")]
    [InlineData("extension<T> T[8] { }", "S3083", "every size")]
    [InlineData("extension<T> T[..4] { }", "S3083", "every size")]
    [InlineData("extension<T> T[>..]: Equatable { }", "S3083", "not conformances")]
    [InlineData("struct Box<T> { var t: T }\nextension<T> Box<T> { }", "S3083", "'extension Box<T>'")]
    [InlineData("extension<T> (T, T) { }", "S3031", "array type")]
    [InlineData("extension<T> T[:] { var n: Int = 0 }", "S3029", "stored properties")]
    [InlineData("extension<T> T[:] { init() { } }", "S3030", "initialiser")]
    [InlineData("extension<T> T[:] { @builtin(\"array_count\") var n: Int { get } }", "S3081", "only Sable.Core")]
    public void MisuseIsReported(string source, string id, string fragment) => AssertError(source, id, fragment);

    [Fact]
    public void AnExtensionInABodyIsRejected() =>
        AssertError("fn f() {\n extension<T> T[>..] { }\n}", "S3050", "file scope");
}
