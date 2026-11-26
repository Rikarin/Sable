namespace Sable.CodeGen.Tests;

/// <summary>Gaps of the compiler reported while writing the library (#340–#349), natively and in the interpreter.</summary>
[Native]
public sealed class ReportedGapTests {
    /// <summary>A variadic parameter with defaulted ones after it, generic or not: a label ends its elements (#340).</summary>
    [Fact]
    public void AVariadicParameterBeforeDefaultedOnes() =>
        Assert.Equal(
            """
            3 6 19
            0 0 20
            1 4 7
            0 x
            2 none
            1 t
            205
            6

            """,
            Programs.AssertAgree("""
            module Variadic

            fn f(_ xs: Int..., file: String = #file, line: Int = #line) -> String {
                var total = 0
                for x in xs {
                    total += x
                }
                return "\(xs.count) \(total) \(line)"
            }

            fn named(_ xs: Int..., file: String = #file) -> String => "\(xs.count) \(file)"

            fn g<T>(_ xs: T..., tag: String = "none") -> String => "\(xs.count) \(tag)"

            fn h(label: String, _ xs: Int..., then: Int) -> Int => xs.count * 100 + then

            @main
            fn main() {
                print(f(1, 2, 3))
                print(f())
                print(f(4, line: 7))
                print(named(file: "x"))
                print(g("a", "b"))
                print(g(1, tag: "t"))
                print(h(label: "l", 1, 2, then: 5))
                print(h(label: "l", then: 6))
            }
            """).Stdout);
}
