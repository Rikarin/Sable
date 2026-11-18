using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     '@packed' and '@align(n)' (SYNTAX.md §22): the layout they give, in a
///     frame, a field, a tuple, an object, an array and an optional, and what
///     the native code computes through it agreeing with the interpreter.
/// </summary>
public sealed class LayoutTests {
    const string Declarations = """
        @packed
        struct Header {
            var tag: U8
            var length: U32
            var flags: U16
        }

        @align(16)
        struct Vec {
            var x: F32
            var y: F32
        }

        struct Holder {
            var a: U8
            var v: Vec
            var h: Header
            var name: String
        }

        @packed
        struct Mixed {
            var b: Bool
            var s: String
            var n: Int
        }

        class Box {
            var c: U8 = 1
            var v: Vec = Vec(x: 1, y: 2)
            var h: Header = Header(tag: 1, length: 2, flags: 3)
        }

        extension Header {
            @inline(never)
            mutating fn grow() {
                for i in 0..<10 {
                    length += 1
                }
            }
        }

        @inline(never)
        fn bump(_ n: inout U32) {
            for j in 0..<1 {
                n += 1
            }
        }
        """;

    const string Body = """
            var h = Header(tag: 7, length: 100, flags: 3)
            h.length += 1
            bump(&h.length)
            h.grow()
            print(h.length)
            var hold = Holder(a: 1, v: Vec(x: 1.5, y: 2.5), h: h, name: "x")
            hold.h.length += 5
            bump(&hold.h.length)
            print(hold.h.length)
            print(hold.v.y)
            let pair = (U8(1), Vec(x: 3, y: 4))
            print(pair.1.x)
            var m = Mixed(b: true, s: "str", n: 42)
            m.s = m.s + "!"
            m.n += 1
            print(m.s)
            print(m.n)
            let box = Box()
            box.h.length += 1
            print(box.h.length)
            print(box.v.y)
            var vs: Vec[>..] = []
            for i in 0..<5 {
                vs.append(Vec(x: F32(i), y: 0))
            }
            print(vs[4].x)
            let opt: Vec? = Vec(x: 9, y: 9)
            if let o = opt {
                print(o.x)
            }
        """;

    [Fact]
    public void PackedAndAlignedStructsHoldTheirValuesWhereverTheyAre() =>
        Assert.Equal(
            "112\n118\n2.5\n3.0\nstr!\n43\n3\n2.0\n4.0\n9.0\n",
            AssertAgree(Program(Declarations, Body)).Stdout
        );

    /// <summary>
    ///     A packed struct is a packed LLVM struct, read and written through
    ///     its fields a byte aligned; one that holds an over-aligned struct is
    ///     padded to put it at a multiple of 16, and its storage is aligned so;
    ///     a field of a packed struct passed to a function that changes it goes
    ///     through an aligned slot and back.
    /// </summary>
    [Fact]
    public void ThePackedOrPaddedLayoutIsWhatLlvmIsTold() {
        var result = Pipeline.Compile(new SourceText(Program(Declarations, Body), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var ir = result.LlvmIr!;
        Assert.Contains("<{ i8, i32, i16 }>", ir, StringComparison.Ordinal);
        Assert.Contains("<{ float, float, [8 x i8] }>", ir, StringComparison.Ordinal);
        Assert.Contains("alloca <{ i8, [15 x i8], <{ float, float, [8 x i8] }>, <{ i8, i32, i16 }>, [1 x i8], ptr }>, align 16", ir, StringComparison.Ordinal);
        Assert.Contains("load i32, ptr %", ir, StringComparison.Ordinal);
        Assert.Matches(@"store i32 %t\d+, ptr %t\d+, align 1", ir);
        Assert.Matches(@"store i32 %t\d+, ptr %t\d+, align 4\n  call void @""sable\.Test\.bump""", ir);
    }
}
