using Sable.Symbols;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Programs started and left running (Sable.Process over runtime/process.c),
///     natively and interpreted, on every system: POSIX's shell and tools, and
///     on Windows cmd and what it comes with. The library's own tests
///     (tests/lib/Sable.Process) say more, of POSIX alone.
/// </summary>
[Native]
public sealed class ProcessTests {
    static bool Windows => OperatingSystem.IsWindows();

    static string System32(string name) => Platform.Literal(Path.Combine(Environment.SystemDirectory, name));

    /// <summary>A shell's command, run: what each system calls its own.</summary>
    static string Shell(string posix, string windows) =>
        Windows ? $"Command(\"{System32("cmd.exe")}\").arguments([\"/c\", \"{windows}\"])" : $"Command(\"sh\").arguments([\"-c\", \"{posix}\"])";

    /// <summary>What it reads, written.</summary>
    static string Echo => Windows ? $"Command(\"{System32("find.exe")}\").arguments([\"/v\", \"\"])" : "Command(\"cat\")";

    /// <summary>A minute's wait.</summary>
    static string Sleep => Windows ? $"Command(\"{System32("ping.exe")}\").arguments([\"-n\", \"60\", \"127.0.0.1\"])" : "Command(\"sleep\").argument(\"60\")";

    static string Async(string body) => $$"""
        module P
        import Sable.Concurrency
        import Sable.Process
        import Sable.Time

        @main
        fn main() async throws {
        {{body}}
        }
        """;

    [Fact]
    public void OutputGivesWhatWasWrittenAndHowItEnded() =>
        Assert.Equal(
            "out\nerr\nexit status 3\nfalse\n",
            AssertAgree(Async($$"""
                let done = try await {{Shell("echo out; echo err >&2; exit 3", "echo out& echo err 1>&2& exit 3")}}.output()
                print(done.output.trimmed())
                print(done.errors.trimmed())
                print(done.status.description())
                print(done.succeeded)
                """)).Stdout
        );

    /// <summary>More than a pipe holds to both streams, and as much read: no side waits on another.</summary>
    [Fact]
    public void ALargeOutputDoesNotDeadlock() =>
        Assert.Equal(
            "true\ntrue\n",
            AssertAgree(Async($$"""
                var line = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\n"
                var text = ""
                var i = 0
                while i < 5000 {
                    text += line
                    i += 1
                }
                let done = try await {{Echo}}.output(input: text)
                print(done.output.replacing("\r", with: "") == text)
                print(try Process.run({{(Windows ? $"\"{System32("find.exe")}\", [\"/v\", \"\"]" : "\"cat\", []")}}, input: text).output.replacing("\r", with: "") == text)
                """), stepLimit: 200_000_000).Stdout
        );

    /// <remarks>Whole lines ended as Windows ends them: its find.exe takes "a\nb\n" for UTF-16.</remarks>
    [Fact]
    public void AChildsStreamsAreReadAndWrittenAsItRuns() =>
        Assert.Equal(
            "true\nthe first line\nthe second line\ntrue\n",
            AssertAgree(Async($$"""
                let child = try {{Echo}}.stdin(.Piped).stdout(.Piped).spawn()
                print(child.tryWait() == none && child.stderr == none)
                try await child.stdin!.write("the first line\r\n")
                try await child.stdin!.write("the second line\r\n")
                child.stdin!.close()
                for try await line in child.stdout!.lines() {
                    print(line.trimmed())
                }
                print(try await child.wait().success)
                """)).Stdout
        );

    [Fact]
    public void AWaitTimesOutAndTheChildIsKilled() =>
        Assert.Equal(
            "timed out\ntrue\nfalse\nfalse\n",
            AssertAgree(Async($$"""
                let child = try {{Sleep}}.stdout(.Null).spawn()
                do {
                    let _ = try await withTimeout(Duration.milliseconds(50)) { try await child.wait() }
                } catch let error as TimeoutError {
                    print("timed out")
                }
                print(child.kill())
                print(try await child.wait().success)
                print(child.kill())
                """)).Stdout
        );

    /// <summary>What the child started ends with it: a pipe it holds is at its end.</summary>
    [Fact]
    public void KillingTheTreeEndsWhatItStarted() =>
        Assert.Equal(
            "ready\ntrue\nfalse\nfalse\n",
            AssertAgree(Async($$"""
                let child = try {{Shell("echo ready; sleep 60; echo no", Windows ? $"echo ready& {System32("ping.exe")} -n 60 127.0.0.1 >nul& echo no" : "")}}.stdout(.Piped).spawn()
                var lines = child.stdout!.lines().makeAsyncIterator()
                print(try await lines.next()!)
                print(child.kill(tree: true))
                print(try await lines.next() != none)
                print(try await child.wait().success)
                """)).Stdout
        );

    [Fact]
    public void TheEnvironmentIsWhatIsSet() =>
        Assert.Equal(
            "yes\nnothing\n",
            AssertAgree(Async($$"""
                let done = try await {{Shell("echo $SABLE_PROCESS_SET", "echo %SABLE_PROCESS_SET%")}}.environment("SABLE_PROCESS_SET", "yes").output()
                print(done.output.trimmed())
                let cleared = try await {{Shell("echo ${SABLE_PROCESS_INHERITED-nothing}", "if defined SABLE_PROCESS_INHERITED (echo something) else (echo nothing)")}}.clearEnvironment().output()
                print(cleared.output.trimmed())
                """), environment: new Dictionary<string, string> { ["SABLE_PROCESS_INHERITED"] = "1" }).Stdout
        );

