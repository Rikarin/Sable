using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     WebAssembly under WASI (runtime/platform_wasi.c): a program built for
///     <c>wasm32-wasi</c>, with wasi-libc's sysroot, and run by wasmtime —
///     one thread, so the executor runs every task on it and the blocking
///     pool's work runs where it is asked for; a trap ends the module as an
///     abort ends a process.
/// </summary>
/// <remarks>
///     Only where wasmtime is on the path and a WASI sysroot is found
///     (<see cref="NativeTarget.FindSysroot" />), as in CI's WASI job, which
///     sets <c>SABLE_WASI_TESTS=required</c> so that they cannot pass
///     vacuously there; elsewhere they do.
/// </remarks>
public sealed class WasiTests {
    static readonly NativeTarget Wasi = NativeTarget.Of("wasm32-wasi");

    static bool CanRun {
        get {
            var can = NativeBuild.IsAvailable() && Wasi.FindSysroot() is not null && Wasmtime() is not null;
            // CI's WASI job sets it: there, not running is a failure.
            Assert.True(can || Environment.GetEnvironmentVariable("SABLE_WASI_TESTS") != "required", "SABLE_WASI_TESTS=required, but there is no clang, WASI sysroot or wasmtime");
            return can;
        }
    }

    [Fact]
    public void AProgramRunsUnderWasmtime() {
        if (!CanRun) {
            return;
        }

        var (stdout, stderr, exitCode) = BuildAndRun("""
            module Main

            class Box {
                var value: Int
                init(_ value: Int) { self.value = value }
            }

            fn main() {
                var numbers: Int[>..] = [3, 1, 4]
                numbers.append(1)
                let box = Box(numbers[0] + numbers.count)
                let words = ["pear", "plum"]
                print("\(box.value) \(words[1] == "plum") \(Platform.os) \(Platform.arch) \(Platform.pointerWidth)")
            }
            """);
        Assert.True(exitCode == 0, stderr);
        Assert.Equal("7 true wasi wasm32 32\n", stdout);
    }

    /// <summary>Tasks, a sleep and blocking work, all on the one thread, in the order a single executor runs them.</summary>
    [Fact]
    public void TasksRunOnTheOneThread() {
        if (!CanRun) {
            return;
        }

        var (stdout, stderr, exitCode) = BuildAndRun("""
            module Main

            @main
            fn main() async {
                let slow = Task.spawn { () -> Int in
                    try? await Task.sleep(nanoseconds: 20_000_000)
                    return 2
                }
                let fast = Task.spawn { () -> Int in 1 }
                let blocked = await Task.blocking { () -> Int in 40 }
                print(await fast.value + await slow.value + blocked)
            }
            """);
        Assert.True(exitCode == 0, stderr);
        Assert.Equal("43\n", stdout);
    }

    [Fact]
    public void ATrapEndsTheModuleAsAnAbortDoes() {
        if (!CanRun) {
            return;
        }

        var (stdout, stderr, exitCode) = BuildAndRun("""
            module Main

            fn main() {
                let numbers = [1, 2, 3]
                print("before")
                print(numbers[numbers.count])
            }
            """);
        Assert.Equal("before\n", stdout);
        Assert.Contains("fatal error: array index out of range", stderr, StringComparison.Ordinal);
        Assert.Equal(134, exitCode);
    }

    static (string Stdout, string Stderr, int ExitCode) BuildAndRun(string source) {
        var directory = Directory.CreateTempSubdirectory("sable-wasi-");
        try {
            var tree = SyntaxTree.Parse(new SourceText(source, "main.sb"), Pipeline.ParseOptionsFor(true, Wasi));
            var result = Pipeline.Compile([tree], target: Wasi);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));

            var module = Path.Combine(directory.FullName, "program.wasm");
            var build = NativeBuild.Build(result.LlvmIr!, module, NativeOptions.Of(true, false) with { Target = Wasi });
            Assert.True(build.Succeeded, build.Output);

            var start = new ProcessStartInfo(Wasmtime()!, ["run", module]) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (stdout.Result, stderr, process.ExitCode);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>wasmtime, as <c>$WASMTIME</c> names it or found on the path; null when there is none.</summary>
    static string? Wasmtime() {
        if (Environment.GetEnvironmentVariable("WASMTIME") is { Length: > 0 } named) {
            return named;
        }

        var name = OperatingSystem.IsWindows() ? "wasmtime.exe" : "wasmtime";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
}
