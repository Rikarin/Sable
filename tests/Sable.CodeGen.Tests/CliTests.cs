using System.Text.Json.Nodes;
using Sable.Driver.Commands;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The <c>sable</c> binary's own surface (docs/05 §5.1): <c>--version</c>,
///     <c>help</c>, completions, the options every command takes, toolchains
///     and <c>bindgen</c>.
/// </summary>
public sealed class CliTests {
    [Fact]
    public void VersionAndHelpForACommand() {
        using var fixture = new PackageFixture();
        var version = SableProcess.Run(fixture.Root, fixture.Home, "--version");
        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith($"sable {Toolchains.RunningVersion} (", version.Output, StringComparison.Ordinal);
        Assert.Equal(version.Output, SableProcess.Run(fixture.Root, fixture.Home, "-V").Output);

        var help = SableProcess.Run(fixture.Root, fixture.Home, "help", "build");
        Assert.Equal(0, help.ExitCode);
        Assert.Equal(SableProcess.Run(fixture.Root, fixture.Home, "build", "--help").Output, help.Output);
        Assert.Contains("--release", help.Output, StringComparison.Ordinal);

        var general = SableProcess.Run(fixture.Root, fixture.Home, "--help");
        Assert.Contains("--message-format", general.Output, StringComparison.Ordinal);
        Assert.Contains("toolchain", general.Output, StringComparison.Ordinal);
        Assert.Equal(2, SableProcess.Run(fixture.Root, fixture.Home, "help", "nothing").ExitCode);
    }

