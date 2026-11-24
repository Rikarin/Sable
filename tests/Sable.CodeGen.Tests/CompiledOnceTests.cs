using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What generic code compiled once serves (#297 stage 4): each program
///     runs natively and in the interpreter, leak-checked, with its generic
///     functions compiled once and specialised, and — in CI
///     (<c>SABLE_TEST_NATIVE=all</c>) — optimised and built in units; all
///     must print the same, and the functions named must have been compiled once.
/// </summary>
[Native]
public sealed class CompiledOnceTests {
    static void Prints(string expected, string program, params string[] once) {
        foreach (var mode in (GenericsMode[])[GenericsMode.Unspecialised, GenericsMode.Specialised]) {
            using var _ = SilGen.UseGenerics(mode);
            Assert.Equal((mode, expected), (mode, AssertAgree(program).Stdout));
            if (CanBuild && NativeMode.All) {
                var separate = BuildSeparatelyAndRun(program);
                Assert.Equal((mode, expected, 0), (mode, separate.Stdout + separate.Stderr, separate.ExitCode));
            }

            if (mode == GenericsMode.Unspecialised) {
                var result = Pipeline.Compile(new SourceText(program, "test.sb"), optimised: false);
                Assert.All(once, f => Assert.True(result.Sil!.Find(f + "$generic") is not null, $"{f} was not compiled once"));
            }
        }
    }

    /// <summary>A generic struct that holds its type parameters' values by value: its initialisers, memberwise, its own, throwing and delegating.</summary>
    [Fact]
    public void AStructHoldingItsParametersByValueIsInitialised() => Prints(
        "xy 4 3 made\n3 2 3 both\n7 s checked\nbad 9\nhello! hello!\ntrue true\n",
        """
            module Test

            struct Bad: Error { var n: Int }

            struct Pair<A, B> {
                var a: A
                var b: B
                var n: Int = 3
                var note: String

                init(_ a: A, _ b: B) {
                    self.a = a
                    self.b = b
                    self.note = "made"
                }

                init(checked a: A, _ b: B, _ ok: Bool) throws {
                    self.a = a
                    if !ok {
                        throw Bad { n: 9 }
                    }

                    self.b = b
                    self.note = "checked"
                }

                init(both a: A, _ b: B) {
                    self.init(a, b)
                    self.note = "both"
                }
            }

            struct Wrap<T> {
                var value: T
                var extra: T?
            }

            fn pair<A, B>(_ a: A, _ b: B) -> Pair<A, B> => Pair<A, B>(a, b)

            fn both<A, B>(_ a: A, _ b: B) -> Pair<A, B> => Pair<A, B>(both: a, b)

            fn checked<A, B>(_ a: A, _ b: B, _ ok: Bool) throws -> Pair<A, B> => try Pair<A, B>(checked: a, b, ok)

            fn wrap<T>(_ t: T) -> Wrap<T> => Wrap(value: t, extra: t)

            class Thing {
                var n: Int
                init(_ n: Int) {
                    self.n = n
                }
            }

            @main
            fn main() {
                let p = pair("x" + "y", 4)
                print("\(p.a) \(p.b) \(p.n) \(p.note)")
                let q = both(Thing(3), [1, 2])
                print("\(q.a.n) \(q.b.count) \(q.n) \(q.note)")
                for ok in [true, false] {
                    do {
                        let r = try checked(Thing(7), "s", ok)
                        print("\(r.a.n) \(r.b) \(r.note)")
                    } catch let bad as Bad {
                        print("bad \(bad.n)")
                    } catch {
                        print("other")
                    }
                }

                let w = wrap("hello" + "!")
                print("\(w.value) \(w.extra ?? "none")")
                let v = wrap(true)
                print("\(v.value) \(v.extra ?? false)")
            }
        """,
        "Test.pair", "Test.both", "Test.checked", "Test.wrap"
    );
}
