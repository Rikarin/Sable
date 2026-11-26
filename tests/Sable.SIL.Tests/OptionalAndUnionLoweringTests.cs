namespace Sable.SIL.Tests;

/// <summary>
///     Optionals and structural unions as programs compute them: promotion
///     that takes a step first, unions that normalise, chains through
///     optional members, and the operators that compare them.
/// </summary>
public sealed class OptionalAndUnionLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));

        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + SilPrinter.Print(module));

        var text = SilPrinter.Print(module);
        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));

        SilPassManager.Run(reparsed, SilPassManager.Default);
        return reparsed;
    }

    static object? Run(SilModule module, string function, params object[] arguments) {
        var interpreter = new SilInterpreter(module);
        var result = interpreter.Run($"M.{function}", arguments);
        Assert.True(interpreter.LiveObjects == 0, $"{interpreter.LiveObjects} object(s) leaked");
        return result;
    }

    // ---- Promotion that takes a step first (#145) ---------------------------

    [Fact]
    public void AnArrayLiteralTakesItsKindThenWraps() {
        var module = Lower(
            """
            fn count() -> Int {
                let xs: Int[>..]? = [9, 8]
                if let ys = xs { return ys.count }
                return -1
            }
            """
        );

        Assert.Equal(2L, Run(module, "count"));
    }

    [Fact]
    public void AValueJoinsTheUnionThenWraps() {
        var module = Lower(
            """
            fn which(_ pick: Bool) -> Int {
                let u: (Int | String)? = pick ? none : "x"
                if let v = u {
                    if v is String { return 2 }
                    return 1
                }
                return 0
            }
            fn passed() -> Int { return take("x") }
            fn take(_ u: (Int | String)?) -> Int {
                guard let v = u else { return 0 }
                return v is String ? 2 : 1
            }
            """
        );

        Assert.Equal(2L, Run(module, "which", false));
        Assert.Equal(0L, Run(module, "which", true));
        Assert.Equal(2L, Run(module, "passed"));
    }

    [Fact]
    public void AnOptionalTupleIsTakenApartWhereItIsUnwrapped() {
        // #262: 'if let (a, _) = t' binds what the pattern names, in 'if',
        // 'guard' and 'while', as 'let (a, _) = t' and 'for (k, v) in d' do.
        var module = Lower(
            """
            fn pair(_ give: Bool) -> (Int, String)? {
                return give ? (7, "seven") : none
            }
            fn first(_ give: Bool) -> Int {
                if let (number, _) = pair(give) {
                    return number
                }
                return -1
            }
            fn both(_ give: Bool) -> Int {
                guard var (n, word) = pair(give), n > 0 else {
                    return -1
                }
                n += word.count
                return n
            }
            fn products() -> Int {
                let items: (Int, Int)[>..] = [(1, 2), (3, 4)]
                var it = items.makeIterator()
                var total = 0
                while let (a, b) = it.next() {
                    total += a * b
                }
                return total
            }
            """
        );

        Assert.Equal(7L, Run(module, "first", true));
        Assert.Equal(-1L, Run(module, "first", false));
        Assert.Equal(12L, Run(module, "both", true));
        Assert.Equal(-1L, Run(module, "both", false));
        Assert.Equal(14L, Run(module, "products"));
    }

    [Fact]
    public void APlainAsGivesItsOperandTheType() {
        var module = Lower(
            """
            fn nothing() -> Int { return (none as Int?) ?? 7 }
            fn some() -> Int { return (3 as Int?) ?? 7 }
            fn array() -> Int { return ([1, 2] as Int[>..]?)?.count ?? 0 }
            """
        );

        Assert.Equal(7L, Run(module, "nothing"));
        Assert.Equal(3L, Run(module, "some"));
        Assert.Equal(2L, Run(module, "array"));
    }

    // ---- Unions (#146) ------------------------------------------------------

    [Fact]
    public void AnIfThatLeavesNarrowsToWhatIsLeft() {
        var module = Lower(
            """
            struct Vec3 { var x: Int, var y: Int }
            fn render(_ v: Int | String | Vec3) -> Int {
                if v is Int { return v }
                if v is String { return v.count }
                return v.x + v.y
            }
            fn rest(_ v: Int | String | Vec3) -> Int {
                if v is Int { return 0 }
                let w: String | Vec3 = v
                if w is String { return w.count * 10 }
                return 2
            }
            fn a() -> Int { return render(7) }
            fn b() -> Int { return render("four") }
            fn c() -> Int { return render(Vec3 { x: 1, y: 2 }) }
            fn d() -> Int { return rest("abc") }
            fn e() -> Int { return rest(Vec3 { x: 1, y: 2 }) }
            fn f() -> Int { return rest(5) }
            fn last(_ v: F64 | String) -> Int {
                if v is F64 { return 1 }
                if v is String { return v.count }
                return 3
            }
            fn g() -> Int { return last("hello") }
            """
        );

        Assert.Equal(5L, Run(module, "g"));

        Assert.Equal(7L, Run(module, "a"));
        Assert.Equal(4L, Run(module, "b"));
        Assert.Equal(3L, Run(module, "c"));
        Assert.Equal(30L, Run(module, "d"));
        Assert.Equal(2L, Run(module, "e"));
        Assert.Equal(0L, Run(module, "f"));
    }

    [Fact]
    public void NoneInAUnionIsItsAbsence() {
        var module = Lower(
            """
            fn f(_ x: Int | String | None) -> Int {
                guard let y = x else { return 0 }
                if y is Int { return y }
                return -1
            }
            fn a() -> Int { return f(none) }
            fn b() -> Int { return f(4) }
            fn c() -> Int { return f("s") }
            """
        );

        Assert.Equal(0L, Run(module, "a"));
        Assert.Equal(4L, Run(module, "b"));
        Assert.Equal(-1L, Run(module, "c"));
    }

    [Fact]
    public void ARecursiveUnionHoldsItself() {
        var module = Lower(
            """
            alias Json = Bool | F64 | String | Json[] | [String: Json]
            fn leaves(_ j: Json) -> Int {
                if j is Json[] {
                    var n = 0
                    for x in j { n = n + leaves(x) }
                    return n
                }
                if j is [String: Json] {
                    var n = 0
                    for (k, v) in j { n = n + leaves(v) }
                    return n
                }
                return 1
            }
            fn shape(_ j: Json) -> Int {
                return match j {
                    case let b as Bool => 1
                    case let f as F64 => 2
                    case let s as String => 3
                    case let xs as Json[] => 100 + xs.count
                    case let d as [String: Json] => 200 + d.count
                }
            }
            fn count() -> Int {
                let inner: Json = [1.5, "x", false]
                let object: Json = ["a": inner, "b": "y"]
                let all: Json = [object, inner, true]
                return leaves(all)
            }
            fn outer() -> Int {
                let object: Json = ["a": true, "b": "y"]
                return shape(object)
            }
            """
        );

        Assert.Equal(8L, Run(module, "count"));
        Assert.Equal(202L, Run(module, "outer"));
    }

    // ---- Chains (#152) ------------------------------------------------------

    const string Chains = """
                          struct Address { var city: String }
                          struct Point { var x: Int, var y: Int }
                          class User {
                              var address: Address?
                              init(address: Address?) { self.address = address }
                              fn lookup() -> Address? { return address }
                          }
                          class Node {
                              var v: Int
                              var next: Node?
                              init(v: Int, next: Node?) { self.v = v; self.next = next }
                          }
                          fn half(_ x: Int) -> Int? { return x % 2 == 0 ? x / 2 : none }
                          """;

    [Fact]
    public void AChainReachesThroughAnOptionalMember() {
        var module = Lower(
            Chains + """

                     fn city(_ user: User?) -> Int { return (user?.address?.city ?? "").count }
                     fn byCall(_ user: User?) -> Int { return (user?.lookup()?.city ?? "").count }
                     fn present() -> Int { return city(User(address: Address { city: "Prague" })) }
                     fn noAddress() -> Int { return city(User(address: none)) + 10 * byCall(User(address: none)) }
                     fn noUser() -> Int { return city(none) + byCall(none) }
                     fn called() -> Int { return byCall(User(address: Address { city: "Brno" })) }
                     """
        );

        Assert.Equal(6L, Run(module, "present"));
        Assert.Equal(0L, Run(module, "noAddress"));
        Assert.Equal(0L, Run(module, "noUser"));
        Assert.Equal(4L, Run(module, "called"));
    }

    [Fact]
    public void AnOptionalFunctionIsCalledWhenThereIsOne() {
        var module = Lower(
            Chains + """

                     fn some() -> Int {
                         let f: ((Int) -> Int)? = (x: Int) => x * 2
                         return f?(21) ?? 0
                     }
                     fn nothing() -> Int {
                         let g: (() -> Int)? = none
                         return g?() ?? 7
                     }
                     fn optionalResult(_ n: Int) -> Int {
                         let h: ((Int) -> Int?)? = half
                         return h?(n) ?? -1
                     }
                     """
        );

        Assert.Equal(42L, Run(module, "some"));
        Assert.Equal(7L, Run(module, "nothing"));
        Assert.Equal(4L, Run(module, "optionalResult", 8L));
        Assert.Equal(-1L, Run(module, "optionalResult", 7L));
    }

    [Fact]
    public void AStoreThroughAChainHappensWhenThereIsSomethingToStoreIn() {
        var module = Lower(
            Chains + """

                     fn objects() -> Int {
                         let a = Node(v: 1, next: Node(v: 2, next: none))
                         a.next?.v = 9
                         a.next?.next?.v = 5
                         let first = a.next?.v ?? 0
                         a.next?.next = Node(v: 3, next: none)
                         return first * 10 + (a.next?.next?.v ?? 0)
                     }
                     fn values() -> Int {
                         var p: Point? = Point { x: 1, y: 2 }
                         p?.x = 10
                         var q: Point? = none
                         q?.x = 10
                         return (p?.x ?? 0) + (q?.x ?? 0) + (p?.y ?? 0)
                     }
                     """
        );

        Assert.Equal(93L, Run(module, "objects"));
        Assert.Equal(12L, Run(module, "values"));
    }

    [Fact]
    public void AnImplicitlyUnwrappedOptionalIsUsedAsWhatItHolds() {
        var module = Lower(
            """
            fn add(_ x: Int!) -> Int { return x + 1 }
            fn length(_ s: String!) -> Int { return s.count }
            fn take(_ n: Int) -> Int { return n * 2 }
            fn pass(_ x: Int!) -> Int { return take(x) }
            fn absent(_ x: Int!) -> Bool { return x == none }
            fn some() -> Int { return add(3) + length("four") + pass(5) }
            fn nothing() -> Int { return add(none) }
            fn checked() -> Bool { return absent(none) }
            """
        );

        Assert.Equal(18L, Run(module, "some"));
        Assert.Equal(true, Run(module, "checked"));
        Assert.Equal("unwrapped a none", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.nothing")).Message);
    }

    // ---- Never standing for a value ----------------------------------------

    [Fact]
    public void UnreachableStandsForAValueOfAnyTypeAndTraps() {
        var module = Lower(
            """
            union Shape { Circle(F64), Empty }
            fn area(_ s: Shape) -> Int {
                return match s {
                    case .Empty => 0
                    case .Circle(_) => unreachable()
                }
            }
            fn pick(_ x: Int) -> String {
                let s: String = x > 0 ? "yes" : unreachable()
                return s
            }
            fn empty() -> Int { return area(.Empty) }
            fn circle() -> Int { return area(.Circle(1.0)) }
            fn positive() -> String { return pick(1) }
            """
        );

        Assert.Equal(0L, Run(module, "empty"));
        Assert.Equal("yes", Run(module, "positive"));
        Assert.Equal("reached code marked unreachable()", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.circle")).Message);
    }

    // ---- A binding that takes the whole subject -----------------------------

    /// <summary>
    ///     A match's subject is the match's own copy, destroyed once an arm
    ///     is chosen; a <c>let</c> that binds all of it keeps a copy of its
    ///     own, so the subject's life is not ended twice.
    /// </summary>
    [Fact]
    public void ABindingOfTheWholeSubjectKeepsItsOwnCopy() {
        var module = Lower(
            """
            fn only(_ s: String) -> String {
                return match s {
                    case let n => "x"
                }
            }

            fn after(_ s: String) -> String {
                return match s {
                    case "a" => "A"
                    case let n => n
                }
            }

            fn either(_ value: Int | String) -> String {
                return match value {
                    case let n as Int => "a number"
                    case (let text) => text
                }
            }

            fn number() -> String => either(1)

            fn text() -> String => either("t")
            """
        );

        Assert.Equal("x", Run(module, "only", "q"));
        Assert.Equal("A", Run(module, "after", "a"));
        Assert.Equal("q", Run(module, "after", "q"));
        Assert.Equal("a number", Run(module, "number"));
        Assert.Equal("t", Run(module, "text"));
    }

    // ---- Calling an optional function that gives Void (#270) ----------------

    [Fact]
    public void AnOptionalFunctionThatGivesVoidIsCalledThroughItsChain() {
        var module = Lower(
            """
            fn calls(_ present: Bool) -> Int {
                var n = 0
                let bump: (Int) -> Void = { n += $0 }
                let g: ((Int) -> Void)? = present ? bump : none
                g?(3)
                let r = g?(4)
                if r == none { return -1 }
                return n
            }
            """
        );

        Assert.Equal(7L, Run(module, "calls", true));
        Assert.Equal(-1L, Run(module, "calls", false));
    }

    // ---- A union case whose one field is a tuple (#275) ---------------------

    [Fact]
    public void ACaseWhoseOneFieldIsATupleCarriesTheTupleItself() {
        var module = Lower(
            """
            union H {
                Some(value: (Bool, Int))
                Pair(a: Int, b: Int)
            }
            union Box<T> {
                Full(T)
                Empty
            }
            fn second() -> Int {
                let h = H.Some(value: (true, 1))
                if case .Some(let v) = h {
                    return v.1
                }
                return -1
            }
            fn spread() -> Int {
                let h = H.Some(value: (false, 7))
                return match h {
                    case .Some(let v) => v.0 ? 0 : v.1
                    case .Pair(let a, let b) => a + b
                }
            }
            fn generic() -> Int {
                let b: Box<(Int, Int)> = .Full((2, 3))
                if case .Full(let t) = b {
                    return t.0 * t.1
                }
                return -1
            }
            """
        );

        Assert.Equal(1L, Run(module, "second"));
        Assert.Equal(7L, Run(module, "spread"));
        Assert.Equal(6L, Run(module, "generic"));
    }
    /// <summary>
    ///     A store through '?.' or '!' into a value an optional holds changes
    ///     it where it is (enum_payload_addr): no copy of the payload is taken
    ///     out and written back, and a none is left alone, or traps.
    /// </summary>
    [Fact]
    public void AStoreIntoAnOptionalsValueChangesItWhereItIs() {
        var module = Lower(
            """
            struct Entry { var key: String, var value: Int }
            fn put(_ entries: inout Entry?[>..], _ i: Int, _ v: Int) {
                entries[i]!.value = v
            }
            fn chain(_ entries: inout Entry?[>..], _ i: Int, _ v: Int) {
                entries[i]?.value = v
            }
            fn total() -> Int {
                var entries: Entry?[>..] = [Entry { key: "a", value: 1 }, none, Entry { key: "c", value: 3 }]
                put(&entries, 0, 10)
                chain(&entries, 1, 20)
                chain(&entries, 2, 30)
                entries[2]!.value += 5
                entries[0]!.key = "z"
                var flag: Bool? = true
                flag! = false
                var word: String? = "x"
                word! = "yz"
                return entries[0]!.value + (entries[1]?.value ?? 100) + entries[2]!.value + entries[0]!.key.count
                    + (flag! ? 1000 : 0) + word!.count
            }
            fn missing() -> Int {
                var entries: Entry?[>..] = [none]
                put(&entries, 0, 1)
                return 0
            }
            """
        );

        foreach (var name in new[] { "M.put", "M.chain" }) {
            var function = module.Functions.Single(f => f.Name == name);
            var instructions = function.Blocks.SelectMany(static b => b.Instructions).ToList();
            Assert.Contains(instructions, static i => i is EnumPayloadAddrInst);
            Assert.DoesNotContain(instructions, static i => i is EnumInst);
        }

        Assert.Equal(10L + 100 + 35 + 1 + 2, Run(module, "total"));
        Assert.Contains("unwrapped a none", Assert.Throws<SilTrap>(() => Run(module, "missing")).Message);
    }
}