    [Fact]
    public void ItStartsInItsDirectory() {
        var root = Windows ? Path.GetPathRoot(Environment.SystemDirectory)! : "/";
        Assert.Equal(
            $"{root}\nno directory 'surely-not-here' to start a program in\n",
            AssertAgree(Async($$"""
                let done = try await {{Shell("pwd", "cd")}}.workingDirectory("{{Platform.Literal(root)}}").output()
                print(done.output.trimmed())
                do {
                    let _ = try {{Echo}}.workingDirectory("surely-not-here").spawn()
                } catch {
                    print(error.description())
                }
                """)).Stdout
        );
    }

    [Fact]
    public void WhatIsNoProgramIsNotFound() =>
        Assert.Equal(
            "no program 'surely-not-a-program' could be started\n",
            AssertAgree(Async("""
                do {
                    let _ = try Command("surely-not-a-program").spawn()
                } catch {
                    print(error.description())
                }
                """)).Stdout
        );

    /// <summary>Each argument is read back as it was given, whatever is in it: by this program itself, started again.</summary>
    [Fact]
    public void ArgumentsAreGivenAsTheyAre() {
        if (!Harnesses.CanBuild) {
            return;
        }

        // Only a native program can be started again.
        var run = BuildAndRun(Async("""
            let given = Environment.arguments()
            if !given.isEmpty {
                for argument in given {
                    print("<" + argument + ">")
                }
                return
            }
            let odd: String[>..] = ["two words", "", "\"", "a\"b", "back\\slash\\", "end \\", "\\\"", "\\\\\"q\\\\", "tab\there", "%PATH%", "^&|<>", "*", "ünï"]
            let done = try await Command("\(Environment.executablePath!)").arguments(odd).output()
            var expected = ""
            for argument in odd {
                expected += "<" + argument + ">\n"
            }
            print(done.output == expected ? "same" : done.output)
            """).Replace("import Sable.Process", "import Sable.IO\nimport Sable.Process", StringComparison.Ordinal));
        Assert.Equal("", run.Stderr);
        Assert.Equal("same\n", run.Stdout);
    }

    [Fact]
    public void ASignalRaisedComes() =>
        Assert.Equal(
            "INT\nTERM\n",
            AssertAgree(Async("""
                let signals = try Signals.on([.Interrupt, .Terminate])
                Signals.raise(.Interrupt)
                Signals.raise(.Terminate)
                var left = 2
                for await signal in signals {
                    print(signal.name)
                    left -= 1
                    if left == 0 {
                        break
                    }
                }
                """)).Stdout
        );

    [Fact]
    public void WaitingForASignalKeepsNoProgramGoing() =>
        Assert.Equal(
            "done\n",
            AssertAgree(Async("""
                let signals = try Signals.on(.Interrupt)
                let _ = Task.detached {
                    for await signal in signals {
                        print(signal.name)
                    }
                }
                await Task.yield()
                print("done")
                """), environment: OneThread).Stdout
        );

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(255)]
    public void ExitEndsTheProgramWithTheStatus(int status) {
        var run = AssertAgree($$"""
            module P
            import Sable.Process

            class Alive {
                deinit { print("not said") }
            }

            @main
            fn main() {
                let alive = Alive()
                print("said")
                Process.exit({{status}})
            }
            """);
        Assert.Equal((status, "said\n", ""), (run.ExitCode, run.Stdout, run.Stderr));
    }

    /// <summary>From a task on another thread too, with others running.</summary>
    [Fact]
    public void ExitEndsTheProgramFromATask() {
        var run = AssertAgree(Async("""
            let running = Task.spawn {
                print("said")
                Process.exit(5)
            }
            await running.value
            """));
        Assert.Equal((5, "said\n"), (run.ExitCode, run.Stdout));
    }

    [Fact]
    public void AbortEndsTheProgramAsATrapDoes() {
        var run = AssertAgree("""
            module P
            import Sable.Process

            @main
            fn main() {
                Process.abort()
            }
            """);
        Assert.Equal((134, ""), (run.ExitCode, run.Stderr));
    }

    /// <summary>
    ///     Overloads told apart by what an argument's shape alone says: an
    ///     array literal of leading dots, and a leading dot, which wait for
    ///     the parameter to say what they are. (Signals.on takes either.)
    /// </summary>
    [Fact]
    public void AnArrayLiteralOfLeadingDotsIsTheArraysOverload() =>
        Assert.Equal(
            "1\n2\n2\n3\n4\n5\n2\n",
            AssertAgree("""
                module P
                union Colour { Red, Mixed(of: Int) }
                fn f(_ c: Colour) -> Int { return 1 }
                fn f(_ c: Colour[>..]) -> Int { return 2 }
                fn f(_ c: Int, _ d: Colour?) -> Int { return 3 }
                fn f(_ c: Int, _ d: Colour[>..]?) -> Int { return 4 }
                fn f(_ c: Int, either d: Colour?) -> Int { return 5 }
                fn g(_ cs: Colour...) -> Int { return cs.count }
                @main
                fn main() {
                    print(f(.Red))
                    print(f([.Red, .Mixed(of: 2)]))
                    print(f([]))
                    print(f(1, .Red))
                    print(f(1, [.Red]))
                    print(f(1, either: none))
                    print(g(.Red, .Mixed(of: 2)))
                }
                """).Stdout
        );
}
