using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The standard library (docs/04 §4.3), written in Sable in lib/ and
///     compiled with the programs that import it: Sable.Core's protocols,
///     Sable.Math, Sable.Text, Sable.Collections. Native and interpreted.
/// </summary>
public sealed class LibraryTests {
    [Fact]
    public void MathIsWrittenInSable() =>
        Assert.Equal(
            "7\na\n10\n21\n12\n1099511627776\ntrue\nfalse\n1.414213562373095\n-3 3 3 -3\n2027381 3 true\n",
            AssertAgree("""
module P
import Sable.Math
@main
fn main() {
    print(max(3, 7))
    print(min("b", "a"))
    print(clamp(15, 0, 10))
    print(gcd(1071, 462))
    print(lcm(4, 6))
    print(power(2, 40))
    print(isPrime(97))
    print(isPrime(91))
    print(squareRoot(2.0))
    print("\(floor(-2.5)) \(ceiling(2.1)) \(round(2.5)) \(round(-2.5))")
    var r = Random(seed: 42)
    print("\(r.next()) \(r.next(from: 1, to: 7)) \(r.nextUnit() < 1.0)")
}
""").Stdout
        );

    [Fact]
    public void TextIsWrittenInSable() =>
        Assert.Equal(
            "ababab\na, b, c\nolléh\ntrue true true\n2\na+b+c\n1||2\n007\nab..|\nHELLO, WORLD abc\nxy\nz\n",
            AssertAgree("""
module P
import Sable.Text
@main
fn main() {
    print(repeating("ab", 3))
    print(joined(["a", "b", "c"], separator: ", "))
    print(reversed("héllo"))
    print("\(hasPrefix("swift", "sw")) \(hasSuffix("swift", "ft")) \(contains("haystack", "st"))")
    print(find("banana", "nan") ?? -1)
    print(replacing("a-b-c", "-", with: "+"))
    print(joined(splitting("1,,2", by: ","), separator: "|"))
    print(paddedLeft("7", to: 3, with: '0'))
    print(paddedRight("ab", to: 4, with: '.') + "|")
    print(uppercased("Hello, World") + " " + lowercased("ABC"))
    let b = TextBuilder()
    b.append("x")
    b.appendLine("y")
    b.append("z")
    print(b.build())
}
""").Stdout
        );

    [Fact]
    public void CollectionsAreWrittenInSable() =>
        Assert.Equal(
            "2 1\na b 1\n4 2 1 false\ntrue\nto be not \ntrue false true 2\n1 9\naccc\n",
            AssertAgree("""
module P
import Sable.Collections

@main
fn main() {
    let s = Stack<Int>()
    s.push(1)
    s.push(2)
    print("\(s.pop() ?? 0) \(s.count)")
    let q = Queue<String>()
    q.enqueue("a")
    q.enqueue("b")
    q.enqueue("c")
    print("\(q.dequeue() ?? "") \(q.dequeue() ?? "") \(q.count)")
    let m = HashMap<String, Int>()
    for word in ["to", "be", "or", "not", "to", "be"] {
        m.set(word, (m.get(word) ?? 0) + 1)
    }
    print("\(m.count) \(m.get("to") ?? 0) \(m.get("not") ?? 0) \(m.contains("xyz"))")
    print(m.remove("or"))
    var keys = ""
    for k in m.allKeys() {
        keys = keys + k + " "
    }
    print(keys)
    let unique = HashSet<Int>()
    print("\(unique.insert(3)) \(unique.insert(3)) \(unique.insert(5)) \(unique.count)")
    let ordered = sorted([5, 3, 9, 1])
    print("\(ordered[0]) \(ordered[3])")
    let byLength = sorted(["ccc", "a", "bb"], by: (a: String, b: String) => a.count < b.count)
    print(byLength[0] + byLength[2])
}
""").Stdout
        );

    [Fact]
    public void GenericCodeComparesThroughTheCoreProtocols() =>
        Assert.Equal(
            "9\nzoo\ntrue\nfalse\n",
            AssertAgree("""
                module P
                fn biggest<T: Comparable>(_ values: T[>..]) -> T? {
                    if values.count == 0 { return none }
                    var best = values[0]
                    for v in values {
                        if v > best { best = v }
                    }
                    return best
                }
                fn has<T: Equatable>(_ values: T[>..], _ wanted: T) -> Bool {
                    for v in values {
                        if v == wanted { return true }
                    }
                    return false
                }
                struct Money: Equatable {
                    var cents: Int
                    operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
                }
                @main
                fn main() {
                    print(biggest([3, 9, 2]) ?? 0)
                    print(biggest(["pear", "apple", "zoo"]) ?? "")
                    print(has([Money { cents: 5 }], Money { cents: 5 }))
                    print(has([Money { cents: 5 }], Money { cents: 6 }))
                }
                """).Stdout
        );

    /// <summary>Only what a program reaches is built: the library costs a program that does not use it nothing.</summary>
    [Fact]
    public void WhatAProgramDoesNotUseIsNotBuilt() {
        var result = Pipeline.Compile(new SourceText("module H\nimport Sable.Collections\n@main\nfn main() {\n    print(1)\n}\n", "test.sab"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.DoesNotContain("HashMap", result.LlvmIr!, StringComparison.Ordinal);
        Assert.DoesNotContain("Sable.Core.Int.<", result.LlvmIr!, StringComparison.Ordinal);
    }
}
