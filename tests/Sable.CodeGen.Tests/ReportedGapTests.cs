using Sable.SIL;

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

    /// <summary>A requirement with 'some P' parameters called through 'any Q', and through a type parameter: the argument's table of P is made (#341).</summary>
    [Fact]
    public void ASomeParameterThroughAnExistential() =>
        Assert.Equal(
            """
            a
            b
            m
            xy2
            2
            h
            3
            g

            """,
            Programs.AssertAgree("""
            module SomeThroughAny

            protocol Named {
                var name: String { get }
            }

            struct Plain: Named {
                var name: String
            }

            final class Boxed: Named {
                let name: String

                init(_ name: String) {
                    self.name = name
                }
            }

            protocol Store {
                fn put(_ key: some Named) -> String
                fn both(_ a: some Named, _ b: some Named & Sendable, times: Int) -> String
                mutating fn add(_ key: some Named)
                var count: Int { get }
            }

            struct Memory: Store {
                var keys: String[>..] = []

                fn put(_ key: some Named) -> String {
                    return key.name
                }

                fn both(_ a: some Named, _ b: some Named & Sendable, times: Int) -> String {
                    return "\(a.name)\(b.name)\(times)"
                }

                mutating fn add(_ key: some Named) {
                    keys.append(key.name)
                }

                var count: Int {
                    get => keys.count
                }
            }

            final class Holder {
                var store: any Store

                init(_ store: any Store) {
                    self.store = store
                }
            }

            fn through(_ s: any Store) -> String {
                return s.put(Boxed("b"))
            }

            fn made() -> any Store {
                return Memory()
            }

            fn viaGeneric<S: Store>(_ s: S) -> String {
                return s.put(Plain(name: "g"))
            }

            @main
            fn main() {
                var s: any Store = Memory()
                print(s.put(Plain(name: "a")))
                print(through(s))
                print(made().put(Plain(name: "m")))
                print(s.both(Plain(name: "x"), Plain(name: "y"), times: 2))
                s.add(Plain(name: "k"))
                s.add(Boxed("j"))
                print(s.count)
                let holder = Holder(s)
                print(holder.store.put(Plain(name: "h")))
                holder.store.add(Plain(name: "i"))
                print(holder.store.count)
                print(viaGeneric(Memory()))
            }
            """).Stdout);

    /// <summary>A type's own member does not hide its protocol extension's of the same labels and other types: both are overloads.</summary>
    [Fact]
    public void AnOwnMemberDoesNotHideAnOverloadOfOtherTypes() =>
        Assert.Equal(
            """
            ext a false
            ext b true
            ext1 c true
            d false
            ext v e

            """,
            Programs.AssertAgree("""
            module Hiding

            import Sable.IO

            protocol Store {
                fn make(_ path: FilePath, recursive: Bool) -> String
            }

            extension Store {
                fn make(_ path: String, recursive: Bool = false) -> String {
                    return "ext " + make(FilePath(path), recursive: recursive)
                }

                fn make(_ path: FilePath) -> String {
                    return "ext1 " + make(path, recursive: true)
                }
            }

            final class Memory: Store {
                fn make(_ path: FilePath, recursive: Bool) -> String {
                    return "\(path.string!) \(recursive)"
                }
            }

            struct Value: Store {
                fn make(_ path: FilePath, recursive: Bool) -> String {
                    return "v \(path.string!)"
                }
            }

            @main
            fn main() {
                let m = Memory()
                print(m.make("a"))
                print(m.make("b", recursive: true))
                print(m.make(FilePath("c")))
                print(m.make(FilePath("d"), recursive: false))
                print(Value().make("e"))
            }
            """).Stdout);

    /// <summary>A value moved into a 'consuming T' parameter is the callee's, compiled once or specialised, optimised or not (#342).</summary>
    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void AValueMovedIntoAConsumingParameterIsNotCopied(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        const string expected = """
            kept 500
            [1, 2, 3]
            [1, 2]
            gone a
            1
            1
            gone c
            c
            gone d
            d
            gone e
            e
            text
            7
            end b
            gone b

            """;
        const string source = """
            module ConsumingMove

            final class Tracked {
                let name: String

                init(_ name: String) {
                    self.name = name
                }

                deinit {
                    print("gone \(name)")
                }
            }

            fn reset<T>(_ value: consuming T, _ change: (inout T) -> Void) -> T {
                var kept = move value
                change(&kept)
                return kept
            }

            fn dropped<T>(_ value: consuming T) -> Int {
                return 1
            }

            fn kept<T>(_ value: consuming T) -> T {
                return value
            }

            fn passOn<T>(_ value: consuming T) -> T {
                return kept(move value)
            }

            fn twice<T>(_ value: T) -> T {
                return kept(value)
            }

            @main
            fn main() {
                var list: Int[>..] = []
                list.reserveCapacity(500)
                list.append(1)
                let back = reset(move list) { $0.removeAll(keepingCapacity: true) }
                print("kept \(back.capacity >= 500 ? 500 : back.capacity)")
                let copied: Int[>..] = [1, 2]
                print(reset(copied) { $0.append(3) })
                print(copied)
                print(dropped(Tracked("a")))
                let b = Tracked("b")
                print(dropped(b))
                print(kept(Tracked("c")).name)
                print(passOn(Tracked("d")).name)
                print(twice(Tracked("e")).name)
                print(kept("text"))
                print(kept(7))
                print("end \(b.name)")
            }
            """;
        Assert.Equal(expected, Programs.AssertAgree(source).Stdout);
        if (Programs.CanBuild) {
            Assert.Equal(expected, Programs.BuildAndRun(source, optimise: false).Stdout);
        }
    }
}
