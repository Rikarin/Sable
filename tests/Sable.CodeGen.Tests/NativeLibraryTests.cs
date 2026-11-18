using System.Diagnostics;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A Sable program built as a library C links (docs/05 §5.2): no entry
///     point, its <c>@export</c> functions what C calls, its globals set up as
///     it is loaded and destroyed as the process exits — as a static library
///     and as a shared one.
/// </summary>
/// <remarks>Like every native test, these pass vacuously where there is no C compiler.</remarks>
public sealed class NativeLibraryTests {
    const string Library = """
                           module Counter

                           class Resource {
                               var name: String

                               init(name: String) {
                                   self.name = name
                               }

                               deinit {
                                   print("released \(name)")
                               }
                           }

                           var total: Int = 40
                           // Declared before what it reads: set up after it, as in a program.
                           let resource = Resource(name: label)
                           let label = "the pool"

                           @export("counter_add")
                           public fn add(_ n: Int) -> Int {
                               total += n
                               return total
                           }

                           @export("counter_name_length")
                           public fn nameLength() -> Int => resource.name.count

                           // A library starts nowhere: this is an ordinary function, never called.
                           fn main() {
                               print("main ran")
                           }
                           """;

    const string Host = """
                        #include <stdio.h>
                        #include <stdint.h>
                        int64_t counter_add(int64_t);
                        int64_t counter_name_length(void);
                        int main(void) {
                            printf("%lld\n", (long long)counter_name_length());
                            printf("%lld\n", (long long)counter_add(2));
                            printf("%lld\n", (long long)counter_add(3));
                            fflush(stdout);
                            return 0;
                        }
                        """;

    // The globals were initialised before C first called in, and the one
    // holding an object was destroyed as the process exited — before the
    // runtime's leak check, which finds nothing left.
    const string Expected = "8\n42\n45\nreleased the pool\n";

    [Fact]
    public void AStaticLibraryLinksIntoACProgram() {
        if (!Programs.CanBuild) {
            return;
        }

        Assert.Equal(Expected, BuildAndRun(LibraryKind.Static));
    }

    [Fact]
    public void ADynamicLibraryIsLoadedByACProgram() {
        if (!Programs.CanBuild) {
            return;
        }

        Assert.Equal(Expected, BuildAndRun(LibraryKind.Dynamic));
    }

    [Fact]
    public void ALibraryNeedsNoEntryPoint() {
        var program = Pipeline.Compile([SyntaxTree.Parse(new SourceText("module M\n\n@export(\"m_one\")\npublic fn one() -> Int => 1\n", "m.sb"))]);
        Assert.Contains(program.Errors, static e => e.Contains("no entry point", StringComparison.Ordinal));

        var library = Pipeline.Compile([SyntaxTree.Parse(new SourceText("module M\n\n@export(\"m_one\")\npublic fn one() -> Int => 1\n", "m.sb"))], library: true);
        Assert.True(library.Succeeded, string.Join("\n", library.Errors));
        Assert.DoesNotContain("@main(", library.LlvmIr!, StringComparison.Ordinal);
        Assert.Contains("@llvm.global_ctors", library.LlvmIr!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LibraryKind.Static, "x86_64-unknown-linux-gnu", "libcounter.a")]
    [InlineData(LibraryKind.Dynamic, "aarch64-unknown-linux-gnu", "libcounter.so")]
    [InlineData(LibraryKind.Static, "x86_64-pc-windows-msvc", "counter.lib")]
    [InlineData(LibraryKind.Dynamic, "x86_64-pc-windows-msvc", "counter.dll")]
    [InlineData(LibraryKind.Dynamic, "arm64-apple-macosx", "libcounter.dylib")]
    public void ALibraryIsNamedAsItsTargetsLinkersLookForIt(LibraryKind kind, string triple, string name) =>
        Assert.Equal(name, NativeBuild.LibraryFileName("counter", kind, NativeTarget.Parse(triple, out _)!));

