using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     docs/04 §2.5: "lldb shows correct frames, locals and types". Built with
///     <c>-g</c> and driven through the real lldb: a breakpoint by file and
///     line, then the backtrace and the variables in two frames.
/// </summary>
/// <remarks>Needs clang and lldb; without them it passes vacuously.</remarks>
public sealed class DebuggerTests {
    const string Source = """
                          module Debug

                          struct Point {
                              var x: Int
                              var y: Int
                          }

                          fn gcd(_ a: Int, _ b: Int) -> Int {
                              var x = a
                              var y = b
                              while y != 0 {
                                  let t = x % y
                                  x = y
                                  y = t
                              }
                              return x
                          }

                          @main
                          fn main() {
                              let p = Point(x: 1071, y: 462)
                              let ratio: F64 = 2.5
                              let flag = true
                              print(gcd(p.x, p.y))
                          }
                          """;

    static bool HasLldb() {
        try {
            using var process = Process.Start(new ProcessStartInfo("lldb", "--version") { RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        } catch (System.ComponentModel.Win32Exception) {
            return false;
        }
    }

    /// <summary>The program built with -g, run under lldb with the commands given: what lldb printed. Null without clang, lldb or macOS.</summary>
    static async Task<string?> UnderLldb(string source, string name, params string[] commands) {
        if (!Programs.CanBuild || !OperatingSystem.IsMacOS() || !HasLldb()) {
            return null;
        }

        var directory = Directory.CreateTempSubdirectory("sable-debug-");
        try {
            var sourcePath = Path.Combine(directory.FullName, $"{name}.sb");
            File.WriteAllText(sourcePath, source);

            var result = Pipeline.Compile(new SourceText(source, $"{name}.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));

            var executable = Path.Combine(directory.FullName, name);
            var build = NativeBuild.Build(result.LlvmIr!, executable, debug: true);
            Assert.True(build.Succeeded, build.Output);

            var start = new ProcessStartInfo("lldb") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--batch");
            foreach (var command in commands) {
                start.ArgumentList.Add("-o");
                start.ArgumentList.Add(command);
            }

            start.ArgumentList.Add("--");
            start.ArgumentList.Add(executable);
            start.Environment["SABLE_WORKERS"] = "0";

            using var lldb = Process.Start(start)!;
            var output = lldb.StandardOutput.ReadToEndAsync();
            _ = await lldb.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await lldb.WaitForExitAsync(timeout.Token);
            return await output;
        } finally {
            directory.Delete(true);
        }
    }

    [PosixFact("it drives lldb with DWARF; a Windows build is described in CodeView, for the PDB its debuggers read, which no test here drives")]
    public async Task LldbShowsFramesLocalsAndTypes() {
        if (await UnderLldb(Source, "debug", "breakpoint set --file debug.sb --line 13", "run", "bt", "frame variable", "up", "frame variable", "kill") is not { } text) {
            return;
        }

        // The frames: where it stopped, with its arguments, and its caller.
        Assert.Contains("gcd(a=1071, b=462) at debug.sb:13", text, StringComparison.Ordinal);
        Assert.Contains("main at debug.sb:24", text, StringComparison.Ordinal);

        // The locals, with Sable's names for their types.
        Assert.Contains("(Int) t = 147", text, StringComparison.Ordinal);
        Assert.Contains("(Int) x = 1071", text, StringComparison.Ordinal);
        Assert.Contains("(Debug.Point) p = (x = 1071, y = 462)", text, StringComparison.Ordinal);
        Assert.Contains("(F64) ratio = 2.5", text, StringComparison.Ordinal);
        Assert.Contains("(Bool) flag = true", text, StringComparison.Ordinal);

        // Not mistaken for Swift.
        Assert.DoesNotContain("Swift compiler", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An async function that has been suspended and resumed keeps its
    ///     parameters and locals in its frame, on its task's slab: lldb shows
    ///     them there, by their names, after the await as before it.
    /// </summary>
    [PosixFact("it drives lldb with DWARF; a Windows build is described in CodeView, for the PDB its debuggers read, which no test here drives")]
    public async Task LldbShowsTheLocalsOfAnAsyncFunction() {
        const string source = """
                              module AsyncDebug

                              fn slow(_ n: Int) async -> Int {
                                  let base = n * 2
                                  await Task.yield()
                                  try? await Task.sleep(nanoseconds: 1_000_000)
                                  let total = base + 1
                                  return total
                              }

                              @main
                              fn main() async {
                                  let task = Task.spawn { await slow(20) }
                                  print(await task.value)
                              }
                              """;
        if (await UnderLldb(source, "asyncdebug", "breakpoint set --file asyncdebug.sb --line 8", "run", "frame variable", "kill") is not { } text) {
            return;
        }

        Assert.Contains("(Int) n = 20", text, StringComparison.Ordinal);
        Assert.Contains("(Int) base = 40", text, StringComparison.Ordinal);
        Assert.Contains("(Int) total = 41", text, StringComparison.Ordinal);
    }
}
