using System.Diagnostics;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A build profile's settings (docs/05 §5.2.9), each as the build acts
///     on it: the opt-level's SIL passes and clang level, debug information
///     in an optimised build, <c>arc-checks</c>, <c>debug-assertions</c>,
///     <c>incremental</c>, and a warning for what a target cannot do.
/// </summary>
public sealed class ProfileTests {
    static bool CanBuild => Programs.CanBuild;

    const string Program = """
                           module Levels

                           class Counter {
                               var count: Int
                               init(count: Int) {
                                   self.count = count
                               }
                               fn bump() {
                                   count = count + 1
                               }
                           }

                           fn twice(_ x: Int) -> Int {
                               return x * 2
                           }

                           fn main() {
                               let counter = Counter(count: 0)
                               var total = 0
                               var words: String[>..] = []
                               for i in 0..<100 {
                                   total = total + twice(i)
                                   counter.bump()
                                   if i % 25 == 0 {
                                       words.append("w\(i)")
                                   }
                               }
                               print(total)
                               print(counter.count)
                               print(words.count)
                           }
                           """;

    /// <summary>
    ///     Each opt-level runs its own SIL passes (<see cref="SilPassManager.ForLevel" />):
    ///     a small callee is inlined at 2, 3 and s, and left a call at 0, 1
    ///     and z, which inline nothing.
    /// </summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("2", true)]
    [InlineData("3", true)]
    [InlineData("s", true)]
    [InlineData("z", false)]
    public void TheOptLevelPicksTheSilPasses(string level, bool inlines) {
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Program, "levels.sb"))], optLevel: level);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var main = SilPrinter.Print(result.Sil!.Find("Levels.main")!);
        Assert.Equal(!inlines, main.Contains("function_ref @Levels.twice", StringComparison.Ordinal));
    }

    /// <summary>Every opt-level builds the same program, natively, with clang's level of the same name.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("s")]
    [InlineData("z")]
    public void EveryOptLevelBuildsTheSameProgram(string level) {
        if (!CanBuild) {
            return;
        }

        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Program, "levels.sb"))], optLevel: level, checkArc: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var (stdout, stderr, exitCode) = BuildAndRun(result.LlvmIr!, new NativeOptions { OptLevel = level });
        Assert.Equal(0, exitCode);
        Assert.Equal("9900\n100\n4\n", stdout);
        Assert.Equal("", stderr);
    }

    /// <summary>
    ///     An optimised build with debug information: the SIL passes run, as
    ///     they do without it, and what they leave is debug information the
    ///     DWARF verifier passes and lldb reads — a frame inlined by LLVM
    ///     still names its function and its arguments.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("s")]
    public async Task AnOptimisedBuildHasValidDebugInformation(string level) {
        if (!CanBuild || OperatingSystem.IsWindows()) {
            return;
        }

        const string source = """
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
                                  let names = ["a", "b"]
                                  print(gcd(p.x, p.y) + names.count)
                              }
                              """;
        var directory = Directory.CreateTempSubdirectory("sable-debug-optimised-");
        try {
            File.WriteAllText(Path.Combine(directory.FullName, "debug.sb"), source);
            var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(source, "debug.sb"))], directory.FullName, optLevel: level);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            Assert.Contains("!DICompileUnit(", result.LlvmIr!, StringComparison.Ordinal);

            var executable = Path.Combine(directory.FullName, "debug");
            var build = NativeBuild.Build(result.LlvmIr!, executable, new NativeOptions { OptLevel = level, DebugInfo = true });
            Assert.True(build.Succeeded, build.Output);
            Assert.Equal(("23\n", 0), PackageFixture.Execute(executable));

            // The DWARF verifier: Apple's dwarfdump on the dSYM, LLVM's elsewhere.
            var (verifier, file) = OperatingSystem.IsMacOS() ? ("dwarfdump", executable + ".dSYM") : ("llvm-dwarfdump", executable);
            if (Run(verifier, "--verify", file) is { } verified) {
                Assert.True(verified.ExitCode == 0, verified.Output);
                Assert.Contains("No errors.", verified.Output, StringComparison.Ordinal);
            }

            if (!OperatingSystem.IsMacOS() || Run("lldb", "--version") is not { ExitCode: 0 }) {
                return;
            }

            var start = new ProcessStartInfo("lldb") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] {
                         "--batch",
                         "-o", "breakpoint set --file debug.sb --line 12",
                         "-o", "run",
                         "-o", "bt",
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
            Assert.Contains("gcd(a=1071, b=462) at debug.sb:12", text, StringComparison.Ordinal);
            Assert.Contains("main at debug.sb:23", text, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     <c>arc-checks</c>: compiled code's retain of an object whose count
    ///     is zero — whose deinit has run — traps, where the unchecked build
    ///     counts it. (A safe program cannot reach one; the IR here makes one.)
    /// </summary>
    [Fact]
    public void ArcChecksTrapOnAnObjectWhoseDeinitHasRun() {
        var checkedIr = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Program, "levels.sb"))], checkArc: true).LlvmIr!;
        Assert.Contains("call void @sable_arc_dead(ptr %object, i32 0)", checkedIr, StringComparison.Ordinal);
        Assert.DoesNotContain("@sable_arc_dead", Pipeline.Compile([SyntaxTree.Parse(new SourceText(Program, "levels.sb"))]).LlvmIr!, StringComparison.Ordinal);
        if (!CanBuild) {
            return;
        }

        string Dead(bool checkArc) => $$"""
                                        @dead = global { ptr, i32, i32 } { ptr null, i32 0, i32 0 }
                                        {{LlvmEmitter.RetainAndRelease(checkArc)}}
                                        declare void @sable_set_arguments(i32, ptr)
                                        declare void @sable_share(ptr)

                                        define i32 @main(i32 %argc, ptr %argv) {
                                          call void @sable_set_arguments(i32 %argc, ptr %argv)
                                          call void @sable.retain(ptr @dead)
                                          ret i32 0
                                        }
                                        """;

        var (_, stderr, exitCode) = BuildAndRun(Dead(checkArc: true), new NativeOptions { OptLevel = "0" });
        Assert.NotEqual(0, exitCode);
        Assert.StartsWith("fatal error: retain of an object whose deinit has run", stderr, StringComparison.Ordinal);
        Assert.Equal(0, BuildAndRun(Dead(checkArc: false), new NativeOptions { OptLevel = "0" }).ExitCode);
    }

    /// <summary>
    ///     A package built with a profile's settings: <c>opt-level = "z"</c>
    ///     with debug information, <c>arc-checks</c>, <c>debug-assertions</c>
    ///     choosing <c>DEBUG</c> in an optimised build, and
    ///     <c>incremental = false</c> compiling it whole.
    /// </summary>
    [Fact]
    public void APackageBuildsWithItsProfilesSettings() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", """
            [profile.release]
            opt-level = "z"
            debug = true
            arc-checks = true
            debug-assertions = true
            incremental = false
            codegen-units = 4
            """,
            ("src/main.sb", """
                module App

                fn main() {
                    #if DEBUG
                    print("debug assertions")
                    #else
                    print("release")
                    #endif
                }
                """));

        var release = Assert.Single(PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new(Profile: "release", Timings: true))!);
        Assert.Equal(("debug assertions\n", 0), PackageFixture.Execute(release.Path));
        var errors = fixture.Errors.ToString();
        Assert.Contains("[optimised + debuginfo]", errors, StringComparison.Ordinal);
        Assert.Contains("Built app: whole", errors, StringComparison.Ordinal);
        Assert.Contains("profile.release.codegen-units: a build compiles one unit per package", errors, StringComparison.Ordinal);

        // The dev profile, unoptimised, defines DEBUG by default.
        var dev = Assert.Single(PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new())!);
        Assert.Equal(("debug assertions\n", 0), PackageFixture.Execute(dev.Path));
    }

    /// <summary>
    ///     <c>strip = "local"</c> (#300) leaves the program's own symbols out of
    ///     a debug build — every one is hidden, so local in the executable —
    ///     and a trap's backtrace names each frame by the function its debug
    ///     information says the frame is in, and the line, as before.
    /// </summary>
    [Fact]
    public async Task ABacktraceNamesFramesByTheirDebugInformationWhereTheSymbolsAreStripped() {
        if (!CanBuild || !(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", """
            [profile.dev]
            strip = "local"
            """,
            ("src/main.sb", """
                module App

                fn inner(_ n: Int) -> Int {
                    precondition(n < 3, "small")
                    return n
                }

                fn outer(_ n: Int) -> Int {
                    return inner(n) + 1
                }

                fn main() {
                    print(outer(1))
                    print(outer(5))
                }
                """));

        var dev = Assert.Single(PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new())!);
        using (var nm = Process.Start(new ProcessStartInfo("nm", [dev.Path]) { RedirectStandardOutput = true }.Utf8())!) {
            var symbols = nm.StandardOutput.ReadToEnd();
            nm.WaitForExit();
            Assert.DoesNotContain("sable.App.inner", symbols, StringComparison.Ordinal);
        }

        var start = new ProcessStartInfo(dev.Path) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
        start.Environment["SABLE_BACKTRACE"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var trace = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal("2\n", await output);
        Assert.Matches(@"App\.inner at (src/)?main\.sb:4", trace.Replace('\\', '/'));
        Assert.Matches(@"App\.outer at (src/)?main\.sb:9", trace.Replace('\\', '/'));
    }

    /// <summary>
    ///     A profile of the package's own, <c>--profile</c>'s: release's
    ///     settings as the manifest has them, changed, built to
    ///     <c>target/&lt;name&gt;/</c>; a profile the manifest lacks is refused.
    /// </summary>
    [Fact]
    public void APackageBuildsWithAProfileOfItsOwn() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", """
            [profile.shipping]
            inherits = "release"
            debug-assertions = true
            """,
            ("src/main.sb", """
                module App

                fn main() {
                    #if DEBUG
                    print("debug assertions")
                    #else
                    print("release")
                    #endif
                }
                """));

        var workspace = fixture.Load("app");
        var built = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new(Profile: "shipping"))!);
        Assert.Equal(Path.Combine(workspace.TargetDirectory, "shipping"), Path.GetDirectoryName(built.Path));
        Assert.True(built.Profile.Optimise);
        Assert.Equal(("debug assertions\n", 0), PackageFixture.Execute(built.Path));
        Assert.Throws<PackageException>(() => PackageBuilder.Build(workspace, fixture.Environment, new(Profile: "nope")));
        Assert.Equal("shipping", Sable.Driver.Commands.PackageCommand.ParseBuildRequest(["--profile", "shipping"])!.Profile);
        Assert.Equal("release", Sable.Driver.Commands.PackageCommand.ParseBuildRequest(["--release"])!.Profile);
    }

    /// <summary>What a target cannot do with a profile's settings is a warning, not taken silently.</summary>
    [Fact]
    public void WhatATargetCannotDoIsWarnedAbout() {
        var profile = Profile.Release with {
            Lto = LtoMode.Thin,
            Strip = StripMode.Symbols,
            Rpath = true,
            Recorded = new Dictionary<string, string> { ["lto"] = "thin", ["strip"] = "symbols", ["rpath"] = "true" },
        };
        // Windows, but not this machine: on Windows itself the link is lld's, which takes bitcode.
        var windows = NativeTarget.Host is { Platform: NativePlatform.Windows, Arch: "x86_64" } ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        var warnings = profile.UnmetFor(NativeTarget.Of(windows)).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.StartsWith($"profile.release.lto: the link for {windows} cannot take LLVM bitcode", warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("profile.release.strip: has no effect", warnings[1], StringComparison.Ordinal);
        Assert.StartsWith("profile.release.rpath: has no effect", warnings[2], StringComparison.Ordinal);

        // A default left alone is not warned about, and Linux strips and finds libraries by rpath.
        Assert.Empty(Profile.Release.UnmetFor(NativeTarget.Of(windows)));
        Assert.Empty(profile.UnmetFor(NativeTarget.Of("x86_64-unknown-linux-gnu")));
    }

    static (string Stdout, string Stderr, int ExitCode) BuildAndRun(string llvmIr, NativeOptions options) {
        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-profile-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(llvmIr, executable, options);
            Assert.True(build.Succeeded, build.Output);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (stdout.Result, stderr, process.ExitCode);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    /// <summary>A tool's exit code and output, or null when it is not installed.</summary>
    static (int ExitCode, string Output)? Run(string tool, params string[] arguments) {
        try {
            var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output.Result + errors);
        } catch (System.ComponentModel.Win32Exception) {
            return null;
        }
    }
}
