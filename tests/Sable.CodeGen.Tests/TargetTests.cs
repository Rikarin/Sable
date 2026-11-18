using System.Diagnostics;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Building for another machine (docs/04 §2.3, docs/05 §5.1): target
///     triples, the IR's data layout and triple, objects for each target
///     clang knows, and <c>--target</c> through a package build.
/// </summary>
/// <remarks>
///     Only macOS and Linux have the runtime, and this machine may have no
///     sysroot for another, so most targets are checked as object files —
///     which need neither — by their headers. On an Apple Silicon Mac an
///     x86-64 macOS program is also linked and, under Rosetta, run.
/// </remarks>
public sealed class TargetTests {
    static readonly bool CanBuild = NativeBuild.IsAvailable();

    const string Program = """
        module Main

        class Box {
            var value: Int
            init(_ value: Int) { self.value = value }
        }

        fn main() {
            var numbers: Int[>..] = [3, 1, 4]
            numbers.append(1)
            numbers[0] = numbers[0] + numbers.count
            let last = numbers.removeLast()
            let words = ["pear", "plum"]
            let box = Box(numbers[0])
            print("\(numbers[0]) \(numbers[1]) \(numbers[2]) \(last) \(words[1] == "plum") \(box.value) \(Platform.os) \(Platform.arch) \(Platform.pointerWidth)")
        }
        """;

    // ---- Triples -----------------------------------------------------------

    [Theory]
    [InlineData("aarch64-unknown-linux-gnu", "arm64", "linux", 64, "")]
    [InlineData("arm64-apple-darwin", "arm64", "macos", 64, "")]
    [InlineData("x86_64-apple-macosx14.0", "x86_64", "macos", 64, "")]
    [InlineData("x86_64-unknown-linux-gnu", "x86_64", "linux", 64, "")]
    [InlineData("x86_64-pc-windows-msvc", "x86_64", "windows", 64, ".exe")]
    [InlineData("wasm32-wasi", "wasm32", "wasi", 32, ".wasm")]
    [InlineData("wasm32-unknown-wasip1", "wasm32", "wasi", 32, ".wasm")]
    public void ATripleNamesItsArchitectureAndSystem(string triple, string arch, string os, int pointerBits, string suffix) {
        var target = NativeTarget.Parse(triple, out var error);
        Assert.True(target is not null, error);
        Assert.Equal(arch, target.Arch);
        Assert.Equal(os, target.Os);
        Assert.Equal(pointerBits, target.PointerBits);
        Assert.Equal(suffix, target.ExecutableSuffix);
        Assert.Equal(target.Platform is not NativePlatform.Wasi, target.HasRuntime);
    }

