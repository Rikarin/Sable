using System.Diagnostics;
using Sable.Driver;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Runtime profiles (docs/06 §6.1, #218): what each links, the stubs of
///     what a smaller one leaves out, and the errors where a program's own
///     code uses it.
/// </summary>
[Native]
public sealed class RuntimeProfileTests {
    const string Hello = "module Hello\n\nfn main() {\n    for name in [\"world\", \"Sable\"] {\n        print(\"hello, \\(name)\")\n    }\n}\n";

    [Fact]
    public void AProfileIsNamedAsAManifestWritesIt() {
        foreach (var profile in Enum.GetValues<RuntimeProfile>()) {
            Assert.Equal(profile, RuntimeProfiles.Parse(profile.Name()));
        }

        Assert.Null(RuntimeProfiles.Parse("tiny"));
        Assert.Empty(RuntimeProfile.Full.Flags());
        Assert.Equal(["-DSABLE_RUNTIME_PROFILE=1"], RuntimeProfile.Minimal.Flags());
        // The full runtime is compiled, and keyed, as it always was.
        Assert.Equal(new NativeOptions().Key, new NativeOptions { Runtime = RuntimeProfile.Full }.Key);
        Assert.NotEqual(new NativeOptions().Key, new NativeOptions { Runtime = RuntimeProfile.Standard }.Key);
    }

    /// <summary>Each function and variable the full runtime exports and the profile's does not is a stub: the function traps, naming itself; the variable is zero.</summary>
    [Fact]
    public void WhatAProfileLeavesOutIsAStub() {
        const string full = """
                            target datalayout = "e-m:o"
                            target triple = "arm64-apple-macosx"
                            %struct.Pair = type { i64, i64 }
                            @sable_kept = global i64 7, align 8
                            @sable_gone = global [2 x i64] [i64 1, i64 2], align 8
                            @local = internal global i64 0, align 8
                            @elsewhere = external global i64, align 8
                            define i64 @sable_core(i64 noundef %0) #0 {
                            define zeroext i1 @sable_task_spawn(ptr noundef %0, %struct.Pair %1) #2 {
                            define internal void @helper() #0 {
                            define linkonce_odr void @inline_one() #0 {
                            """;
        const string profile = """
                               @sable_kept = global i64 7, align 8
                               define i64 @sable_core(i64 noundef %0) #0 {
                               """;
        var stubs = RuntimeProfiles.AbsentStubs(full, profile);
        Assert.Contains("target triple = \"arm64-apple-macosx\"", stubs, StringComparison.Ordinal);
        Assert.Contains("%struct.Pair = type { i64, i64 }", stubs, StringComparison.Ordinal);
        Assert.Contains("define zeroext i1 @sable_task_spawn(ptr noundef %0, %struct.Pair %1) {\n  call void @sable_runtime_absent(ptr @.absent.0)", stubs, StringComparison.Ordinal);
        Assert.Contains("c\"sable_task_spawn\\00\"", stubs, StringComparison.Ordinal);
        Assert.Contains("@sable_gone = global [2 x i64] zeroinitializer, align 8", stubs, StringComparison.Ordinal);
        foreach (var kept in new[] { "@sable_core(", "@sable_kept =", "@helper", "@inline_one", "@local", "@elsewhere" }) {
            Assert.DoesNotContain(kept, stubs, StringComparison.Ordinal);
        }
    }

    /// <summary>Every profile's runtime builds, the smaller ones with their stubs beside them, and a program linked with each runs.</summary>
    [Fact]
    public void AProgramRunsWithEveryProfile() {
        if (!Programs.CanBuild) {
            return;
        }

        var result = Pipeline.Compile(new SourceText(Hello, "hello.sb"), optimised: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var sizes = new Dictionary<RuntimeProfile, long>();
        foreach (var profile in Enum.GetValues<RuntimeProfile>()) {
            var options = NativeOptions.Of(true, false) with { Runtime = profile };
            var runtime = NativeBuild.Runtime(options, [], BuildCache.Default);
            Assert.True(runtime.Result.Succeeded, runtime.Result.Output);
            Assert.Equal(profile == RuntimeProfile.Full ? 1 : 2, runtime.Objects.Count);
            sizes[profile] = new FileInfo(runtime.Objects[0]).Length;
            Assert.Equal("hello, world\nhello, Sable\n", Run(result.LlvmIr!, options).Stdout);
        }

        Assert.True(sizes[RuntimeProfile.Minimal] < sizes[RuntimeProfile.Standard] && sizes[RuntimeProfile.Standard] < sizes[RuntimeProfile.Full], string.Join(", ", sizes));
    }

    /// <summary>What the standard library reaches of a part left out traps as it is called, naming the function and the profile.</summary>
    [Fact]
    public void ReachingWhatTheProfileLeavesOutTraps() {
        if (!Programs.CanBuild) {
            return;
        }

        var source = "module Absent\n\nfn main() {\n    print(\"before\")\n    print(Runtime.processId())\n}\n";
        var result = Pipeline.Compile(new SourceText(source, "absent.sb"), optimised: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var run = Run(result.LlvmIr!, NativeOptions.Of(true, false) with { Runtime = RuntimeProfile.Minimal });
        Assert.Equal("before\n", run.Stdout);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("'sable_process_id' is in a part of the runtime the minimal runtime profile leaves out", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, Run(result.LlvmIr!, NativeOptions.Of(true, false) with { Runtime = RuntimeProfile.Standard }).ExitCode);
    }

    /// <summary>The minimal profile refuses, where the program's own code says it, an import of a module that needs more of the runtime and 'async' code (S3451).</summary>
    [Fact]
    public void TheMinimalProfileRefusesWhatItLeavesOut() {
        static List<string> Denied(string source, RuntimeProfile profile = RuntimeProfile.Minimal) =>
            Pipeline.RuntimeProfileDenied([SyntaxTree.Parse(new SourceText(source, "main.sb"))], profile);

        Assert.Empty(Denied("module M\n\nimport Sable.Text\nimport Sable.Collections\n\nfn main() {}\n"));
        var net = Assert.Single(Denied("module M\n\nimport Sable.Net\n\nfn main() {}\n"));
        Assert.Contains("error[S3451]", net, StringComparison.Ordinal);
        Assert.Contains("'Sable.Net' needs the runtime's sockets", net, StringComparison.Ordinal);
        Assert.Contains("main.sb:3:8", net, StringComparison.Ordinal);
        Assert.Contains("'Sable.Crypto' imports 'Sable.IO', which needs", Assert.Single(Denied("module M\n\nimport Sable.Crypto\n\nfn main() {}\n")), StringComparison.Ordinal);

        const string tasks = "module M\n\nasync fn work() -> Int {\n    return 1\n}\n\nasync fn main() {\n    print(await work())\n}\n";
        var errors = Denied(tasks);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, static e => Assert.Contains("'async' code runs on the runtime's executor", e, StringComparison.Ordinal));
        Assert.Empty(Denied(tasks, RuntimeProfile.Standard));
        Assert.Empty(Denied("module M\n\nimport Sable.Net\n\nfn main() {}\n", RuntimeProfile.Standard));
    }

    [Fact]
    public void EveryModuleBeyondTheMinimalProfileIsOne() {
        Assert.All(Library.BeyondMinimal.Keys, static m => Assert.Contains(m, Library.ModuleNames));
    }

    /// <summary><c>sable build --runtime</c>, and a manifest's <c>[targets.&lt;triple&gt;] runtime</c>, pick the profile.</summary>
    [Fact]
    public void TheCommandLineAndTheManifestPickTheProfile() {
        using var fixture = new PackageFixture();
        fixture.Write("async.sb", "module A\n\nasync fn main() {\n    print(1)\n}\n");
        var refused = SableProcess.Run(fixture.Root, fixture.Home, "build", "async.sb", "--runtime", "minimal", "--emit", "llvm-ir");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("S3451", refused.Errors, StringComparison.Ordinal);
        Assert.Equal(2, SableProcess.Run(fixture.Root, fixture.Home, "build", "async.sb", "--runtime", "tiny").ExitCode);

        fixture.Write("p/package.sb", """
                                      import Sable.Build

                                      let manifest = Package {
                                          name: "p",
                                          version: "0.1.0",
                                          targetSettings: [.target("wasm32-wasi", reflection: false, runtime: .minimal), .target("x86_64-unknown-linux-gnu")],
                                      }
                                      """);
        fixture.Write("p/src/lib.sb", "module P\n");
        var manifest = Sable.Driver.Packages.ManifestFiles.Load(Path.Combine(fixture.Root, "p"));
        Assert.Equal(RuntimeProfile.Minimal, manifest.TargetSettingsFor(NativeTarget.Of("wasm32-wasi"))!.Runtime);
        Assert.Null(manifest.TargetSettingsFor(NativeTarget.Of("x86_64-unknown-linux-gnu"))!.Runtime);

        if (!Programs.CanBuild) {
            return;
        }

        const string main = "module Main\n\nasync fn main() {\n    print(1)\n}\n";
        fixture.Package("app", "app", "0.1.0", $"[targets.{HostTriple}]\nruntime = \"minimal\"\n", ("src/main.sb", main));
        Assert.Null(Sable.Driver.Packages.PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new()));
        Assert.Contains("S3451", fixture.Errors.ToString(), StringComparison.Ordinal);
        Assert.NotNull(Sable.Driver.Packages.PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new() { Runtime = RuntimeProfile.Standard }));
    }

    /// <summary>This machine as a triple <c>--target</c> takes.</summary>
    static string HostTriple => (NativeTarget.Host.Platform, NativeTarget.Host.Arch) switch {
        (NativePlatform.MacOS, "arm64") => "aarch64-apple-darwin",
        (NativePlatform.MacOS, _) => "x86_64-apple-darwin",
        (NativePlatform.Linux, "arm64") => "aarch64-unknown-linux-gnu",
        (NativePlatform.Linux, _) => "x86_64-unknown-linux-gnu",
        _ => "x86_64-pc-windows-msvc",
    };

    static Programs.Execution Run(string llvmIr, NativeOptions options) {
        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(llvmIr, executable, options);
            Assert.True(build.Succeeded, build.Output);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true }.Utf8();
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            var run = ChildProcess.Run(start, ChildProcess.ProgramLimit);
            return new(run.Stdout, run.Stderr, run.ExitCode);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }
}
