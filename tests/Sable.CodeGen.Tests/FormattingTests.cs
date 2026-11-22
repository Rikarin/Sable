using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     '#?' laid out over lines, and an interpolation written into a
///     TextWriter a piece at a time: the interpreter and the native code
///     agree, specialised and not.
/// </summary>
[Native]
public sealed class FormattingTests {
    const string Program = """
                           module P
                           @derive(Debug)
                           struct Order {
                               let id: Int
                               let items: String[>..]
                           }
                           struct Pieces: TextWriter {
                               var count = 0
                               var text = ""
                               mutating fn write(_ piece: String) {
                                   count += 1
                                   text = text + piece
                               }
                           }
                           fn total<W: TextWriter>(_ writer: inout W, _ n: Int) {
                               writer.write("n=\(n:04) [\(n:x)]")
                           }
                           @main
                           fn main() {
                               print("\(Order(id: 1, items: ["a"]):#?)")
                               var w = Pieces()
                               w.write("x\(2)y\([1, 2])")
                               total(&w, 255)
                               print("\(w.count) \(w.text)")
                           }
                           """;

    const string Expected = "Order(\n    id: 1,\n    items: [\n        \"a\",\n    ],\n)\n9 x2y[1, 2]n=0255 [ff]\n";

    [Fact]
    public void AgreeSpecialised() {
        using var _ = SilGen.UseGenerics(GenericsMode.Specialised);
        Assert.Equal(Expected, AssertAgree(Program).Stdout);
    }

    [Fact]
    public void AgreeUnspecialised() {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        Assert.Equal(Expected, AssertAgree(Program).Stdout);
    }
}
