using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>@inline_layout</c> (docs/02 §2.5, docs/04 §3.6): a value as one
///     contiguous, pointer-free block — fixed fields first, then variable
///     ones in place — read and written where it is, serialised by being its
///     bytes, and revived only after every count, tag and relative link is
///     checked. Native and interpreted, held to the same bytes.
/// </summary>
public sealed class InlineLayoutTests {
    const string Orders = """
                          @inline_layout
                          struct OrderLine {
                              var sku: U8[..16]
                              var qty: U16
                              var price: U32
                          }

                          @inline_layout
                          struct Order {
                              var id: U64
                              var customer: U8[..32]
                              var lines: OrderLine[>..]
                              var parent: Order&<u32>?

                              var total: U64 {
                                  get {
                                      var t: U64 = 0
                                      for line in lines {
                                          t = t + U64(line.qty) * U64(line.price)
                                      }
                                      return t
                                  }
                              }
                          }

                          fn describe(_ order: Order) -> String {
                              var skus = ""
                              for line in order.lines {
                                  skus = skus + " " + line.sku.toString()
                              }
                              return "#\(order.id) \(order.customer.toString()):\(skus) = \(order.total)"
                          }

                          fn decode(_ bytes: U8[>..]) -> String {
                              return match Order.fromBytes(bytes) {
                                  case .Ok(let order) => describe(order)
                                  case .Invalid(let why) => "rejected: \(why)"
                              }
                          }

                          fn truncated(_ bytes: U8[>..]) -> U8[>..] {
                              var kept: U8[>..] = []
                              for i in 0..<(bytes.count - 1) {
                                  kept.append(bytes[i])
                              }
                              return kept
                          }
                          """;

