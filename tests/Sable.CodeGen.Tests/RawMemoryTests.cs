using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Raw memory (RawPtr, UnsafeBuffer, NativeMemory): the interpreter's
///     model of it and the native code agree, specialised and not, and what
///     natively is undefined the interpreter traps on, saying what it was.
/// </summary>
[Native]
public sealed class RawMemoryTests {
    const string Pointers = """
                            module P
                            final class Box {
                                var value: Int
                                init(_ value: Int) { self.value = value }
                            }
                            fn total<T: AdditiveArithmetic>(_ start: RawPtr<T>, _ count: Int) -> T {
                                var sum = T.zero
                                var i = 0
                                while i < count {
                                    unsafe { sum = sum + start.load(at: i) }
                                    i += 1
                                }
                                return sum
                            }
                            fn copied<T>(_ value: T) -> T {
                                unsafe {
                                    let p = RawPtr<T>.allocate(count: 2)
                                    defer { p.deallocate() }
                                    p.initialize(to: value)
                                    (p + 1).moveInitialize(from: p, count: 1)
                                    // Moved: the first place holds nothing now.
                                    let result = p.load(at: 1)
                                    p.advanced(by: 1).deinitialize()
                                    return result
                                }
                            }
                            @main
                            fn main() {
                                print(MemoryLayout<Int>.size)
                                print(MemoryLayout<U16>.alignment)
                                unsafe {
                                    let p = RawPtr<Int>.allocate(count: 3)
                                    p.initialize(repeating: 7, count: 3)
                                    p.store(10, at: 1)
                                    print(total(p, 3))
                                    print((p + 1).load())
                                    p.deallocate()

                                    let words = RawPtr<String>.allocate(count: 2)
                                    words.initialize(to: "raw")
                                    words.initialize(to: "memory", at: 1)
                                    print(words.load() + " " + words.move(at: 1))
                                    words.deinitialize()
                                    words.deallocate()

                                    let boxes = UnsafeBuffer<Box>.allocate(count: 2)
                                    boxes.initialize(repeating: Box(1))
                                    var view = boxes
                                    view[1] = Box(41)
                                    view[0].value += 1
                                    print(view.reduce(0) { $0 + $1.value })
                                    boxes.deinitialize()
                                    boxes.deallocate()

                                    let bytes = NativeMemory.allocate(byteCount: 16, alignment: 16, zeroed: true)
                                    bytes.storeBytes(of: 0xCAFE as U16, byteOffset: 3)
                                    print(Int(bytes.load(at: 3)))
                                    print(Int(bytes.load(as: U32.self, byteOffset: 2)))
                                    NativeMemory.deallocate(bytes)
                                }
                                print(copied("generic"))
                                print(copied(Box(5)).value)
                            }
                            """;

    const string Expected = "8\n2\n24\n10\nraw memory\n43\n254\n13303296\ngeneric\n5\n";

    [Fact]
    public void PointersAgreeSpecialised() {
        using var _ = SilGen.UseGenerics(GenericsMode.Specialised);
        Assert.Equal(Expected, AssertAgree(Pointers).Stdout);
    }

    [Fact]
    public void PointersAgreeUnspecialised() {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        Assert.Equal(Expected, AssertAgree(Pointers).Stdout);
    }

    /// <summary>
    ///     'f(move x)' hands the callee the caller's value, not a copy, and a
    ///     'consuming' parameter moved on hands it on again: the array's
    ///     buffer is the only reference to it all the way, so emptying it
    ///     keeps its room — what a BufferPool depends on.
    /// </summary>
    [Fact]
    public void AMoveIntoACallHandsOnTheValue() =>
        Assert.Equal("100\n1024\n1\n1024\n", AssertAgree("""
            module P
            fn keep(_ a: consuming U8[>..]) -> Int {
                var kept = move a
                kept.removeAll(keepingCapacity: true)
                return kept.capacity
            }
            @main
            fn main() {
                var bytes: U8[>..] = []
                bytes.reserveCapacity(100)
                bytes.append(1)
                print(keep(move bytes))
                let pool = BufferPool()
                var rented = pool.rent(minimumCapacity: 1000)
                rented.append(contentsOf: [1, 2, 3])
                print(rented.capacity)
                pool.giveBack(move rented)
                print(pool.count)
                print(pool.rent(minimumCapacity: 600).capacity)
            }
            """).Stdout);

    [Theory]
    [InlineData("let p = RawPtr<Int>.allocate(count: 1)\np.deallocate()\np.deallocate()", "freeing 0x")]
    [InlineData("let p = RawPtr<Int>.allocate(count: 1)\np.deallocate()\np.initialize(to: 1)", "used after it was freed")]
    [InlineData("let p = RawPtr<Int>.allocate(count: 1)\nprint(p.load())", "where nothing was written")]
    [InlineData("let p = RawPtr<String>.allocate(count: 1)\nprint(p.load())", "which holds none")]
    [InlineData("let p = RawPtr<Int>.allocate(count: 1)\np.initialize(to: 1, at: 1)", "run past the end of its allocation")]
    [InlineData("let p = RawPtr<U8>.allocate(count: 8)\np.cast(to: Int.self).advanced(by: 0).initialize(to: 1)\n(p + 1).cast(to: Int.self).initialize(to: 1)", "not aligned to 8")]
    [InlineData("print(RawPtr<Int>.null.load())", "a null pointer used")]
    public void TheInterpreterTrapsOnWhatIsUndefined(string body, string message) {
        var (_, trap) = Interpret($"module P\n@main\nfn main() {{\nunsafe {{\n{body}\n}}\n}}\n");
        Assert.NotNull(trap);
        Assert.Contains(message, trap, StringComparison.Ordinal);
    }
}
