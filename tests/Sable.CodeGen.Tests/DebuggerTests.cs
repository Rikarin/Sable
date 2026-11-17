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

    [Fact]
    public async Task LldbShowsFramesLocalsAndTypes() {
        if (!Programs.CanBuild || !OperatingSystem.IsMacOS() || !HasLldb()) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-debug-");
        try {
            var sourcePath = Path.Combine(directory.FullName, "debug.sab");
            File.WriteAllText(sourcePath, Source);

            var result = Pipeline.Compile(new SourceText(Source, "debug.sab"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));

            var executable = Path.Combine(directory.FullName, "debug");
            var build = NativeBuild.Build(result.LlvmIr!, executable, debug: true);
            Assert.True(build.Succeeded, build.Output);

            var start = new ProcessStartInfo("lldb") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] {
                         "--batch",
                         "-o", "breakpoint set --file debug.sab --line 13",
                         "-o", "run",
                         "-o", "bt",
                         "-o", "frame variable",
                         "-o", "up",
                         "-o", "frame variable",
                         "-o", "kill",
                         "--", executable,
                     }) {
                start.ArgumentList.Add(argument);
            }

            using var lldb = Process.Start(start)!;
            var output = lldb.StandardOutput.ReadToEndAsync();
            _ = await lldb.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await lldb.WaitForExitAsync(timeout.Token);
            var text = await output;

            // The frames: where it stopped, with its arguments, and its caller.
            Assert.Contains("gcd(a=1071, b=462) at debug.sab:13", text, StringComparison.Ordinal);
            Assert.Contains("main at debug.sab:24", text, StringComparison.Ordinal);

            // The locals, with Sable's names for their types.
            Assert.Contains("(Int) t = 147", text, StringComparison.Ordinal);
            Assert.Contains("(Int) x = 1071", text, StringComparison.Ordinal);
            Assert.Contains("(Debug.Point) p = (x = 1071, y = 462)", text, StringComparison.Ordinal);
            Assert.Contains("(F64) ratio = 2.5", text, StringComparison.Ordinal);
            Assert.Contains("(Bool) flag = true", text, StringComparison.Ordinal);

            // Not mistaken for Swift.
            Assert.DoesNotContain("Swift compiler", text, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }
}
