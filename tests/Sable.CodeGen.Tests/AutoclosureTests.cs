using System.Diagnostics;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     '@autoclosure' parameters (#289): the argument is worked out when the
///     callee asks, and only then. Sable.Core's 'assert' is one, so a release
///     build works out none of what its call says. Each program is built for
///     a profile — 'DEBUG' or 'RELEASE' defined, as 'sable build' does —
///     and run natively and in the SIL interpreter, which must agree.
/// </summary>
public sealed class AutoclosureTests {
    /// <summary>The program's output, built for the profile: interpreted, and native when there is a C compiler, the two the same.</summary>
    static string Run(string source, bool release) {
        var tree = SyntaxTree.Parse(new SourceText(source, "test.sb"), Pipeline.ParseOptionsFor(release));
        var result = Pipeline.Compile([tree], optimised: release);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var interpreter = new SilInterpreter(result.Sil!, 10_000_000);
        interpreter.Run(result.Sil!.EntryPoint!);
        Assert.True(interpreter.LiveObjects == 0, $"the interpreter found {interpreter.LiveObjects} object(s) leaked");
        var output = interpreter.Output.ToString();
        if (!CanBuild) {
            return output;
        }

        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, release);
            Assert.True(build.Succeeded, build.Output);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stderr);
            Assert.Equal(output, stdout.Result);
        } finally {
            Platform.DeleteExecutable(executable);
        }

        return output;
    }

    const string Counted = """
        module Test

        var evaluated = 0

        fn counted(_ value: Bool) -> Bool {
            evaluated = evaluated + 1
            return value
        }

        fn described(_ text: String) -> String {
            evaluated = evaluated + 100
            return text
        }

        @main
        fn main() {
            var n = 2
            assert(counted(n < 3), described("n is \(n)"))
            n = n + 1
            precondition(counted(n == 3), described("never built: the condition holds"))
            if n > 3 {
                assertionFailure(described("not reached"))
            }
            print(n)
            print(evaluated)
        }
        """;

    /// <summary>
    ///     A debug build works out an assertion's condition, and its message
    ///     only when the condition fails, which here it does not; a release
    ///     build works out neither. A precondition's condition is worked out
    ///     in both, and its message in neither.
    /// </summary>
    [Theory]
    [InlineData(false, "3\n2\n")]
    [InlineData(true, "3\n1\n")]
    public void AnAssertionsConditionIsWorkedOutInADebugBuildOnly(bool release, string expected) =>
        Assert.Equal(expected, Run(Counted, release));

    /// <summary>
    ///     An '@autoclosure' parameter is worked out each time the callee
    ///     calls it, and never when it does not: a generic one, one with a
    ///     default, one forwarded by calling it, and an '@escaping' one kept
    ///     to call later. The same in either build.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnArgumentIsWorkedOutWhenTheCalleeAsks(bool release) =>
        Assert.Equal(
            "1\nname is x\n0\n1\n5\n7\n2\n2\n9\n3\ntrue\n5\n",
            Run(
                """
                module Test

                var evaluated = 0

                fn bump(_ value: Int) -> Int {
                    evaluated = evaluated + 1
                    return value
                }

                fn check(_ ok: @autoclosure () -> Bool, _ why: @autoclosure () -> String = "no reason") -> Int {
                    if ok() {
                        return 1
                    }
                    print(why())
                    return 0
                }

                fn either<T>(_ first: T?, or fallback: @autoclosure () -> T) -> T {
                    if let value = first {
                        return value
                    }
                    return fallback()
                }

                class Later {
                    var make: () -> Int
                    init(_ make: @autoclosure @escaping () -> Int) {
                        self.make = make
                    }
                }

                fn twice(_ ok: @autoclosure () -> Bool) -> Bool {
                    return check(ok()) == 1 && check(ok()) == 1
                }

                @main
                fn main() {
                    var hits = 0
                    let name = "x"
                    print(check(hits == 0, "name is \(name)"))
                    hits = hits + 1
                    print(check(hits == 0, "name is \(name)"))
                    print(check(bump(hits) == 1))
                    print(either(5, or: bump(7)))
                    print(either(none, or: bump(7)))
                    print(evaluated)
                    let later = Later(bump(9))
                    print(evaluated)
                    print(later.make())
                    print(evaluated)
                    print(twice(bump(1) == 1))
                    print(evaluated)
                }
                """,
                release
            )
        );

    /// <summary>
    ///     A parameter whose type lets it throw takes an argument that throws,
    ///     marked inside it; the error comes out of the call that asked.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AThrowingAutoclosureThrowsWhereItIsCalled(bool release) =>
        Assert.Equal(
            "4\ncaught\n0\n",
            Run(
                """
                module Test

                union Bad { Odd }

                fn half(_ n: Int) throws(Bad) -> Int {
                    if n % 2 == 1 {
                        throw Bad.Odd
                    }
                    return n / 2
                }

                fn orZero(_ value: @autoclosure () throws(Bad) -> Int) -> Int {
                    do {
                        return try value()
                    } catch {
                        print("caught")
                        return 0
                    }
                }

                @main
                fn main() {
                    print(orZero(try half(8)))
                    print(orZero(try half(3)))
                }
                """,
                release
            )
        );
}
