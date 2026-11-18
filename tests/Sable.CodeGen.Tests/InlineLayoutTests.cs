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

        var source = File.ReadAllText(Path.Combine(directory.FullName, "tools", "fuzz", "inline_layout.sab"))
            .Replace("let cases = 10000000", "let cases = 3000", StringComparison.Ordinal);
        Assert.StartsWith("cases 3000, accepted ", AssertAgree(source, stepLimit: 100_000_000).Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var o = Order { id: 1, customer: \"this customer name is far too long for it\", lines: [], parent: none }\nprint(o.id)", "too many bytes for the inline array")]
    [InlineData("var t = Tree { root: none, name: \"t\", nodes: [] }\nprint(t.nodes[0].value)", "array index out of range")]
    public void InlineLimitsTrap(string body, string trap) {
        var execution = AssertAgree(Program(Orders + "\n" + Trees, body));
        Assert.Equal($"fatal error: {trap}\n", execution.Stderr);
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

                         fn first(_ bag: ref Bag) -> ref Item {
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
                        let item = first(&bag)
                        print("\(item.value) \(first(&bag).value + 1)")
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("fn bad() -> ref Item {\n    var bag = Bag { items: [Item { value: 1 }] }\n    return &bag.items[0]\n}", "", "cannot return a reference rooted in local 'bag'")]
    [InlineData("fn bad(_ bag: Bag) -> ref Item {\n    return &bag.items[0]\n}", "", "cannot return a reference rooted in parameter 'bag', which is passed by value")]
    [InlineData("", "    var bag = Bag { items: [Item { value: 1 }] }\n    let kept = &bag.items[0]\n    bag.items.append(Item { value: 2 })\n    print(kept.value)",
        "this reference into local 'bag' is stale — the value was changed at line")]
    [InlineData("", "    var bag = Bag { items: [Item { value: 1 }] }\n    for item in bag.items {\n        bag.items.append(Item { value: item.value })\n    }",
        "this reference into local 'bag' is stale")]
    public void AViewCannotOutliveOrOutlastItsBlock(string declarations, string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(Views + "\n" + declarations, body.Length == 0 ? "    print(1)" : body), "test.sab"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }
}
