using System.Diagnostics;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Building for another machine (docs/04 §2.3, docs/05 §5.1): target
///     triples, the IR's data layout and triple, objects for each target
///     clang knows, and <c>--target</c> through a package build.
/// </summary>
/// <remarks>
///     This machine may have no sysroot for another, so most targets are
///     checked as object files — which need none — by their headers. On an
///     Apple Silicon Mac an x86-64 macOS program is also linked and, under
///     Rosetta, run; WasiTests run WebAssembly where wasmtime is.
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
        Assert.True(target.HasRuntime);
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
        Assert.DoesNotContain(NativeTarget.Host.ClangFlags, static f => f.StartsWith("--target", StringComparison.Ordinal));
        Assert.Equal(ParseOptions.HostOs, NativeTarget.Host.Os);
        Assert.Equal(ParseOptions.HostArch, NativeTarget.Host.Arch);
        Assert.Equal(["--target=x86_64-unknown-linux-gnu"], NativeTarget.Of("x86_64-unknown-linux-gnu").ClangFlags);
        Assert.Equal(["--target=aarch64-pc-windows-msvc", "-fms-runtime-lib=dll"], NativeTarget.Of("aarch64-pc-windows-msvc").ClangFlags);
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

    /// <summary>
    ///     Debug information for Windows is CodeView, which its linker gathers
    ///     into the PDB its debuggers read; for everything else, DWARF.
    /// </summary>
    [Theory]
    [InlineData("x86_64-pc-windows-msvc", true)]
    [InlineData("x86_64-unknown-linux-gnu", false)]
    public void DebugInformationIsCodeViewForWindows(string triple, bool codeView) {
        if (!CanBuild) {
            return;
        }

        var target = NativeTarget.Of(triple);
        var directory = Directory.CreateTempSubdirectory("sable-target-");
        try {
            var tree = SyntaxTree.Parse(new SourceText(Program, "main.sb"), Pipeline.ParseOptionsFor(false, target));
            var result = Pipeline.Compile([tree], directory.FullName, target: target);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            Assert.Equal(codeView, result.LlvmIr!.Contains("!\"CodeView\", i32 1", StringComparison.Ordinal));

            var path = Path.Combine(directory.FullName, "program.o");
            var compiled = NativeObjects.CompileIr(result.LlvmIr!, path, NativeOptions.Of(false, true) with { Target = target, Lto = LtoMode.Off });
            Assert.True(compiled.Succeeded, compiled.Output);
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Assert.Equal(codeView, text.Contains(".debug$S", StringComparison.Ordinal));
            Assert.Equal(!codeView, text.Contains(".debug_info", StringComparison.Ordinal));
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     On Windows a debug build's PDB holds the program's CodeView: its
    ///     source file and its functions, by their Sable names.
    /// </summary>
    [Fact]
    public void AWindowsDebugBuildsPdbDescribesTheProgram() {
        if (!CanBuild || !OperatingSystem.IsWindows()) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-pdb-");
        try {
            var result = Pipeline.Compile(new SourceText(Program, "main.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var executable = Path.Combine(directory.FullName, "program.exe");
            var built = NativeBuild.Build(result.LlvmIr!, executable, optimise: false, debug: true);
            Assert.True(built.Succeeded, built.Output);
            var pdb = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.ChangeExtension(executable, ".pdb")));
            Assert.Contains("main.sb", pdb, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     A build for another machine whose C library is not where clang
    ///     looks says so, and where it is looked for — not an internal error
    ///     made of clang's: here a sysroot with nothing in it.
    /// </summary>
    [Theory]
    [InlineData("wasm32-wasi", "wasi-libc")]
    [InlineData("linux", "debootstrap")]
    public void AMissingSysrootIsSaidWithWhereOneIsLookedFor(string triple, string hint) {
        if (!CanBuild) {
            return;
        }

        if (triple == "linux") {
            // Linux, but not this machine's.
            triple = NativeTarget.Host is { Platform: NativePlatform.Linux, Arch: "x86_64" } ? "aarch64-unknown-linux-gnu" : "x86_64-unknown-linux-gnu";
        }

        var target = NativeTarget.Of(triple);
        var sysroot = Directory.CreateTempSubdirectory("sable-sysroot-");
        var cache = Directory.CreateTempSubdirectory("sable-runtime-");
        try {
            var build = NativeBuild.Build("", Path.Combine(sysroot.FullName, "never"), NativeOptions.Of(true, false) with { Target = target, Sysroot = sysroot.FullName }, new BuildCache(cache.FullName));
            Assert.False(build.Succeeded);
            Assert.True(build.Setup, build.Output);
            Assert.StartsWith($"the C library for {triple} is not in the sysroot {sysroot.FullName}", build.Report, StringComparison.Ordinal);
            Assert.Contains(target.SysrootHome, build.Output, StringComparison.Ordinal);
            Assert.Contains(hint, build.Output, StringComparison.Ordinal);
            Assert.Contains("clang said:", build.Output, StringComparison.Ordinal);
        } finally {
            sysroot.Delete(true);
            cache.Delete(true);
        }
    }

    /// <summary>
    ///     A sysroot not given is looked for: first under
    ///     <c>$SABLE_HOME/sysroots/&lt;triple&gt;</c>, then, for WASI, where
    ///     wasi-sdk and wasi-libc install theirs. The host needs none, and a
    ///     build's key holds the one it uses.
    /// </summary>
    [Fact]
    public void ASysrootIsLookedForWhereTheTargetsAreKept() {
        var wasi = NativeTarget.Of("wasm32-wasi");
        var candidates = wasi.SysrootCandidates().ToList();
        Assert.Equal(Path.Combine(NativeBuild.SableHome, "sysroots", "wasm32-wasi"), candidates[0]);
        Assert.Contains("/opt/wasi-sdk/share/wasi-sysroot", candidates);
        Assert.Contains("/usr/share/wasi-sysroot", candidates);

        var linux = NativeTarget.Of("aarch64-unknown-linux-gnu");
        Assert.Equal([linux.SysrootHome], linux.SysrootCandidates());
        Assert.Null(NativeTarget.Host.FindSysroot());

        var given = NativeOptions.Of(true, false) with { Target = wasi, Sysroot = "/somewhere" };
        Assert.Equal(["--sysroot=/somewhere"], NativeBuild.SysrootFlags(given));
        Assert.Contains("|/somewhere|", given.Key, StringComparison.Ordinal);
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