    [Theory]
    [InlineData("linux", "is not a target triple")]
    [InlineData("sparc-sun-solaris", "architecture")]
    [InlineData("x86_64-unknown-freebsd", "operating system")]
    [InlineData("wasm32-unknown-linux", "WebAssembly")]
    public void ATripleTheCompilerCannotBuildForIsRefused(string triple, string why) {
        Assert.Null(NativeTarget.Parse(triple, out var error));
        Assert.Contains(why, error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostIsBuiltWithoutATargetFlag() {
        Assert.True(NativeTarget.Host.IsHost);
        Assert.Empty(NativeTarget.Host.ClangFlags);
        Assert.Equal(ParseOptions.HostOs, NativeTarget.Host.Os);
        Assert.Equal(ParseOptions.HostArch, NativeTarget.Host.Arch);
        Assert.Equal(["--target=x86_64-unknown-linux-gnu"], NativeTarget.Of("x86_64-unknown-linux-gnu").ClangFlags);
    }

    // ---- The IR ------------------------------------------------------------

    /// <summary>
    ///     The IR says what it is built for — the data layout and triple clang
    ///     compiles the runtime with — and reads the runtime's headers through
    ///     their types, which that layout lays out, never at byte offsets that
    ///     hold for 64-bit pointers alone.
    /// </summary>
    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", "p270:32:32")]
    [InlineData("wasm32-wasi", "p:32:32")]
    public void TheIrCarriesTheTargetsLayoutAndTriple(string triple, string layout) {
        if (!CanBuild) {
            return;
        }

        var target = NativeTarget.Of(triple);
        var ir = NativeBuild.ForTarget(Compile(target).LlvmIr!, target);
        Assert.StartsWith("target datalayout = \"", ir, StringComparison.Ordinal);
        Assert.Contains(layout, ir.Split('\n')[0], StringComparison.Ordinal);
        Assert.Matches($"\ntarget triple = \"{triple.Split('-')[0]}-[^\"]*\"\n", ir);

        Assert.Contains("getelementptr inbounds %SableArrayHeader", ir, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"getelementptr inbounds i8, ptr %[\w.]+, i64 (8|12|16|24|32)\b", ir);
    }

    // ---- Objects for every target ------------------------------------------

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("aarch64-unknown-linux-gnu")]
    [InlineData("x86_64-apple-darwin")]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("wasm32-wasi")]
    public void AProgramCompilesToAnObjectForEachTarget(string triple) {
        if (!CanBuild) {
            return;
        }

        var target = NativeTarget.Of(triple);
        var directory = Directory.CreateTempSubdirectory("sable-target-");
        try {
            var path = Path.Combine(directory.FullName, "program.o");
            var compiled = NativeObjects.CompileIr(Compile(target).LlvmIr!, path, NativeOptions.Of(true, false) with { Target = target, Lto = LtoMode.Off });

            // Which targets clang can write code for is how it was built:
            // Apple's has no WebAssembly, LLVM's own has them all.
            if (compiled.Output.Contains("No available targets are compatible", StringComparison.Ordinal)) {
                return;
            }

            Assert.True(compiled.Succeeded, compiled.Output);
            Assert.Equal(Machine(target), MachineOf(File.ReadAllBytes(path)));
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The runtime has no WebAssembly layer: an executable for it is refused with why, not left to clang's errors.</summary>
    [Theory]
    [InlineData("wasm32-wasi")]
    public void AnExecutableNeedsTheRuntimesPlatformLayer(string triple) {
        var target = NativeTarget.Of(triple);
        var build = NativeBuild.Build("", Path.Combine(Path.GetTempPath(), "never"), NativeOptions.Of(true, false) with { Target = target });
        Assert.False(build.Succeeded);
        Assert.Contains($"no platform layer for {triple}", build.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     On an Apple Silicon Mac, a whole x86-64 macOS program: compiled,
    ///     linked with the runtime built for it, and run under Rosetta, where
    ///     it says it is x86-64.
    /// </summary>
    [Fact]
    public void AnIntelMacProgramIsBuiltAndRunsOnAnAppleSiliconMac() {
        if (!CanBuild || NativeTarget.Host.Platform != NativePlatform.MacOS || NativeTarget.Host.Arch != "arm64") {
            return;
        }

        var target = NativeTarget.Of("x86_64-apple-darwin");
        var executable = Path.Combine(Path.GetTempPath(), $"sable-x64-{Guid.NewGuid():N}");
        try {
            var build = NativeBuild.Build(Compile(target).LlvmIr!, executable, NativeOptions.Of(true, false) with { Target = target });
            Assert.True(build.Succeeded, build.Output);
            Assert.Equal(Machine(target), MachineOf(File.ReadAllBytes(executable)));
            if (!Rosetta()) {
                return;
            }

            var (output, exitCode) = PackageFixture.Execute(executable);
            Assert.Equal(0, exitCode);
            Assert.Equal("7 1 4 1 true 7 macos x86_64 64\n", output);
        } finally {
            File.Delete(executable);
        }
    }

    // ---- Through a package build -------------------------------------------

    /// <summary>
    ///     <c>sable build --target</c>: the output goes to
    ///     <c>target/&lt;triple&gt;/&lt;profile&gt;/</c>, the program's files and the
    ///     standard library are parsed for the target, and a dependency whose
    ///     <c>when:</c> names another system is left out.
    /// </summary>
    [Fact]
    public void APackageIsBuiltForTheTargetItIsAskedFor() {
        if (!CanBuild || NativeTarget.Host.Platform != NativePlatform.MacOS || NativeTarget.Host.Arch != "arm64") {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("linuxonly", "linuxonly", "0.1.0", "", ("src/lib.sb", "module LinuxOnly\n\npublic fn penguin() -> String {\n    return \"penguin\"\n}\n"));
        fixture.Package("app", "app", "0.1.0", "[dependencies]\nlinuxonly = { path = \"../linuxonly\", when = \"os(linux)\" }\n", ("src/main.sb", """
            module App

            fn main() {
                #if os(linux)
                print("linux")
                #else
                print("\(Platform.os) \(Platform.arch)")
                #endif
            }
            """));

        var target = NativeTarget.Of("x86_64-apple-darwin");
        var built = PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new BuildRequest { Target = target });
        Assert.True(built is [_], fixture.Errors.ToString());
        Assert.Equal(Path.Combine(fixture.Root, "app", "target", "x86_64-apple-darwin", "dev", "app"), built[0].Path);
        Assert.Equal(Machine(target), MachineOf(File.ReadAllBytes(built[0].Path)));
        if (Rosetta()) {
            Assert.Equal(("macos x86_64\n", 0), PackageFixture.Execute(built[0].Path));
        }

        // Built for this machine, the same package goes where it always did.
        var host = PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new BuildRequest());
        Assert.True(host is [_], fixture.Errors.ToString());
        Assert.Equal(Path.Combine(fixture.Root, "app", "target", "dev", "app"), host[0].Path);
        Assert.Equal(($"macos arm64\n", 0), PackageFixture.Execute(host[0].Path));
    }

    [Fact]
    public void ADependencysConditionIsTestedAgainstTheTarget() {
        Assert.True(DependencyCondition.Holds("os(linux)", "linux", "arm64"));
        Assert.False(DependencyCondition.Holds("os(linux)", "macos", "arm64"));
        Assert.True(DependencyCondition.Holds("arch(x64)", "linux", "x86_64"));
        Assert.False(DependencyCondition.Holds("arch(arm64)", "linux", "x86_64"));
    }

    // ---- Helpers -----------------------------------------------------------

    static CompileResult Compile(NativeTarget target) {
        var tree = SyntaxTree.Parse(new SourceText(Program, "main.sb"), Pipeline.ParseOptionsFor(true, target));
        var result = Pipeline.Compile([tree], target: target);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        return result;
    }

    /// <summary>What an object or executable's header says it was built for.</summary>
    static string MachineOf(byte[] bytes) =>
        bytes is [0x7F, (byte)'E', (byte)'L', (byte)'F', ..] ? $"elf {BitConverter.ToUInt16(bytes, 18)}"
        : bytes is [0xCF, 0xFA, 0xED, 0xFE, ..] ? $"mach-o {BitConverter.ToUInt32(bytes, 4):x}"
        : bytes is [0x00, (byte)'a', (byte)'s', (byte)'m', ..] ? "wasm"
        : bytes.Length > 2 ? $"coff {BitConverter.ToUInt16(bytes, 0):x}"
        : "nothing";

    /// <summary>What <see cref="MachineOf" /> says for the target: ELF's e_machine, Mach-O's CPU type, COFF's machine.</summary>
    static string Machine(NativeTarget target) => (target.Platform, target.Arch) switch {
        (NativePlatform.Linux, "x86_64") => "elf 62",
        (NativePlatform.Linux, "arm64") => "elf 183",
        (NativePlatform.MacOS, "x86_64") => "mach-o 1000007",
        (NativePlatform.MacOS, "arm64") => "mach-o 100000c",
        (NativePlatform.Windows, "x86_64") => "coff 8664",
        (NativePlatform.Wasi, _) => "wasm",
        _ => "unknown",
    };

    /// <summary>Whether this Mac runs x86-64 programs.</summary>
    static bool Rosetta() {
        try {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/arch", ["-x86_64", "/usr/bin/true"]) { RedirectStandardError = true })!;
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        } catch (System.ComponentModel.Win32Exception) {
            return false;
        }
    }
}
