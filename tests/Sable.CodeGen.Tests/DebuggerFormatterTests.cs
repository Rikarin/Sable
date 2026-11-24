using System.Diagnostics;
using System.Text.RegularExpressions;
using Sable.Driver.Commands;

namespace Sable.CodeGen.Tests;

/// <summary>
///     docs/04 Phase 5: "DWARF plus an lldb formatter plugin for String, List,
///     unions, and existentials". Each program is built with <c>-g</c> and run
///     under the real lldb with <c>tools/lldb/sable_lldb.py</c> loaded; the
///     tests read what <c>frame variable</c> prints, and that <c>next</c>,
///     <c>step</c> and <c>finish</c> go where the source says.
/// </summary>
/// <remarks>
///     Needs clang and an lldb that can run a program here — not, for one,
///     an x64 container emulated on arm64, where nothing can be traced.
///     Without them every test passes vacuously.
/// </remarks>
public sealed partial class DebuggerFormatterTests {
    const string Values = """
                          module Values
                          import Sable.Collections

                          struct Point {
                              var x: Int
                              var y: Int
                          }

                          struct Line {
                              var from: Point
                              var to: Point
                              var name: String
                          }

                          union Shape {
                              Circle(radius: F64)
                              Rect(width: Int, height: Int)
                              Named(label: String)
                              Empty
                          }

                          enum Direction { North, East, South, West }

                          protocol Named {
                              fn name() -> String
                          }

                          struct Dog: Named {
                              var breed: String
                              fn name() -> String { return breed }
                          }

                          open class Animal: Named {
                              var legs: Int
                              init(_ legs: Int) {
                                  self.legs = legs
                              }
                              fn name() -> String { return "animal" }
                          }

                          class Bird: Animal {
                              init() {
                                  super.init(2)
                              }
                          }

                          class Node {
                              var value: Int
                              var label: String
                              var next: Node?
                              init(_ value: Int, _ label: String) {
                                  self.value = value
                                  self.label = label
                                  self.next = none
                              }
                          }

                          @main
                          fn main() {
                              let greeting = "hello, world"
                              let blank = ""
                              let numbers: Int[>..] = [10, 20, 30]
                              let names: String[>..] = ["ann", "bob"]
                              let nothingYet: Int[>..] = []
                              let middle = numbers[1...]
                              let noneOfThem: Int[:] = []
                              let circle = Shape.Circle(radius: 1.5)
                              let rect = Shape.Rect(width: 2, height: 3)
                              let tagged = Shape.Named(label: "box")
                              let hollow = Shape.Empty
                              let heading = Direction.West
                              let answer: Int? = 42
                              let absent: Int? = none
                              let somePoint: Point? = Point { x: 5, y: 6 }
                              let maybeText: String? = "maybe"
                              let pet: any Named = Dog { breed: "collie" }
                              let bird: any Named = Bird()
                              let node = Node(7, "seven")
                              let noNode: Node? = none
                              let someNode: Node? = node
                              let line = Line { from: Point { x: 1, y: 2 }, to: Point { x: 3, y: 4 }, name: "diagonal" }
                              let pair = (1, "one")
                              let letter: Char = 'x'
                              let scale = 3
                              let times = { (a: Int) -> Int in a * scale }
                              var ages: [String: Int] = [:]
                              ages["ann"] = 31
                              var map: [String: Int] = ["one": 1, "gone": 0, "two": 2]
                              map["gone"] = none
                              print(times(numbers.count)) // stop
                          }
                          """;

    const string Steps = """
                         module Steps

                         fn triple(_ n: Int) -> Int {
                             let doubled = n * 2
                             let tripled = doubled + n
                             return tripled
                         }

                         @main
                         fn main() {
                             var total = 0
                             total = total + 1
                             let t = triple(total)
                             total = total + t
                             print(total)
                         }
                         """;

    const string Calls = """
                         module Calls

                         protocol Named {
                             fn name() -> String
                         }

                         struct Dog: Named {
                             var breed: String
                             fn name() -> String {
                                 return breed
                             }
                         }

                         @main
                         fn main() {
                             let pet: any Named = Dog { breed: "collie" }
                             var count = 2
                             let label = "sum"
                             let add = { (a: Int) -> Int in
                                 count = count + a
                                 return count
                             }
                             print(pet.name())
                             print(label + " \(add(3))")
                         }
                         """;

