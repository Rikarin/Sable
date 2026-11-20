using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Build scripts (docs/05 §5.6, #202): <c>build.sb</c> runs before its
///     package compiles, in the interpreter, allowed only what its manifest
///     declares; what it writes under <c>$OUT_DIR</c> is compiled with the
///     package, and what it prints reaches the link.
/// </summary>
public sealed class BuildScriptTests {
    const string Generator = """
                             module Build

                             import Sable.IO

                             fn main() {
                                 let outDir = Environment.variable("OUT_DIR")!
                                 let table = try! File.read("data/table.txt")
                                 var code = "module Generated\n\n"
                                 for line in table.split('\n') {
                                     let parts = line.split('=')
                                     if parts.count == 2 {
                                         code = code + "public fn \(parts[0])() -> Int {\n    return \(parts[1])\n}\n\n"
                                     }
                                 }
                                 try! File.write(Path.join(outDir, "generated.sb"), code)
                             }
                             """;

    const string Main = "module Main\n\nimport Generated\n\nfn main() {\n    print(answer() + seven())\n}\n";

    static string Package(PackageFixture fixture, string build, string script, params (string Path, string Text)[] files) {
        fixture.Package("gen", "gen", "0.1.0", $"[build]\nscript = \"build.sb\"\n{build}", [("build.sb", script), .. files]);
        return Path.Combine(fixture.Root, "gen");
    }

    static string Generated(PackageFixture fixture) => Package(fixture, "allow-read = [\"data/\"]\n", Generator,
        ("data/table.txt", "answer=42\nseven=7\n"), ("src/main.sb", Main));

    [Fact]
    public void WhatTheScriptWritesIsCompiledWithThePackage() {
        using var fixture = new PackageFixture();
        Generated(fixture);
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("Running build script of gen v0.1.0", fixture.Errors.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "gen", "target", "dev", "build", "gen-0.1.0", "out", "generated.sb")));

        if (Programs.CanBuild) {
            var binary = Assert.Single(PackageBuilder.Build(fixture.Load("gen"), fixture.Environment, new())!);
            Assert.Equal("49\n", PackageFixture.Execute(binary.Path).Output);
        }
    }

    [Fact]
    public void ItRunsAgainOnlyWhenWhatItReadChanged() {
        using var fixture = new PackageFixture();
        var directory = Generated(fixture);
        var errors = new StringWriter();
        var environment = fixture.Environment with { Error = errors };
        int Runs() {
            errors.GetStringBuilder().Clear();
            Assert.Equal(0, PackageBuilder.Check(fixture.Load("gen"), environment, new()));
            return errors.ToString().Split('\n').Count(static l => l.Contains("Running build script", StringComparison.Ordinal));
        }

        Assert.Equal(1, Runs());
        Assert.Equal(0, Runs());

        // A file it did not read is not its business.
        File.WriteAllText(Path.Combine(directory, "data", "other.txt"), "x");
        Assert.Equal(0, Runs());

        // The file it read is.
        File.WriteAllText(Path.Combine(directory, "data", "table.txt"), "answer=40\nseven=7\n");
        Assert.Equal(1, Runs());
        Assert.Contains("return 40", File.ReadAllText(Path.Combine(directory, "target", "dev", "build", "gen-0.1.0", "out", "generated.sb")), StringComparison.Ordinal);

        // So are the script and the profile.
        File.AppendAllText(Path.Combine(directory, "build.sb"), "\n// changed\n");
        Assert.Equal(1, Runs());
        errors.GetStringBuilder().Clear();
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("gen"), environment, new(Profile: "release")));
        Assert.Contains("Running build script", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AScriptIsCompiledWithItsBuildDependencies() {
        using var fixture = new PackageFixture();
        fixture.Package("helpers", "helpers", "1.0.0", "",
            ("src/lib.sb", "module Helpers\n\npublic fn constant(_ name: String, _ value: Int) -> String {\n    return \"public fn \\(name)() -> Int {\\n    return \\(value)\\n}\\n\"\n}\n"));
        const string script = """
                              module Build

                              import Helpers
                              import Sable.IO

                              fn main() {
                                  let outDir = Environment.variable("OUT_DIR")!
                                  try! File.write(Path.join(outDir, "generated.sb"), "module Generated\n\n" + constant("answer", 42) + constant("seven", 7))
                              }
                              """;
        var directory = Package(fixture, "[build-dependencies]\nhelpers = { path = \"../helpers\" }\n", script, ("src/main.sb", Main));

        var errors = new StringWriter();
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("gen"), fixture.Environment with { Error = errors }, new()));
        Assert.Contains("return 42", File.ReadAllText(Path.Combine(directory, "target", "dev", "build", "gen-0.1.0", "out", "generated.sb")), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", errors.ToString(), StringComparison.Ordinal);

        // Locked with the rest, and a build-dependency only: not part of the program.
        var graph = PackageGraph.Resolve(fixture.Load("gen"), fixture.Environment);
        Assert.NotNull(graph.Lock.Find("helpers"));
        Assert.Equal(["helpers"], graph.Packages["gen"].BuildDependencies);
        Assert.DoesNotContain(graph.Closure("gen"), static p => p.Name == "helpers");

        // Changed, the script runs again.
        File.WriteAllText(Path.Combine(fixture.Root, "helpers", "src", "lib.sb"),
            "module Helpers\n\npublic fn constant(_ name: String, _ value: Int) -> String {\n    return \"public fn \\(name)() -> Int {\\n    return \\(value + 1)\\n}\\n\"\n}\n");
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("gen"), fixture.Environment with { Error = errors }, new()));
        Assert.Contains("return 43", File.ReadAllText(Path.Combine(directory, "target", "dev", "build", "gen-0.1.0", "out", "generated.sb")), StringComparison.Ordinal);
        if (Programs.CanBuild) {
            var binary = Assert.Single(PackageBuilder.Build(fixture.Load("gen"), fixture.Environment, new())!);
            Assert.Equal("51\n", PackageFixture.Execute(binary.Path).Output);
        }
    }

    [Fact]
    public void ReadingWhatItDidNotDeclareStopsTheBuild() {
        using var fixture = new PackageFixture();
        Package(fixture, "", Generator, ("data/table.txt", "answer=1\n"), ("src/main.sb", Main));
        var error = Assert.Throws<PackageException>(() => PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("may not read 'data/table.txt'", error.Message, StringComparison.Ordinal);
        Assert.Contains("allowRead", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingOutsideWhatItDeclaredStopsTheBuild() {
        using var fixture = new PackageFixture();
        const string writer = """
                              module Build

                              import Sable.IO

                              fn main() {
                                  try! File.write("src/sneaky.sb", "module Sneaky\n")
                              }
                              """;
        Package(fixture, "allow-read = [\"src/\"]\n", writer, ("src/lib.sb", "module Gen\n"));
        var error = Assert.Throws<PackageException>(() => PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("may not write 'src/sneaky.sb'", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "gen", "src", "sneaky.sb")));

        // Declared, it may.
        Package(fixture, "allow-write = [\"src/\"]\n", writer, ("src/lib.sb", "module Gen\n"));
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "gen", "src", "sneaky.sb")));
    }

    [Fact]
    public void ALinkCannotLeadOutOfWhatItMayRead() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Write("secret.txt", "answer=1\n");
        var directory = Package(fixture, "allow-read = [\"data/\"]\n", Generator, ("src/main.sb", Main));
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        File.CreateSymbolicLink(Path.Combine(directory, "data", "table.txt"), Path.Combine(fixture.Root, "secret.txt"));
        var error = Assert.Throws<PackageException>(() => PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("may not read 'data/table.txt'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItRunsOnlyTheProgramsItDeclaredAndSeesOnlyTheBuildsVariables() {
        using var fixture = new PackageFixture();
        const string runner = """
                              module Build

                              import Sable.IO
                              import Sable.Process

                              fn main() {
                                  let home = Environment.variable("HOME")
                                  print("sable:warning=home \(home == none ? "hidden" : "seen")")
                                  print("sable:warning=profile \(Environment.variable("SABLE_PROFILE")!) features \(Environment.variable("SABLE_FEATURES")!)")
                                  let echoed = try! Process.run("echo", ["from", "echo"])
                                  print("sable:warning=\(echoed.output.trimmed())")
                                  if Process.changeDirectory("src") {
                                      print("sable:warning=now in \(Path.fileName(Process.currentDirectory()))")
                                  }
                              }
                              """;
        Package(fixture, "allow-exec = [\"echo\"]\nallow-read = [\"src/\"]\n[features]\ndefault = [\"fast\"]\nfast = []\n", runner, ("src/lib.sb", "module Gen\n"));
        var before = Directory.GetCurrentDirectory();
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        var said = fixture.Errors.ToString();
        Assert.Contains("warning: gen: home hidden", said, StringComparison.Ordinal);
        Assert.Contains("warning: gen: profile dev features fast", said, StringComparison.Ordinal);
        Assert.Contains("warning: gen: from echo", said, StringComparison.Ordinal);
        Assert.Contains("warning: gen: now in src", said, StringComparison.Ordinal);
        // Its working directory is its own, not the build's.
        Assert.Equal(before, Directory.GetCurrentDirectory());

        Package(fixture, "", runner, ("src/lib.sb", "module Gen\n"));
        var error = Assert.Throws<PackageException>(() => PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("may not run 'echo': it runs only nothing", error.Message, StringComparison.Ordinal);
        Assert.Contains("allowExec", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingScriptStopsTheBuildWithWhatItSaid() {
        using var fixture = new PackageFixture();
        const string failing = """
                               module Build

                               import Sable.IO

                               fn main() {
                                   Console.writeErrorLine("no compiler found")
                                   let missing: Int? = none
                                   print(missing!)
                               }
                               """;
        Package(fixture, "", failing, ("src/lib.sb", "module Gen\n"));
        var error = Assert.Throws<PackageException>(() => PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("the build script of gen", error.Message, StringComparison.Ordinal);
        Assert.Contains("no compiler found", error.Message, StringComparison.Ordinal);

        Package(fixture, "", "module Build\n\nfn main() {\n    let x: Int = \"no\"\n}\n", ("src/lib.sb", "module Gen\n"));
        error = Assert.Throws<PackageException>(() => PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("does not compile", error.Message, StringComparison.Ordinal);
        Assert.Contains("S3004", error.Message, StringComparison.Ordinal);

        Package(fixture, "", "module Build\n\nfn main() {\n    print(\"sable:rustc-link-lib=z\")\n}\n", ("src/lib.sb", "module Gen\n"));
        error = Assert.Throws<PackageException>(() => PackageBuilder.Check(fixture.Load("gen"), fixture.Environment, new()));
        Assert.Contains("which the build does not understand", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANativeLibraryItBuildsIsLinked() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        var compiler = NativeBuild.CompilerPath;
        // LLVM's archiver on Windows, where a library is 'triple.lib'.
        var (archiver, library) = OperatingSystem.IsWindows() ? ("llvm-ar", "triple.lib") : ("ar", "libtriple.a");
        var script = $$"""
                       module Build

                       import Sable.IO
                       import Sable.Process

                       fn main() {
                           let outDir = Environment.variable("OUT_DIR")!
                           let object = Path.join(outDir, "triple.o")
                           let compiled = try! Process.run("{{compiler}}", ["-c", "native/triple.c", "-o", object])
                           if !compiled.succeeded {
                               Console.writeErrorLine(compiled.errors)
                               let failed: Int? = none
                               print(failed!)
                           }
                           let archived = try! Process.run("{{archiver}}", ["rcs", Path.join(outDir, "{{library}}"), object])
                           print(archived.succeeded ? "" : "sable:warning=ar failed")
                           print("sable:link-search=\(outDir)")
                           print("sable:link-lib=triple")
                           print("sable:rerun-if-changed=native/triple.c")
                       }
                       """;
        var directory = Package(fixture, $"allow-exec = [\"{compiler}\", \"{archiver}\"]\n", script,
            ("native/triple.c", "int triple(int x) { return 3 * x; }\n"),
            ("src/lib.sb", "module Gen\n\n@extern(\"c\")\npublic fn triple(_ x: I32) -> I32\n"),
            ("src/main.sb", "module Main\n\nimport Gen\n\nfn main() {\n    print(triple(14))\n}\n"),
            ("tests/triple_tests.sb", "module GenTests\n\nimport Gen\nimport Sable.Testing\n\n@test\nfn triples() {\n    assert(triple(2) == 6)\n}\n"));

        var binary = Assert.Single(PackageBuilder.Build(fixture.Load("gen"), fixture.Environment, new())!);
        Assert.Equal("42\n", PackageFixture.Execute(binary.Path).Output);

        // The test harness is linked with it too.
        var tested = SableProcess.Run(directory, fixture.Home, "test");
        Assert.True(tested.ExitCode == 0, tested.Output + tested.Errors);

        // The C it named is an input: changed, the script runs, and the program is linked again.
        File.WriteAllText(Path.Combine(directory, "native", "triple.c"), "int triple(int x) { return 3 * x + 1; }\n");
        binary = Assert.Single(PackageBuilder.Build(fixture.Load("gen"), fixture.Environment, new())!);
        Assert.Equal("43\n", PackageFixture.Execute(binary.Path).Output);
    }
}
