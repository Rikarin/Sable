using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Generic code over the iteration and collection protocols, given the
///     compiler's own sequences — arrays of every kind, ranges, strings and
///     dictionaries — and the library's algorithms over them: native and
///     interpreted, held to the same answers.
/// </summary>
public sealed class SequenceProtocolTests {
    const string Generic = """
                           fn total<S: Sequence>(_ values: S) -> Int where S.Element == Int {
                               var sum = 0
                               for value in values {
                                   sum += value
                               }
                               return sum
                           }

                           fn middle<C: RandomAccessCollection>(_ items: C) -> C.Element? {
                               if !(items.startIndex < items.endIndex) {
                                   return none
                               }
                               return items[items.index(items.startIndex, offsetBy: items.count / 2)]
                           }

                           fn bump<C: MutableCollection>(_ items: inout C) where C.Element == Int {
                               var i = items.startIndex
                               while i < items.endIndex {
                                   items[i] = items[i] * 10
                                   i = items.index(after: i)
                               }
                           }

                           fn upTo<C: RangeReplaceableCollection>(_ n: Int) -> C where C.Element == Int {
                               var made = C()
                               var i = 0
                               while i < n {
                                   made.append(element: i)
                                   i += 1
                               }
                               return made
                           }
                           """;

    [Fact]
    public void GenericCodeGoesThroughEveryKindOfArray() =>
        Assert.Equal(
            "6\n9\n5\n2\n",
            AssertAgree(
                Program(
                    Generic,
                    """
                        let grow: Int[>..] = [1, 2, 3]
                        let shrink: Int[>..<] = [4, 5]
                        let fixed: Int[] = [5]
                        print(total(grow))
                        print(total(shrink))
                        print(total(fixed))
                        print(total(grow.slice(from: 1, to: 2)))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AnArrayIsACollectionByPosition() =>
        Assert.Equal(
            "b\n10,20,30\n0,1,2,3\n",
            AssertAgree(
                Program(
                    Generic,
                    """
                        let letters: String[>..] = ["a", "b", "c"]
                        print(middle(letters) ?? "?")
                        var values: Int[>..] = [1, 2, 3]
                        bump(&values)
                        print("\(values[0]),\(values[1]),\(values[2])")
                        let made: Int[>..] = upTo(4)
                        print("\(made[0]),\(made[1]),\(made[2]),\(made[3])")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void RangesStringsAndDictionariesAreSequences() =>
        Assert.Equal(
            "9\n10\n5\n3\n42\n",
            AssertAgree(
                Program(
                    Generic,
                    """
                        print(total(2..<5))
                        print(total(1...4))
                        print(middle(3..<8) ?? -1)
                        var characters = 0
                        for _ in "héllo".filter({ $0 != 'l' }) {
                            characters += 1
                        }
                        print(characters)
                        let ages: [String: Int] = ["a": 40, "b": 2]
                        print(ages.reduce(0) { total, entry in total + entry.1 })
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TheAlgorithmsAgree() =>
        Assert.Equal(
            "10,6,2\n1,2,3,5,8\n18\n3\n1:3 2:8\n50,30,80\n8,5,3\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let values: Int[>..] = [5, 3, 8, 1, 2]
                        print(values.filter { $0 % 2 == 1 }.map { "\($0 * 2)" }.joined(separator: ","))
                        print(values.sorted().map { "\($0)" }.joined(separator: ","))
                        print(values.reduce(-1) { $0 + $1 })
                        print(values.count(where: { $0 > 2 }))
                        var pairs: String[>..] = []
                        for (offset, value) in values.enumerated() where offset == 1 || offset == 2 {
                            pairs.append("\(offset):\(value)")
                        }
                        print(pairs.joined(separator: " "))
                        print(values.lazy.filter { $0 > 2 }.map { "\($0 * 10)" }.toArray().joined(separator: ","))
                        let backwards: Int[>..] = values.filter { $0 > 2 }.sorted().reversed().collect()
                        print(backwards.map { "\($0)" }.joined(separator: ","))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ALoopThatNamesNoElementStillGoesThroughThem() =>
        Assert.Equal(
            "2 3 1\n",
            AssertAgree(
                Program(
                    """
                    fn letters() -> Char[>..] {
                        var made: Char[>..] = []
                        made.append('a')
                        made.append('b')
                        return made
                    }
                    """,
                    """
                        var a = 0
                        for _ in letters() {
                            a += 1
                        }
                        var b = 0
                        for _ in 0..<3 {
                            b += 1
                        }
                        var c = 0
                        let d: [String: Int] = ["k": 1]
                        for _ in d {
                            c += 1
                        }
                        print("\(a) \(b) \(c)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AProtocolsDefaultReachesWhatTheProtocolItRefinesRequires() =>
        Assert.Equal(
            "true\n30\n",
            AssertAgree(
                Program(
                    """
                    struct Tens: BidirectionalCollection {
                        var startIndex: Int => 0
                        var endIndex: Int => 4
                        var count: Int => 4
                        fn index(after i: Int) -> Int => i + 1
                        fn index(before i: Int) -> Int => i - 1
                        subscript(position: Int) -> Int {
                            get => position * 10
                        }
                        fn makeIterator() -> ArrayIterator<Int> {
                            let values: Int[>..] = [0, 10, 20, 30]
                            return values.makeIterator()
                        }
                    }

                    extension BidirectionalCollection {
                        var hasAny: Bool => startIndex < endIndex
                    }
                    """,
                    """
                        print(Tens().hasAny)
                        print(Tens().last ?? -1)
                    """
                )
            ).Stdout
        );
}