    /// <summary>Why the tests that run lldb are skipped on Windows.</summary>
    const string NeedsLldb = "lldb's Python formatter plugin and these sessions are for lldb on macOS and Linux; a Windows build is described in CodeView, for the PDB its debuggers read, which no test here drives";

    static readonly Lazy<string?> Lldb = new(FindLldb);

    /// <summary>An lldb that can launch and stop a program here; null when there is none.</summary>
    static string? FindLldb() {
        if (!Programs.CanBuild || OperatingSystem.IsWindows()) {
            return null;
        }

        var lldb = Environment.GetEnvironmentVariable("LLDB") is { Length: > 0 } configured ? configured : "lldb";
        try {
            using var process = Process.Start(new ProcessStartInfo(lldb, "--version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
            if (process.ExitCode != 0) {
                return null;
            }
        } catch (System.ComponentModel.Win32Exception) {
            return null;
        }

        // Tracing may be impossible even so: try it on the smallest program.
        var probe = Run(lldb, "module Probe\n@main\nfn main() {\n    print(1)\n}\n", "probe.sb", ["b probe.sb:4", "run", "kill"]).GetAwaiter().GetResult();
        return probe.Contains("stop reason = breakpoint", StringComparison.Ordinal) ? lldb : null;
    }

    /// <summary>Builds the program with debug information and runs lldb on it, with the plugin loaded; returns what it printed.</summary>
    /// <param name="separately">
    ///     Built as a package's dev build is (<see cref="Sable.Driver.Build.IncrementalBuild" />):
    ///     the program's unit unoptimised, linked with the standard library's
    ///     archive, compiled optimised (#298). Otherwise the program is
    ///     compiled whole, the library's code with it, unoptimised.
    /// </param>
    static async Task<string> Run(string lldb, string source, string fileName, IReadOnlyList<string> commands, bool separately = false) {
        var directory = Directory.CreateTempSubdirectory("sable-formatters-");
        try {
            var sourcePath = Path.Combine(directory.FullName, fileName);
            File.WriteAllText(sourcePath, source);

            var executable = Path.Combine(directory.FullName, Path.GetFileNameWithoutExtension(fileName));
            if (separately) {
                var outcome = Sable.Driver.Build.IncrementalBuild.Build([new(Path.GetFileNameWithoutExtension(fileName), [new(sourcePath, fileName, [])])],
                    new("dev", false, directory.FullName, true), executable, Path.Combine(directory.FullName, "cache"), cache: new BuildCache(Path.Combine(directory.FullName, "shared")));
                Assert.True(outcome.Succeeded, string.Join("\n", outcome.Errors));
                Assert.Null(outcome.FellBack);
            } else {
                var result = Pipeline.Compile(new SourceText(source, fileName), directory.FullName);
                Assert.True(result.Succeeded, string.Join("\n", result.Errors));

                var build = NativeBuild.Build(result.LlvmIr!, executable, debug: true);
                Assert.True(build.Succeeded, build.Output);
            }

            var plugin = Path.Combine(directory.FullName, "sable_lldb.py");
            File.WriteAllText(plugin, NativeBuild.LldbPluginSource);

            var start = new ProcessStartInfo(lldb) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.FullName };
            foreach (var argument in (string[]) [
                         "--batch",
                         "--one-line-before-file", "settings set target.load-script-from-symbol-file false",
                         // A container may not let lldb turn address randomisation off.
                         "--one-line-before-file", "settings set target.disable-aslr false",
                         "--one-line-before-file", $"command script import \"{plugin}\"",
                     ]) {
                start.ArgumentList.Add(argument);
            }

            foreach (var command in commands) {
                start.ArgumentList.Add("-o");
                start.ArgumentList.Add(command);
            }

            start.ArgumentList.Add("--");
            start.ArgumentList.Add(executable);

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try {
                await process.WaitForExitAsync(timeout.Token);
            } catch (OperationCanceledException) {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return await output + await errors;
        } finally {
            directory.Delete(true);
        }
    }

    static int LineOf(string source, string marker) =>
        source.Split('\n').Select((text, index) => (text, index)).First(l => l.text.Contains(marker, StringComparison.Ordinal)).index + 1;

    /// <summary>The program's variables where it stops, as <c>frame variable</c> shows them; null when there is no lldb.</summary>
    static readonly Lazy<string?> ValuesOutput = new(() =>
        Lldb.Value is not { } lldb
            ? null
            : Run(lldb, Values, "values.sb", [$"b values.sb:{LineOf(Values, "// stop")}", "run", "frame variable", "kill"]).GetAwaiter().GetResult());

    static void Shows(params string[] expected) {
        if (ValuesOutput.Value is not { } output) {
            return;
        }

        foreach (var text in expected) {
            Assert.Contains(text, output, StringComparison.Ordinal);
        }
    }

    [PosixFact(NeedsLldb)]
    public void StringsShowTheirText() =>
        Shows("(String) greeting = \"hello, world\"", "(String) blank = \"\"", "(String?) maybeText = \"maybe\"");

    [PosixFact(NeedsLldb)]
    public void ListsShowTheirCountAndElements() =>
        Shows(
            "([Int]) numbers = count = 3 {\n  [0] = 10\n  [1] = 20\n  [2] = 30\n}",
            "([String]) names = count = 2 {\n  [0] = \"ann\"\n  [1] = \"bob\"\n}",
            "([Int]) nothingYet = count = 0"
        );

    /// <summary>A slice shows its own run of the buffer it views: its count, and its elements from its start.</summary>
    [PosixFact(NeedsLldb)]
    public void SlicesShowTheirCountAndElements() =>
        Shows(
            "([Int:]) middle = count = 2 {\n  [0] = 20\n  [1] = 30\n}",
            "([Int:]) noneOfThem = count = 0"
        );

    [PosixFact(NeedsLldb)]
    public void UnionsShowTheirCaseAndPayload() =>
        Shows(
            "(Values.Shape) circle = Circle(radius: 1.5)",
            "(Values.Shape) rect = Rect(width: 2, height: 3)",
            "(Values.Shape) tagged = Named(label: \"box\")",
            "(Values.Shape) hollow = Empty",
            "(Values.Direction) heading = West"
        );

    [PosixFact(NeedsLldb)]
    public void OptionalsShowTheirValueOrNone() =>
        Shows(
            "(Int?) answer = 42",
            "(Int?) absent = none",
            "(Values.Point?) somePoint = (x: 5, y: 6)",
            "(Values.Node?) noNode = none",
            "(Values.Node?) someNode = Values.Node {"
        );

    [PosixFact(NeedsLldb)]
    public void ExistentialsShowTheDynamicTypeAndValue() =>
        Shows(
            "(any Values.Named) pet = Values.Dog {\n  [value] = (breed = \"collie\")\n}",
            // A class held in one shows as the class it is, not the one it was boxed as.
            "(any Values.Named) bird = Values.Bird {\n  [value] = Values.Bird {\n    legs = 2\n"
        );

    [PosixFact(NeedsLldb)]
    public void ClassInstancesShowTheirFieldsAndReferenceCount() =>
        Shows("(Values.Node) node = Values.Node {\n  value = 7\n  label = \"seven\"\n  next = none\n  [strong] = 2\n}");

    [PosixFact(NeedsLldb)]
    public void NestedStructsShowFieldByField() =>
        Shows("(Values.Line) line = {\n  from = (x = 1, y = 2)\n  to = (x = 3, y = 4)\n  name = \"diagonal\"\n}");

    [PosixFact(NeedsLldb)]
    public void TuplesCharactersAndClosures() =>
        Shows("((Int, String)) pair = (1, \"one\")", "(Char) letter = 'x'", "((Int) -> Int) times = Values.main$closure0");

    [PosixFact(NeedsLldb)]
    public void DictionariesShowTheirEntriesByKey() =>
        Shows(
            "(Sable.Core.Dictionary<String, Int>) ages = count = 1 {\n  [\"ann\"] = 31\n}",
            "(Sable.Core.Dictionary<String, Int>) map = count = 2 {\n  [\"one\"] = 1\n  [\"two\"] = 2\n}"
        );

    [PosixFact(NeedsLldb)]
    public async Task NextStepsLineByLineAndFinishReturns() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        var output = await Run(lldb, Steps, "steps.sb", ["b steps.sb:11", "run", "next", "next", "step", "next", "frame variable", "finish", "next", "next", "frame variable total", "kill"]);

        // 'next' goes one statement at a time, 'step' into the call.
        var stops = System.Text.RegularExpressions.Regex.Matches(output, @"frame #0: .*? at steps\.sb:(\d+)").Select(static m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal([11, 12, 13, 4, 5], stops.Take(5));
        Expect(output, "Steps.triple(n=1) at steps.sb:4");
        Expect(output, "(Int) doubled = 2");

        // 'finish' comes back to the caller with the result.
        Expect(output, "Return value: (Int) $0 = 3");
        Expect(output, "(Int) total = 4");
    }

    /// <summary>That the session shows the text, and the whole session when it does not: a debugger's output says why.</summary>
    static void Expect(string output, string expected) =>
        Assert.True(output.Contains(expected, StringComparison.Ordinal), $"lldb did not show: {expected}\n--- the session ---\n{output}");

    /// <summary>
    ///     A dev build links the standard library's archive compiled optimised
    ///     (#298), with its line tables: a breakpoint in the library's code
    ///     stops there, the backtrace names its frame and the program's, and
    ///     the program's frame shows its values through the formatters.
    /// </summary>
    [PosixFact(NeedsLldb)]
    public async Task ADevBuildStopsInTheOptimisedLibraryAndShowsTheProgramsValues() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        const string source = """
                              module Library
                              import Sable.IO

                              fn main() {
                                  let words: String[>..] = ["pear", "fig"]
                                  let loud = "sable".uppercased()
                                  print("\(loud) \(words.count)")
                              }
                              """;
        var output = await Run(lldb, source, "library.sb", ["br set -r mappingCase", "run", "bt", "up", "frame variable words", "kill"], separately: true);

        Expect(output, "stop reason = breakpoint");
        // lldb shows the file by name, or (a newer lldb, which writes the
        // source the line table carries to a file of its own) as
        // '-23b16d.String+Case.sb'.
        Expect(output, "String+Case.sb:");

        // The mark comes from DW_AT_APPLE_optimized, which LLVM writes for
        // Apple's targets only.
        if (OperatingSystem.IsMacOS()) {
            Expect(output, "[opt]");
        }
        Expect(output, "Library.main at library.sb:6");
        Expect(output, "[0] = \"pear\"");
    }

    [PosixFact(NeedsLldb)]
    public async Task StepGoesThroughAnExistentialIntoTheMethod() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        var output = await Run(lldb, Calls, "calls.sb", ["b calls.sb:23", "run", "step", "bt", "finish", "kill"]);

        // The compiler's thunk between them is in the backtrace, but is not stopped in.
        Expect(output, "Calls.Dog.name(self=(breed = \"collie\")) at calls.sb:10");
        Expect(output, "$generic$witness$Calls.Dog$Calls.Named$0$name");
        Expect(output, "Return value: (String) $0 = \"collie\"");
    }

    [PosixFact(NeedsLldb)]
    public async Task ClosuresShowTheirFunctionAndCaptures() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        var output = await Run(lldb, Calls, "calls.sb", ["b calls.sb:20", "run", "frame variable", "up", "frame variable add.count add.label", "kill"]);

        // Inside, what it captured reads as locals do — by reference or by value.
        Expect(output, "Calls.main$closure0(a=3) at calls.sb:20");
        Expect(output, "(Int) a = 3\n(Int) count = 2");

        // Outside, a closure is its function, and its captures are its children.
        Expect(output, "(Int) add.count = 2");
    }

    [PosixFact(NeedsLldb)]
    public async Task FunctionsAreFoundByTheirSableNames() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        // Taken whole, as C takes a name: an lldb that reads it as Swift's
        // or C++'s would look up only 'triple' (the plugin says C).
        var output = await Run(lldb, Steps, "steps.sb", ["b Steps.triple", "run", "bt", "kill"]);
        Expect(output, "Steps.triple(n=1) at steps.sb:4");
        Expect(output, "Steps.main at steps.sb:13");
    }

    /// <summary>
    ///     A witness (one entry of a witness table, #299) is a trampoline to the method, in a file of its
    ///     own; and, for an LLVM that would write its whole body as one
    ///     line-0 row, its prologue has a line — what lldb 18 needs to step
    ///     on through it rather than out of it.
    /// </summary>
    [Fact]
    public void AWitnessThunkIsSteppedThroughWhateverTheLlvm() {
        var directory = Directory.CreateTempSubdirectory("sable-thunk-");
        try {
            var result = Pipeline.Compile(new SourceText(Calls, "calls.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var ir = result.LlvmIr!;
            var subprogram = ir.Split('\n').Single(static l => l.Contains("!DISubprogram(name: \"$generic$witness$Calls.Dog$Calls.Named$0$name\"", StringComparison.Ordinal));
            Assert.Contains("targetFuncName: \"Calls.Dog.name\"", subprogram, StringComparison.Ordinal);
            Assert.Contains("DIFlagArtificial", subprogram, StringComparison.Ordinal);
            var file = System.Text.RegularExpressions.Regex.Match(subprogram, @"file: (![0-9]+)").Groups[1].Value;
            Assert.Contains($"{file} = !DIFile(filename: \"<compiler-generated>\"", ir, StringComparison.Ordinal);
            var scopeLine = LlvmEmitter.ThunkPrologueHasALine(NativeBuild.CompilerVersion) ? 1 : 0;
            Assert.Contains($"line: 0, type:", subprogram, StringComparison.Ordinal);
            Assert.Contains($"scopeLine: {scopeLine},", subprogram, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("Ubuntu clang version 18.1.3 (1ubuntu1)\nTarget: x86_64-pc-linux-gnu", 18, false, true)]
    [InlineData("clang version 19.1.7\nTarget: aarch64-unknown-linux-gnu", 19, false, true)]
    [InlineData("clang version 20.1.0 (Fedora 20.1.0-1.fc42)", 20, false, false)]
    [InlineData("Apple clang version 16.0.0 (clang-1600.0.26.6)\nTarget: arm64-apple-darwin24.6.0", 16, true, false)]
    public void TheCompilersVersionDecidesTheThunksPrologue(string versionText, int major, bool apple, bool prologueHasALine) {
        var version = NativeBuild.ParseVersion(versionText);
        Assert.Equal((major, apple), version);
        Assert.Equal(prologueHasALine, LlvmEmitter.ThunkPrologueHasALine(version));
    }

    [Fact]
    public void NoCompilerNoThunkPrologueLine() {
        Assert.Null(NativeBuild.ParseVersion("gcc (GCC) 14.2.0"));
        Assert.False(LlvmEmitter.ThunkPrologueHasALine(null));
    }

    [PosixFact(NeedsLldb)]
    public async Task TypesThatContainThemselvesAreDescribed() {
        const string source = """
                              module Rec

                              struct Tree {
                                  var label: String
                                  var children: Tree[>..]
                                  var score: (Tree) -> Int
                              }

                              class Node {
                                  var value: Int
                                  var next: Node?
                                  init(_ value: Int) {
                                      self.value = value
                                      self.next = none
                                  }
                              }

                              fn size(_ t: Tree) -> Int {
                                  return t.children.count
                              }

                              @main
                              fn main() {
                                  let leaf = Tree { label: "leaf", children: [], score: size }
                                  let root = Tree { label: "root", children: [leaf], score: size }
                                  let n = Node(1)
                                  n.next = Node(2)
                                  print(root.score(root))
                              }
                              """;
        if (Lldb.Value is not { } lldb) {
            // Still: the description must be written without recursing forever.
            Assert.True(Pipeline.Compile(new SourceText(source, "rec.sb"), Path.GetTempPath()).Succeeded);
            return;
        }

        var output = await Run(lldb, source, "rec.sb", ["b rec.sb:28", "run", "frame variable root n", "kill"]);
        Expect(output, "children = count = 1 {\n    [0] = {\n      label = \"leaf\"");
        Expect(output, "next = Rec.Node {\n    value = 2\n    next = none");
    }

    const string Scopes = """
                          module Scopes

                          let limit = 3
                          var counter = 10

                          @main
                          fn main() {
                              let x = 1
                              var total = 0
                              for i in 0..<2 {
                                  let x = i + 100
                                  total = total + x
                                  print(x) // inner
                              }
                              counter = counter + limit
                              print(x + total + counter) // after
                          }
                          """;

    /// <summary>
    ///     DWARF 5, a block's locals in a lexical block of their own, and the
    ///     program's globals — in the IR, whatever debugger there is.
    /// </summary>
    [Fact]
    public void BlocksAndGlobalsAreDescribed() {
        var directory = Directory.CreateTempSubdirectory("sable-scopes-");
        try {
            var result = Pipeline.Compile(new SourceText(Scopes, "scopes.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var ir = result.LlvmIr!;
            // Windows' linker reads CodeView, not DWARF; the nodes below are the same either way.
            Assert.Contains(
                NativeTarget.Host.Platform == NativePlatform.Windows ? "!\"CodeView\", i32 1}" : "!\"Dwarf Version\", i32 5}",
                ir,
                StringComparison.Ordinal);

            // The inner 'x' is in the loop body's block, inside the loop's own.
            var innerX = System.Text.RegularExpressions.Regex.Match(ir, @"!DILocalVariable\(name: ""x"", scope: (![0-9]+), file: ![0-9]+, line: 11,");
            Assert.True(innerX.Success, ir);
            Assert.Matches($@"{innerX.Groups[1].Value} = distinct !DILexicalBlock\(scope: ![0-9]+, file: ![0-9]+, line: 10,", ir);
            Assert.Matches(@"!DILocalVariable\(name: ""x"", scope: ![0-9]+, file: ![0-9]+, line: 8,", ir);

            Assert.Matches(@"@""sable\.global\.Scopes\.limit"" = internal global i64 zeroinitializer, !dbg ![0-9]+", ir);
            Assert.Contains("!DIGlobalVariable(name: \"counter\"", ir, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    [PosixFact(NeedsLldb)]
    public async Task AShadowedNameShowsOnlyWhereItIsInScope() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        var output = await Run(lldb, Scopes, "scopes.sb", [
            $"b scopes.sb:{LineOf(Scopes, "// inner")}", $"b scopes.sb:{LineOf(Scopes, "// after")}",
            "run", "frame variable x", "continue", "continue", "frame variable", "target variable", "kill",
        ]);

        // In the loop, the name is the loop's 'x'.
        Expect(output, "(lldb) frame variable x\n(Int) x = 100\n");

        // After it, only the outer one is, and no loop variable.
        Expect(output, "(lldb) frame variable\n(Int) x = 1\n(Int) total = 201\n(lldb)");
        Expect(output, "(Int) counter = 13");
        Expect(output, "(Int) limit = 3");
    }

    /// <summary>
    ///     Built for Windows, the same nodes are written as CodeView — what
    ///     the linker gathers into the PDB Windows' debuggers read — and
    ///     not as DWARF. The object is made here when clang is; nothing here
    ///     can debug it.
    /// </summary>
    [Fact]
    public void AWindowsTargetIsDescribedInCodeView() {
        var directory = Directory.CreateTempSubdirectory("sable-codeview-");
        try {
            var windows = NativeTarget.Of("x86_64-pc-windows-msvc");
            var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Scopes, "scopes.sb"))], directory.FullName, target: windows);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var ir = result.LlvmIr!;
            Assert.Contains("!{i32 2, !\"CodeView\", i32 1}", ir, StringComparison.Ordinal);
            Assert.DoesNotContain("Dwarf Version", ir, StringComparison.Ordinal);
            Assert.Contains("!DISubprogram(name: \"Scopes.main\"", ir, StringComparison.Ordinal);
            Assert.Contains("distinct !DILexicalBlock(", ir, StringComparison.Ordinal);
            Assert.Contains("!DIGlobalVariable(name: \"counter\"", ir, StringComparison.Ordinal);
            // CodeView has no place for a file's text.
            Assert.DoesNotContain("source: ", ir, StringComparison.Ordinal);

            if (!Programs.CanBuild) {
                return;
            }

            var objectPath = Path.Combine(directory.FullName, "scopes.obj");
            // -g at -O0: optimised, the object would be bitcode for link-time
            // optimisation, as it is when the host is Windows.
            var compiled = NativeObjects.CompileIr(ir, objectPath, NativeOptions.Of(optimise: false, debug: true) with { Target = windows });
            Assert.True(compiled.Succeeded, compiled.Output);
            var bytes = File.ReadAllBytes(objectPath);
            Assert.True(bytes.AsSpan().IndexOf(".debug$S"u8) >= 0, "the object has no CodeView symbols section");
            Assert.True(bytes.AsSpan().IndexOf(".debug_info"u8) < 0, "the object has DWARF");
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>An optimised build's compile unit says it is, so that a debugger is warier of what it reads.</summary>
    [Fact]
    public void AnOptimisedBuildSaysSo() {
        var directory = Directory.CreateTempSubdirectory("sable-optimised-");
        try {
            var optimised = Pipeline.Compile(new SourceText(Scopes, "scopes.sb"), directory.FullName, optimised: true);
            Assert.True(optimised.Succeeded, string.Join("\n", optimised.Errors));
            Assert.Contains("isOptimized: true", optimised.LlvmIr!, StringComparison.Ordinal);

            var plain = Pipeline.Compile(new SourceText(Scopes, "scopes.sb"), directory.FullName);
            Assert.Contains("isOptimized: false", plain.LlvmIr!, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    const string Library = """
                           module Gen
                           import Sable.Collections

                           @inline_layout
                           struct Spot {
                               var x: I32
                               var y: I32
                           }

                           @inline_layout
                           struct Order {
                               var id: U64
                               var at: Spot
                               var paid: Bool
                               var codes: U16[..8]
                           }

                           fn describe<T>(_ value: T) -> Int {
                               return 1
                           }

                           @main
                           fn main() {
                               var stack = Stack<Int>()
                               let order = Order { id: 7, at: Spot { x: 3, y: -4 }, paid: true, codes: [1, 2] }
                               stack.push(describe(5) + describe("five")) // push
                               print(stack.count + Int(order.id))
                           }
                           """;

    /// <summary>
    ///     The standard library's files are in the compiler, not on disk: their
    ///     text goes in the line table, so stepping into the library shows its
    ///     lines. The program's own files are read where they are. Built
    ///     for Linux, as DWARF, the only format with a place for the text:
    ///     a Windows host's own build is CodeView.
    /// </summary>
    [Fact]
    public void TheLibrarysTextIsInTheDebugInformation() {
        var directory = Directory.CreateTempSubdirectory("sable-sources-");
        try {
            var linux = NativeTarget.Of("x86_64-unknown-linux-gnu");
            var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Library, "gen.sb"))], directory.FullName, target: linux);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var ir = result.LlvmIr!;
            Assert.Matches(@"!DIFile\(filename: ""lib/Sable\.Collections/Stack\.sb"", directory: ""[^""]*"", source: ""[^""]*public fn push", ir);
            Assert.Matches(@"!DIFile\(filename: ""gen\.sb"", directory: ""[^""]*""\)", ir);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>A generic function's instance is named as Sable writes it, its type arguments in brackets.</summary>
    [Fact]
    public void GenericInstancesAreNamedAsSableWritesThem() {
        var directory = Directory.CreateTempSubdirectory("sable-generic-names-");
        // The names of specialised instances: an unspecialised function is one, named for its type parameters.
        using var specialised = SilGen.UseGenerics(GenericsMode.Specialised);
        try {
            var result = Pipeline.Compile(new SourceText(Library, "gen.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var ir = result.LlvmIr!;
            Assert.Contains("!DISubprogram(name: \"Gen.describe<Int>\"", ir, StringComparison.Ordinal);
            Assert.Contains("!DISubprogram(name: \"Gen.describe<String>\"", ir, StringComparison.Ordinal);
            Assert.Contains("!DISubprogram(name: \"Sable.Collections.Stack<Int>.push\"", ir, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     Stepping into the library lands on its source, a generic instance is
    ///     found by its Sable name, and an <c>@inline_layout</c> value shows its
    ///     fields rather than the address of its bytes.
    /// </summary>
    [PosixFact(NeedsLldb)]
    public async Task TheLibraryGenericsAndInlineLayoutsReadAsWritten() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        // Specialised frames: lldb reading unspecialised ones from their metadata is stage 9 of #297.
        using var specialised = SilGen.UseGenerics(GenericsMode.Specialised);
        var output = await Run(lldb, Library, "gen.sb", [
            "b 'Gen.describe<String>'", $"b gen.sb:{LineOf(Library, "// push")}",
            "run", "frame variable order", "continue", "bt 1", "finish", "step", "kill",
        ]);

        Expect(output, "(Gen.Order) order = {\n  id = 7\n  at = (x = 3, y = -4)\n  paid = true\n}");
        Expect(output, "Gen.describe<String>(value=");
        Expect(output, "Sable.Collections.Stack<Int>.push(");
        Expect(output, "items.append(item)");
    }

    [Fact]
    public void TheDebugCommandIsThere() {
        var command = Command.Find("debug");
        Assert.NotNull(command);
        Assert.Contains("sable debug <file.sb>", command.Usage, StringComparison.Ordinal);
    }

    /// <summary>What an editor's debugger loads: the plugin <c>sable debug</c> itself would.</summary>
    [Fact]
    public void TheDebugCommandWritesThePluginForAnEditor() {
        var directory = Directory.CreateTempSubdirectory("sable-plugin-");
        try {
            var path = Path.Combine(directory.FullName, "nested", "sable_lldb.py");
            Assert.Equal(0, Command.Find("debug")!.Run(["--write-plugin", path]));
            Assert.Equal(NativeBuild.LldbPluginSource, File.ReadAllText(path));
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The DWARF itself is well formed, as the platform's verifier sees it.</summary>
    [PosixFact("the debug information is DWARF, for lldb and gdb; a Windows build is CodeView, checked by AWindowsTargetIsDescribedInCodeView")]
    public async Task DwarfVerifies() {
        // Apple's dwarfdump, or LLVM's, perhaps under its version's name.
        string[] candidates = OperatingSystem.IsMacOS()
            ? ["dwarfdump"]
            : ["llvm-dwarfdump", ..Enumerable.Range(14, 10).Reverse().Select(static v => $"llvm-dwarfdump-{v}")];
        if (!Programs.CanBuild || candidates.FirstOrDefault(Exists) is not { } verifier) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-dwarf-");
        try {
            var result = Pipeline.Compile(new SourceText(Values, "values.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var executable = Path.Combine(directory.FullName, "values");
            var build = NativeBuild.Build(result.LlvmIr!, executable, debug: true);
            Assert.True(build.Succeeded, build.Output);

            var target = OperatingSystem.IsMacOS() ? executable + ".dSYM" : executable;
            var start = new ProcessStartInfo(verifier) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--verify");
            start.ArgumentList.Add(target);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var text = await output;

            // A function --gc-sections dropped keeps its debug entry, its
            // range resolved by the linker to address 0 (GNU ld writes 0
            // where lld writes a tombstone), so dead ranges overlap at 0.
            // That is the linker's, not the DWARF's: every other error fails.
            var real = ErrorBlock().Split(text).Where(static b => b.TrimStart().StartsWith("error:", StringComparison.Ordinal) && !IsDeadAtZero(b)).ToList();
            Assert.True(real.Count == 0, text + errors);
            if (process.ExitCode != 0) {
                Assert.True(OperatingSystem.IsLinux(), text + errors);
            } else {
                Assert.Contains("No errors.", text, StringComparison.Ordinal);
            }
        } finally {
            directory.Delete(true);
        }

        static bool Exists(string tool) {
            try {
                using var process = Process.Start(new ProcessStartInfo(tool, "--version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
                process.WaitForExit();
                return process.ExitCode == 0;
            } catch (System.ComponentModel.Win32Exception) {
                return false;
            }
        }
    }

    /// <summary>
    ///     Whether a verifier error is one about overlapping ranges every one
    ///     of which starts at address 0: functions the linker dropped. The
    ///     error's lines name the ranges ("[0x0, 0x8)") or the entries'
    ///     low_pc ("address = 0x0000000000000000").
    /// </summary>
    static bool IsDeadAtZero(string block) {
        if (!block.Contains("overlapping", StringComparison.Ordinal)) {
            return false;
        }

        // The ranges the error names on its own line, if it names any (the
        // entry it prints after them may list all its unit's ranges); else
        // the low_pc of the entries it prints.
        var first = block.TrimStart().Split('\n')[0];
        var named = RangeStart().Matches(first).Select(static m => m.Groups["start"].Value).ToList();
        var starts = named.Count > 0 ? named : LowPc().Matches(block).Select(static m => m.Groups["start"].Value).ToList();
        return starts.Count > 0 && starts.All(static s => s.Trim('0').Length == 0);
    }

    /// <summary>Where each of the verifier's errors starts.</summary>
    [GeneratedRegex(@"(?=^\s*error:)", RegexOptions.Multiline)]
    private static partial Regex ErrorBlock();

    /// <summary>A range's start: "[0x0000000000000000, ".</summary>
    [GeneratedRegex(@"\[0x(?<start>[0-9a-f]+),")]
    private static partial Regex RangeStart();

    /// <summary>An entry's low_pc, as the verifier prints it.</summary>
    [GeneratedRegex(@"DW_AT_low_pc[^\n]*address = 0x(?<start>[0-9a-f]+)")]
    private static partial Regex LowPc();
}