    [Fact]
    public void TestAndBenchSayAFileIsOptional() {
        Assert.Contains("usage: sable test [<file.sb>|<directory>...]", new TestCommand().Usage, StringComparison.Ordinal);
        Assert.Contains("usage: sable bench [<file.sb>...]", new BenchCommand().Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalOptionsAreTakenFromAnywhereBeforeTheProgramsArguments() {
        var (options, rest) = GlobalOptions.Parse(["-q", "build", "--release", "-j4", "--offline", "--color=never", "--", "-v"], out _)!.Value;
        Assert.Equal(Verbosity.Quiet, options.Verbosity);
        Assert.Equal(4, options.Jobs);
        Assert.True(options.Offline);
        Assert.Equal(ColorChoice.Never, options.Color);
        Assert.Equal(["build", "--release", "--", "-v"], rest);

        (options, rest) = GlobalOptions.Parse(["check", "--message-format", "json", "-j", "2", "-v"], out _)!.Value;
        Assert.Equal(MessageFormat.Json, options.MessageFormat);
        Assert.Equal(2, options.Jobs);
        Assert.Equal(Verbosity.Verbose, options.Verbosity);
        Assert.Equal(["check"], rest);

        Assert.Null(GlobalOptions.Parse(["-j", "none"], out var error));
        Assert.Contains("-j takes a number", error, StringComparison.Ordinal);
        Assert.Null(GlobalOptions.Parse(["--color=sometimes"], out error));
        Assert.Contains("auto, always or never", error, StringComparison.Ordinal);
        Assert.Null(GlobalOptions.Parse(["--message-format=xml"], out _));
    }

    [Fact]
    public void JsonMessagesCarryEachDiagnosticsFields() {
        using var fixture = new PackageFixture();
        fixture.Write("bad.sb", "module M\n\nfn main() {\n    let x: Int = \"a\"\n}\n");
        var result = SableProcess.Run(fixture.Root, fixture.Home, "check", "bad.sb", "--message-format=json");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Errors);

        var messages = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => JsonNode.Parse(l)!.AsObject()).ToList();
        var diagnostic = Assert.Single(messages, static m => m["type"]!.GetValue<string>() == "diagnostic");
        Assert.Equal("error", diagnostic["severity"]!.GetValue<string>());
        Assert.Equal("S3004", diagnostic["code"]!.GetValue<string>());
        Assert.Equal("bad.sb", diagnostic["file"]!.GetValue<string>());
        Assert.Equal(4, diagnostic["line"]!.GetValue<int>());
        Assert.Equal(18, diagnostic["column"]!.GetValue<int>());
        Assert.Contains("expected 'Int'", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("^^^", diagnostic["rendered"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonMessagesForAManifestsErrorsAndStatusLines() {
        var output = new StringWriter();
        using (var writer = new JsonMessageWriter(output)) {
            writer.WriteLine("   Compiling app v1.0.0");
            writer.WriteLine("error: /p/sable.toml:3: package.version: '1.x' is not a version");
            writer.WriteLine();
        }

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("message", lines[0]["type"]!.GetValue<string>());
        Assert.Equal("   Compiling app v1.0.0", lines[0]["text"]!.GetValue<string>());
        Assert.Equal("/p/sable.toml", lines[1]["file"]!.GetValue<string>());
        Assert.Equal(3, lines[1]["line"]!.GetValue<int>());
        Assert.Equal("package.version: '1.x' is not a version", lines[1]["message"]!.GetValue<string>());
    }

    [Fact]
    public void ColourIsAskedForOrLeftOut() {
        Assert.Equal("\e[1;31merror[S3004]:\e[0m bad", ColorWriter.Colour("error[S3004]: bad"));
        Assert.Equal("\e[1;33mwarning:\e[0m odd", ColorWriter.Colour("warning: odd"));
        Assert.Equal("\e[1;32m   Compiling\e[0m app v1.0.0", ColorWriter.Colour("   Compiling app v1.0.0"));
        Assert.Equal("plain text", ColorWriter.Colour("plain text"));

        using var fixture = new PackageFixture();
        fixture.Write("bad.sb", "module M\n\nfn main() {\n    let x: Int = \"a\"\n}\n");
        Assert.Contains("\e[1;31merror", SableProcess.Run(fixture.Root, fixture.Home, "--color=always", "check", "bad.sb").Errors, StringComparison.Ordinal);
        Assert.DoesNotContain("\e[", SableProcess.Run(fixture.Root, fixture.Home, "check", "bad.sb").Errors, StringComparison.Ordinal);
        Assert.DoesNotContain("\e[", SableProcess.Run(fixture.Root, fixture.Home, new Dictionary<string, string?> { ["NO_COLOR"] = "1" }, "--color=auto", "check", "bad.sb").Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void QuietLeavesOutTheStatusLines() {
        using var fixture = new PackageFixture();
        Assert.Equal(0, SableProcess.Run(fixture.Root, fixture.Home, "new", "app", "--lib").ExitCode);
        var app = Path.Combine(fixture.Root, "app");
        Assert.Contains("Checking app", SableProcess.Run(app, fixture.Home, "check").Errors, StringComparison.Ordinal);
        var quiet = SableProcess.Run(app, fixture.Home, "check", "-q");
        Assert.Equal(0, quiet.ExitCode);
        Assert.Equal("", quiet.Errors);
    }

    [Fact]
    public void OfflineResolvesFromTheCacheOnly() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0", value: "10");
        fixture.Package("app", "app", "1.0.0", "[dependencies]\nnumbers = \"1\"\n", ("src/lib.sb", "module App\n"));
        var offline = fixture.Environment with { Offline = true };

        var error = Assert.ThrowsAny<Exception>(() => PackageGraph.Resolve(fixture.Load("app"), offline));
        Assert.Contains("--offline", error.Message, StringComparison.Ordinal);

        // Once it is in the cache, offline is enough.
        PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Equal("numbers", PackageGraph.Resolve(fixture.Load("app"), offline).Packages["numbers"].Name);

        var adding = Assert.Throws<PackageException>(() => PackageOperations.Add(fixture.Load("app"), offline, fixture.Load("app").Members[0], "other", false, null));
        Assert.Contains("--offline", adding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionsNameEveryCommandAndItsOptions() {
        using var fixture = new PackageFixture();
        foreach (var shell in (string[])["bash", "zsh", "fish"]) {
            var script = SableProcess.Run(fixture.Root, fixture.Home, "completions", shell);
            Assert.Equal(0, script.ExitCode);
            foreach (var command in Command.All) {
                Assert.Contains(command.Name, script.Output, StringComparison.Ordinal);
            }

            // fish names a long option without its dashes.
            Assert.Contains(shell == "fish" ? "-l 'release'" : "--release", script.Output, StringComparison.Ordinal);
            Assert.Contains(shell == "fish" ? "-l 'offline'" : "--offline", script.Output, StringComparison.Ordinal);
        }

        Assert.Equal(2, SableProcess.Run(fixture.Root, fixture.Home, "completions", "powershell").ExitCode);

        // bash can read what it is given.
        if (File.Exists("/bin/bash")) {
            var path = fixture.Write("sable.bash", SableProcess.Run(fixture.Root, fixture.Home, "completions", "bash").Output);
            using var bash = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/bash", ["-n", path]) { RedirectStandardError = true })!;
            var errors = bash.StandardError.ReadToEnd();
            bash.WaitForExit();
            Assert.True(bash.ExitCode == 0, errors);
        }
    }

    [Fact]
    public void ToolchainsInstallListAndPin() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        using var fixture = new PackageFixture();

        // A stand-in toolchain: a 'sable' that says who it is and what it was asked.
        var build = Path.Combine(fixture.Root, "build-9.9.9");
        var fake = fixture.Write("build-9.9.9/sable", "#!/bin/sh\necho \"toolchain 9.9.9: $*\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var installed = SableProcess.Run(fixture.Root, fixture.Home, "toolchain", "install", "9.9.9", "--from", build);
        Assert.True(installed.ExitCode == 0, installed.Errors);
        Assert.True(File.Exists(Path.Combine(fixture.Home, "toolchains", "9.9.9", "sable")));
        Assert.Equal(1, SableProcess.Run(fixture.Root, fixture.Home, "toolchain", "install", "9.9.9", "--from", build).ExitCode);
        Assert.Equal(1, SableProcess.Run(fixture.Root, fixture.Home, "toolchain", "install", "1.2.3").ExitCode);

        var list = SableProcess.Run(fixture.Root, fixture.Home, "toolchain", "list");
        Assert.Contains($"{Toolchains.RunningVersion} (this one)", list.Output, StringComparison.Ordinal);
        Assert.Contains("9.9.9", list.Output, StringComparison.Ordinal);

        // A pinned package is built by its toolchain.
        Assert.Equal(0, SableProcess.Run(fixture.Root, fixture.Home, "new", "app").ExitCode);
        var app = Path.Combine(fixture.Root, "app");
        Assert.Equal(0, SableProcess.Run(Path.Combine(app, "src"), fixture.Home, "toolchain", "pin", "9.9.9").ExitCode);
        Assert.Equal("9.9.9\n", File.ReadAllText(Path.Combine(app, Toolchains.PinFile)));
        var redirected = SableProcess.Run(app, fixture.Home, "build", "--release");
        Assert.Equal(0, redirected.ExitCode);
        Assert.Equal("toolchain 9.9.9: build --release\n", redirected.Output);
        Assert.Contains("9.9.9 (pinned here)", SableProcess.Run(app, fixture.Home, "toolchain", "list").Output, StringComparison.Ordinal);

        // A pin on a toolchain that is not there stops the build, and says how to get it.
        Assert.Equal(0, SableProcess.Run(app, fixture.Home, "toolchain", "uninstall", "9.9.9").ExitCode);
        var missing = SableProcess.Run(app, fixture.Home, "check");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("pins toolchain 9.9.9, which is not installed", missing.Errors, StringComparison.Ordinal);

        Assert.Equal(0, SableProcess.Run(app, fixture.Home, "toolchain", "pin", Toolchains.RunningVersion).ExitCode);
        Assert.Equal(0, SableProcess.Run(app, fixture.Home, "check").ExitCode);
    }

    const string Header = """
                          #include <stdint.h>
                          #include <stddef.h>
                          #define SCALE 3
                          #define MASK 0x10
                          typedef struct { int32_t x, y; } Point;
                          struct Size { double w; double h; };
                          enum Colour { RED, GREEN = 5, BLUE };
                          typedef struct { int *items; } Holder;
                          int add(int a, int b);
                          double area(struct Size s);
                          Point scale_point(Point p, int64_t by);
                          size_t length(void);
                          const char *name(int id);
                          int sum(int count, ...);
                          int hold(Holder h);
                          _Bool is_red(enum Colour c);
                          int match(int in);
                          static inline int helper(int x) { return x; }
                          """;

    const string Implementation = """
                                  #include "shapes.h"
                                  int add(int a, int b) { return a + b; }
                                  double area(struct Size s) { return s.w * s.h; }
                                  Point scale_point(Point p, int64_t by) { Point r = { p.x * (int32_t)by, p.y * (int32_t)by }; return r; }
                                  size_t length(void) { return 7; }
                                  const char *name(int id) { return "x"; }
                                  int sum(int count, ...) { return 0; }
                                  int hold(Holder h) { return 0; }
                                  _Bool is_red(enum Colour c) { return c == RED; }
                                  int match(int in) { return in * 2; }
                                  """;

    [Fact]
    public void BindgenBindsWhatCanCrossAndSaysWhatCannot() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        var header = fixture.Write("shapes.h", Header);
        var bound = Bindgen.Generate(header, Bindgen.ModuleNameFor(header), []);

        Assert.Contains("module Shapes", bound, StringComparison.Ordinal);
        Assert.Contains("public struct Point {\n    public var x: I32\n    public var y: I32\n}", bound, StringComparison.Ordinal);
        Assert.Contains("public struct Size {", bound, StringComparison.Ordinal);
        Assert.Contains("public let GREEN: I32 = 5\npublic let BLUE: I32 = 6", bound, StringComparison.Ordinal);
        Assert.Contains("public let SCALE: Int = 3", bound, StringComparison.Ordinal);
        Assert.Contains("public let MASK: Int = 16", bound, StringComparison.Ordinal);
        Assert.Contains("@extern(\"c\")\npublic fn add(_ a: I32, _ b: I32) -> I32", bound, StringComparison.Ordinal);
        Assert.Contains("public fn scale_point(_ p: Point, _ by: I64) -> Point", bound, StringComparison.Ordinal);
        Assert.Contains("public fn length() -> UInt", bound, StringComparison.Ordinal);
        Assert.Contains("public fn is_red(_ c: I32) -> Bool", bound, StringComparison.Ordinal);
        // A Sable keyword as a C name is renamed, and the C name kept.
        Assert.Contains("@extern(\"c\", \"match\")\npublic fn match_(_ in_: I32) -> I32", bound, StringComparison.Ordinal);
        Assert.Contains("fn name: it returns a pointer", bound, StringComparison.Ordinal);
        Assert.Contains("fn sum: it takes variable arguments", bound, StringComparison.Ordinal);
        Assert.Contains("struct Holder: its field 'items' is a pointer", bound, StringComparison.Ordinal);
        Assert.Contains("fn hold: its parameter 'h' is struct Holder, which is not bound", bound, StringComparison.Ordinal);
        Assert.DoesNotContain("helper", bound, StringComparison.Ordinal);
        Assert.DoesNotContain("int8_t", bound, StringComparison.Ordinal);

        // What it wrote compiles, and calls the C it describes.
        fixture.Write("shapes.c", Implementation);
        var program = $$"""
                        module Main

                        import Shapes

                        fn main() {
                            print(add(2, 3))
                            print(area(Size(w: 2.0, h: 4.5)))
                            let p = scale_point(Point(x: 2, y: -3), I64(SCALE))
                            print("\(p.x) \(p.y)")
                            print(length())
                            print(is_red(RED))
                            print(match_(21))
                        }
                        """;
        var result = Pipeline.Compile(
            [SyntaxTree.Parse(new SourceText(program, "main.sb")), SyntaxTree.Parse(new SourceText(bound, "shapes.sb"))], null, false);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var directory = fixture.Root;
        var cObject = Path.Combine(directory, "shapes.o");
        using (var compile = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NativeBuild.CompilerPath, ["-c", Path.Combine(directory, "shapes.c"), "-o", cObject]) { RedirectStandardError = true })!) {
            var errors = compile.StandardError.ReadToEnd();
            compile.WaitForExit();
            Assert.True(compile.ExitCode == 0, errors);
        }

        var objectFile = Path.Combine(directory, "main.o");
        Assert.True(NativeObjects.CompileIr(result.LlvmIr!, objectFile, false, false).Succeeded);
        var runtime = Path.Combine(directory, "runtime.o");
        Assert.True(NativeObjects.CompileRuntime(runtime, false, false).Succeeded);
        var executable = Path.Combine(directory, "main");
        var linked = NativeObjects.Link([objectFile, runtime, cObject], executable);
        Assert.True(linked.Succeeded, linked.Output);
        Assert.Equal("5\n9.0\n6 -9\n7\ntrue\n42\n", PackageFixture.Execute(executable).Output);
    }

    [Fact]
    public void BindgenOnTheCommandLine() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Write("include/limits_here.h", "#define TOP 9\nint top(void);\n");
        fixture.Write("app.h", "#include \"limits_here.h\"\nint twice(int x);\n");
        var result = SableProcess.Run(fixture.Root, fixture.Home, "bindgen", "app.h", "--module", "AppC", "-o", "app.sb", "--", "-I", "include");
        Assert.True(result.ExitCode == 0, result.Errors);
        var bound = File.ReadAllText(Path.Combine(fixture.Root, "app.sb"));
        Assert.Contains("module AppC", bound, StringComparison.Ordinal);
        Assert.Contains("public fn twice(_ x: I32) -> I32", bound, StringComparison.Ordinal);
        // The included header's declarations are its own to bind.
        Assert.DoesNotContain("top", bound, StringComparison.Ordinal);

        var missing = SableProcess.Run(fixture.Root, fixture.Home, "bindgen", "app.h");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("clang could not read", missing.Errors, StringComparison.Ordinal);
    }
}