    /// <summary>
    ///     A package whose manifest asks for a native library gets one in
    ///     <c>target/&lt;profile&gt;/</c>, named for it, from its library and its
    ///     dependency's; a <c>package.sb</c> asks with <c>native:</c>.
    /// </summary>
    [Fact]
    public void APackageBuildsItsLibraryAsTheManifestAsks() {
        using var fixture = new PackageFixture();
        fixture.Package("base", "base", "0.1.0", "", ("src/lib.sb", "module Base\n\npublic fn twice(_ n: Int) -> Int {\n    return n * 2\n}\n"));
        fixture.Package("my-lib", "my-lib", "0.1.0", "[lib]\nkind = \"static\"\n\n[dependencies]\nbase = { path = \"../base\" }\n",
            ("src/lib.sb", "module MyLib\n\nimport Base\n\n@export(\"my_twice\")\npublic fn myTwice(_ n: Int) -> Int {\n    return twice(n)\n}\n"));

        var manifest = fixture.Load("my-lib").RootManifest;
        Assert.Equal(LibraryKind.Static, manifest.LibKind);
        if (!Programs.CanBuild) {
            return;
        }

        Assert.Empty(PackageBuilder.Build(fixture.Load("my-lib"), fixture.Environment, new BuildRequest())!);
        var library = Path.Combine(fixture.Root, "my-lib", "target", "dev", NativeBuild.LibraryFileName("my_lib", LibraryKind.Static, NativeTarget.Host));
        Assert.True(File.Exists(library), fixture.Errors.ToString());
    }

    [Fact]
    public void AManifestsLibraryKindIsStaticOrDynamic() {
        using var fixture = new PackageFixture();
        fixture.Package("p", "p", "0.1.0", "[lib]\nkind = \"shared\"\n", ("src/lib.sb", "module P\n"));
        var error = Assert.Single(Assert.Throws<ManifestException>(() => fixture.Load("p")).Errors);
        Assert.Equal("lib.kind", error.Key);

        fixture.Write("q/package.sb", """
                                      import Sable.Build

                                      let manifest = Package {
                                          name: "q",
                                          version: "0.1.0",
                                          targets: [.library(path: "src/lib.sb", native: .dynamicLibrary)],
                                      }
                                      """);
        fixture.Write("q/src/lib.sb", "module Q\n");
        Assert.Equal(LibraryKind.Dynamic, ManifestFiles.Load(Path.Combine(fixture.Root, "q")).LibKind);
    }

    /// <summary>The library built, a C program linked against it and run with the leak check on: what it printed.</summary>
    static string BuildAndRun(LibraryKind kind) {
        var compiled = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Library, "counter.sb"))], library: true);
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors));

        var directory = Directory.CreateTempSubdirectory("sable-native-library-");
        try {
            var library = Path.Combine(directory.FullName, NativeBuild.LibraryFileName("counter", kind, NativeTarget.Host));
            var built = NativeBuild.BuildLibrary(compiled.LlvmIr!, library, kind, NativeOptions.Of(optimise: true, debug: false), Path.Combine(directory.FullName, "runtime"));
            Assert.True(built.Succeeded, built.Output);

            var host = Path.Combine(directory.FullName, "host.c");
            File.WriteAllText(host, Host);
            var executable = Platform.Executable(Path.Combine(directory.FullName, "host"));
            // Windows: the C runtime the library's objects were compiled for,
            // and a shared library's import library, which the link wrote
            // beside it under the static one's name. macOS and Linux: the
            // shared library is found beside the program.
            List<string> arguments = [..NativeTarget.Host.ClangFlags, host];
            arguments.AddRange((kind, NativeTarget.Host.Platform) switch {
                (LibraryKind.Dynamic, NativePlatform.Windows) => [Path.Combine(directory.FullName, "counter.lib")],
                (LibraryKind.Dynamic, NativePlatform.MacOS) => [library, "-Wl,-rpath,@executable_path"],
                (LibraryKind.Dynamic, _) => [library, "-Wl,-rpath,$ORIGIN"],
                (_, NativePlatform.Linux) => [library, "-lm", "-lpthread"],
                _ => [library],
            });
            arguments.AddRange(["-o", executable]);
            var (linked, linkOutput) = Run(NativeBuild.CompilerPath, arguments, null);
            Assert.True(linked == 0, linkOutput);

            var (exitCode, output) = Run(executable, [], "1");
            Assert.True(exitCode == 0, output);
            return output.Replace("\r\n", "\n", StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    static (int ExitCode, string Output) Run(string program, IReadOnlyList<string> arguments, string? checkLeaks) {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        if (checkLeaks is not null) {
            start.Environment["SABLE_CHECK_LEAKS"] = checkLeaks;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr);
    }
}