    [Fact]
    public void AnOrderIsItsBytes() =>
        Assert.Equal(
            "#7 Ann: A-1 B-22 = 4498\n38\n7 0 0 0 0 0 0 0\n#7 Ann: A-1 B-22 = 4498\n#7 Ann: = 0\n17\n",
            AssertAgree(
                Program(
                    Orders,
                    """
                        var order = Order { id: 7, customer: "Ann", lines: [], parent: none }
                        order.lines.append(OrderLine { sku: "A-1", qty: 2, price: 1999 })
                        order.lines.append(OrderLine { sku: "B-22", qty: 1, price: 500 })
                        print(describe(order))
                        let bytes = Bytes.of(order)
                        print(bytes.count)
                        print("\(bytes[0]) \(bytes[1]) \(bytes[2]) \(bytes[3]) \(bytes[4]) \(bytes[5]) \(bytes[6]) \(bytes[7])")
                        print(decode(bytes))
                        let empty = Order { id: 7, customer: "Ann", lines: [], parent: none }
                        print(describe(empty))
                        print(Bytes.of(empty).count)
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     '@align(n)' on an inline struct rounds its fixed part up to a
    ///     multiple of n, and puts it at a multiple of n in one that holds it,
    ///     with zero bytes between: a record format's fixed stride.
    /// </summary>
    [Fact]
    public void AlignPadsTheFixedPart() =>
        Assert.Equal(
            "8\n33\n1 0 0 0 0 0 0 0 2 0 0 0 0 0 0 0\n3 0 1 7 9\n5 9 7 1 2\n",
            AssertAgree(
                Program(
                    """
                    @inline_layout
                    @align(8)
                    struct Stamp {
                        var kind: U8
                        var code: U16
                    }

                    @inline_layout
                    struct Entry {
                        var flag: U8
                        var stamp: Stamp
                        var count: U8
                        var stamps: Stamp[..4]
                    }
                    """,
                    """
                        print(Bytes.of(Stamp { kind: 1, code: 2 }).count)
                        var e = Entry { flag: 1, stamp: Stamp { kind: 2, code: 0 }, count: 3, stamps: [] }
                        e.stamps.append(Stamp { kind: 7, code: 9 })
                        let bytes = Bytes.of(e)
                        print(bytes.count)
                        var line = ""
                        for i in 0..<16 {
                            line = line + (i == 0 ? "" : " ") + "\(bytes[i])"
                        }
                        print(line)
                        print("\(bytes[16]) \(bytes[17]) \(bytes[24]) \(bytes[25]) \(bytes[26])")
                        match Entry.fromBytes(bytes) {
                            case .Ok(let copy) => print("\(copy.count + 2) \(copy.stamps[0].code) \(copy.stamps[0].kind) \(copy.flag) \(copy.stamp.kind)")
                            case .Invalid(let why) => print(why)
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ValuesAreCopiedOnWrite() =>
        Assert.Equal(
            "#1 Ann: x = 10\n#2 Bob: x y = 30\n",
            AssertAgree(
                Program(
                    Orders,
                    """
                        var a = Order { id: 1, customer: "Ann", lines: [OrderLine { sku: "x", qty: 1, price: 10 }], parent: none }
                        var b = a
                        b.id = 2
                        b.customer = "Bob"
                        b.lines.append(OrderLine { sku: "y", qty: 2, price: 10 })
                        print(describe(a))
                        print(describe(b))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void FromBytesRefusesWhatIsNotAValue() =>
        Assert.Equal(
            "rejected: a count longer than the bytes\nrejected: bytes after the value\nrejected: an inline array longer than its bound\n"
            + "rejected: a relative reference that does not point at a value of its type\nrejected: a count longer than the bytes\n"
            + "rejected: truncated\n#7 Ann: A-1 = 3998\n",
            AssertAgree(
                Program(
                    Orders,
                    """
                        var order = Order { id: 7, customer: "Ann", lines: [OrderLine { sku: "A-1", qty: 2, price: 1999 }], parent: none }
                        let good = Bytes.of(order)
                        print(decode(truncated(good)))
                        var longer = good
                        longer.append(0)
                        print(decode(longer))
                        var bound = good
                        bound[12] = 33
                        print(decode(bound))
                        var dangling = good
                        dangling[8] = 3
                        print(decode(dangling))
                        var lines = good
                        lines[16] = 100
                        print(decode(lines))
                        let nothing: U8[>..] = []
                        print(decode(nothing))
                        var selfParent = good
                        selfParent[8] = 248
                        selfParent[9] = 255
                        selfParent[10] = 255
                        selfParent[11] = 255
                        print(decode(selfParent))
                    """
                )
            ).Stdout
        );

    const string Trees = """
                         @inline_layout
                         struct Node {
                             var value: I32
                             var parent: Node&<u32>?
                         }

                         @inline_layout
                         struct Tree {
                             var root: Node&<u32>?
                             var name: U8[..32]
                             var nodes: Node[>..]
                         }

                         fn depth(_ tree: Tree, _ index: Int) -> Int {
                             var steps = 0
                             var current = tree.nodes[index].parent
                             while current != none {
                                 steps = steps + 1
                                 current = current!.parent
                             }
                             return steps
                         }
                         """;

    [Fact]
    public void RelativeReferencesLinkValuesInOneBlock() =>
        Assert.Equal(
            "0 1 2 1\n10\n30 20 10\nroot 10\n0 1 2 1\nroot 10\nok\n",
            AssertAgree(
                Program(
                    Trees,
                    """
                        var tree = Tree { root: none, name: "t", nodes: [] }
                        tree.nodes.append(Node { value: 10, parent: none })
                        tree.nodes.append(Node { value: 20, parent: none })
                        tree.nodes.append(Node { value: 30, parent: none })
                        tree.nodes.append(Node { value: 40, parent: none })
                        tree.nodes[1].parent = &tree.nodes[0]
                        tree.nodes[2].parent = &tree.nodes[1]
                        tree.nodes[3].parent = &tree.nodes[0]
                        tree.root = &tree.nodes[0]
                        print("\(depth(tree, 0)) \(depth(tree, 1)) \(depth(tree, 2)) \(depth(tree, 3))")
                        print(tree.nodes[2].parent!.parent!.value)
                        var walk = ""
                        for node in tree.nodes {
                            if node.value == 30 {
                                walk = "\(node.value) \(node.parent!.value) \(node.parent!.parent!.value)"
                            }
                        }
                        print(walk)
                        print("root \(tree.root!.value)")

                        // The name grows: the nodes move, and every reference across the move is measured again.
                        tree.name = "a much longer name"
                        print("\(depth(tree, 0)) \(depth(tree, 1)) \(depth(tree, 2)) \(depth(tree, 3))")
                        print("root \(tree.root!.value)")
                        match Tree.fromBytes(Bytes.of(tree)) {
                            case .Ok(let copy) => print(copy.nodes.count == 4 ? "ok" : "wrong")
                            case .Invalid(let why) => print(why)
                        }
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     An append is two splices — the element, then the count that takes
    ///     it in — and between them the bytes are no value: walked then, the
    ///     count stops short and reads the element as the next field. The
    ///     relative references are read once, before either, so an array
    ///     followed by another variable-size one grows, interpreted as natively.
    /// </summary>
    [Fact]
    public void AnArrayGrowsBeforeAnotherVariableField() =>
        Assert.Equal(
            "3 2\n12 7\n",
            AssertAgree(
                Program(
                    """
                    @inline_layout
                    struct Line {
                        var sku: U8[..16]
                        var qty: U16
                    }

                    @inline_layout
                    struct Order {
                        var id: U64
                        var codes: U16[..8]
                        var lines: Line[>..]
                        var parent: Order&<u32>?
                    }
                    """,
                    """
                        var order = Order { id: 7, codes: [], lines: [], parent: none }
                        order.codes.append(3)
                        order.codes.append(4)
                        order.lines.append(Line { sku: "a", qty: 2 })
                        order.codes.append(5)
                        order.lines.append(Line { sku: "bb", qty: 5 })
                        print("\(order.codes.count) \(order.lines.count)")
                        var codes = 0
                        for code in order.codes { codes = codes + Int(code) }
                        var qty = 0
                        for line in order.lines { qty = qty + Int(line.qty) }
                        print("\(codes) \(qty)")
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A grow-only array's count takes a second byte at 128, a splice of
    ///     its own after the element's; with a relative reference across it,
    ///     that reference is still measured from the bytes as they were.
    /// </summary>
    [Fact]
    public void ACountThatTakesAnotherByteKeepsReferencesAcrossIt() =>
        Assert.Equal(
            "200 10 20\nok\n",
            AssertAgree(
                Program(
                    """
                    @inline_layout
                    struct Node {
                        var value: U32
                    }

                    @inline_layout
                    struct Graph {
                        var root: Node&<u32>?
                        var tags: U8[>..]
                        var nodes: Node[>..]
                    }
                    """,
                    """
                        var graph = Graph { root: none, tags: [], nodes: [] }
                        graph.nodes.append(Node { value: 10 })
                        graph.nodes.append(Node { value: 20 })
                        graph.root = &graph.nodes[1]
                        var i = 0
                        while i < 200 {
                            graph.tags.append(1)
                            i = i + 1
                        }
                        print("\(graph.tags.count) \(graph.nodes[0].value) \(graph.root!.value)")
                        match Graph.fromBytes(Bytes.of(graph)) {
                            case .Ok(let copy) => print(copy.root!.value == 20 ? "ok" : "wrong")
                            case .Invalid(let why) => print(why)
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void EveryScalarKeepsItsBits() =>
        Assert.Equal(
            "true -5 65535 -2147483648 18446744073709551615 1.5 -0.25 é\n3 5 8\n36\n",
            AssertAgree(
                Program(
                    """
                    @inline_layout
                    struct Scalars {
                        var flag: Bool
                        var small: I8
                        var wide: U16
                        var signed: I32
                        var big: U64
                        var single: F32
                        var double: F64
                        var letter: Char
                        var primes: U8[..4]
                    }
                    """,
                    """
                        var s = Scalars { flag: true, small: -5, wide: 65535, signed: -2147483648, big: 18446744073709551615, single: 1.5, double: -0.25, letter: 'é', primes: [3, 5] }
                        s.primes.append(8)
                        print("\(s.flag) \(s.small) \(s.wide) \(s.signed) \(s.big) \(s.single) \(s.double) \(s.letter)")
                        var listed = ""
                        for p in s.primes {
                            listed = listed + (listed.count == 0 ? "" : " ") + "\(p)"
                        }
                        print(listed)
                        print(Bytes.of(s).count)
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     '@inline_layout' conforms the struct to Sable.Core's InlineLayout,
    ///     so generic code can decode one by its requirement.
    /// </summary>
    [Fact]
    public void GenericCodeDecodesThroughInlineLayout() =>
        Assert.Equal(
            "16\nrejected\n",
            AssertAgree(
                Program(
                    """
                    @inline_layout
                    struct Packed { var a: U8, var b: U8 }

                    fn load<T: InlineLayout>(_ bytes: U8[>..]) -> Decoded<T> {
                        return T.fromBytes(bytes)
                    }

                    fn show(_ decoded: Decoded<Packed>) -> String {
                        return match decoded {
                            case .Ok(let p) => "\(Int(p.a) + Int(p.b))"
                            case .Invalid(_) => "rejected"
                        }
                    }
                    """,
                    """
                        let good: Decoded<Packed> = load(Bytes.of(Packed { a: 7, b: 9 }))
                        print(show(good))
                        let short: U8[>..] = [1]
                        let bad: Decoded<Packed> = load(short)
                        print(show(bad))
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     The fuzzer of tools/fuzz, cut to a few thousand cases: the native
    ///     validator and the interpreter's accept and refuse the same inputs.
    /// </summary>
    [Fact]
    public void TheValidatorsAgreeOnMutatedInputs() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "Sable.slnx"))) {
            directory = directory.Parent;
        }

        var source = File.ReadAllText(Path.Combine(directory.FullName, "tools", "fuzz", "inline_layout.sb"))
            .Replace("setting(\"SABLE_FUZZ_CASES\", 10000000)", "3000", StringComparison.Ordinal);
        Assert.Contains("let cases = 3000", source, StringComparison.Ordinal);
        Assert.StartsWith("seed 20261117\ncases 3000, accepted ", AssertAgree(source, stepLimit: 100_000_000).Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var o = Order { id: 1, customer: \"this customer name is far too long for it\", lines: [], parent: none }\nprint(o.id)", "too many bytes for the inline array")]
    [InlineData("var t = Tree { root: none, name: \"t\", nodes: [] }\nprint(t.nodes[0].value)", "array index out of range")]
    public void InlineLimitsTrap(string body, string trap) {
        var execution = AssertAgree(Program(Orders + "\n" + Trees, body));
        Assert.StartsWith($"fatal error: {trap}\n  at test.sb:", execution.Stderr, StringComparison.Ordinal);
    }

    const string Views = """
                         @inline_layout
                         struct Item {
                             var value: I32
                         }

                         @inline_layout
                         struct Bag {
                             var items: Item[>..]
                         }

                         fn first(_ bag: Bag&) -> Item& {
                             return &bag.items[0]
                         }
                         """;

    [Fact]
    public void AViewRootedInTheCallersValueMayBeReturned() =>
        Assert.Equal(
            "4 5\n",
            AssertAgree(
                Program(
                    Views,
                    """
                        var bag = Bag { items: [] }
                        bag.items.append(Item { value: 4 })
                        let item = first(bag)
                        print("\(item.value) \(first(bag).value + 1)")
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("fn bad() -> Item& {\n    var bag = Bag { items: [Item { value: 1 }] }\n    return &bag.items[0]\n}", "", "cannot return a reference rooted in local 'bag'")]
    [InlineData("fn bad(_ bag: Bag) -> Item& {\n    return &bag.items[0]\n}", "", "cannot return a reference rooted in parameter 'bag', which is passed by value")]
    [InlineData("", "    var bag = Bag { items: [Item { value: 1 }] }\n    let kept = &bag.items[0]\n    bag.items.append(Item { value: 2 })\n    print(kept.value)",
        "this reference into local 'bag' is stale — the value was changed at line")]
    [InlineData("", "    var bag = Bag { items: [Item { value: 1 }] }\n    for item in bag.items {\n        bag.items.append(Item { value: item.value })\n    }",
        "this reference into local 'bag' is stale")]
    public void AViewCannotOutliveOrOutlastItsBlock(string declarations, string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(Views + "\n" + declarations, body.Length == 0 ? "    print(1)" : body), "test.sb"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }
}
