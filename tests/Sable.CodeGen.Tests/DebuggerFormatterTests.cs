using System.Diagnostics;
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
public sealed class DebuggerFormatterTests {
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
    const string NeedsLldb = "lldb's Python formatter plugin and these sessions are for lldb on macOS and Linux; a Windows debugger reads a PDB's CodeView, which the compiler does not write yet";

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
    static async Task<string> Run(string lldb, string source, string fileName, IReadOnlyList<string> commands) {
        var directory = Directory.CreateTempSubdirectory("sable-formatters-");
        try {
            var sourcePath = Path.Combine(directory.FullName, fileName);
            File.WriteAllText(sourcePath, source);

            var result = Pipeline.Compile(new SourceText(source, fileName), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));

            var executable = Path.Combine(directory.FullName, Path.GetFileNameWithoutExtension(fileName));
            var build = NativeBuild.Build(result.LlvmIr!, executable, debug: true);
            Assert.True(build.Succeeded, build.Output);

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

    [PosixFact(NeedsLldb)]
    public async Task StepGoesThroughAnExistentialIntoTheMethod() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        var output = await Run(lldb, Calls, "calls.sb", ["b calls.sb:23", "run", "step", "bt", "finish", "kill"]);

        // The compiler's thunk between them is in the backtrace, but is not stopped in.
        Expect(output, "Calls.Dog.name(self=(breed = \"collie\")) at calls.sb:10");
        Expect(output, "$witness$Calls.Dog$name");
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
    ///     A witness thunk is a trampoline to the method, in a file of its
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
            var subprogram = ir.Split('\n').Single(static l => l.Contains("!DISubprogram(name: \"$witness$Calls.Dog$name\"", StringComparison.Ordinal));
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
    [PosixFact("the debug information is DWARF, for lldb and gdb; a Windows debugger reads CodeView from a PDB, which the compiler does not write yet")]
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
            Assert.True(process.ExitCode == 0, text + errors);
            Assert.Contains("No errors.", text, StringComparison.Ordinal);
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
}
