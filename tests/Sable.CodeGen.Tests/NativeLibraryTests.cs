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

    /// <summary>
    ///     A package's native library is compiled as a binary is: each package
    ///     a unit, cached, the member's own setting up every global — its
    ///     dependency's too — as the library loads. Unoptimised and with
    ///     ThinLTO (the release default), static and shared, a C program gets
    ///     what the whole library gives it; a body edit compiles one unit
    ///     again, and an unchanged build nothing.
    /// </summary>
    [Theory]
    [InlineData(LibraryKind.Static, false)]
    [InlineData(LibraryKind.Dynamic, false)]
    [InlineData(LibraryKind.Static, true)]
    [InlineData(LibraryKind.Dynamic, true)]
    public void APackagesLibraryIsCompiledUnitByUnit(LibraryKind kind, bool release) {
        if (!Programs.CanBuild || !NativeObjects.LinksLibrary(kind, NativeTarget.Host)) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("pool", "pool", "0.1.0", "", ("src/lib.sb", """
            module Pool

            public class Resource {
                public var name: String

                public init(name: String) {
                    self.name = name
                }

                deinit {
                    print("released \(name)")
                }
            }

            // Declared before what it reads: set up after it, as in a program.
            public let resource = Resource(name: label)
            let label = "the pool"
            """));
        var manifest = $"[lib]\nkind = \"{(kind == LibraryKind.Static ? "static" : "dynamic")}\"\n\n[dependencies]\npool = {{ path = \"../pool\" }}\n";
        fixture.Package("counter", "counter", "0.1.0", manifest, ("src/lib.sb", """
            module Counter

            import Pool

            var total: Int = 40

            @export("counter_add")
            public fn add(_ n: Int) -> Int {
                total += n
                return total
            }

            @export("counter_name_length")
            public fn nameLength() -> Int => resource.name.count
            """));

        var request = new BuildRequest(Profile: release ? "release" : "dev", Timings: true);
        var library = Path.Combine(fixture.Root, "counter", "target", release ? "release" : "dev", NativeBuild.LibraryFileName("counter", kind, NativeTarget.Host));
        var name = Path.GetFileName(library);
        Assert.Empty(PackageBuilder.Build(fixture.Load("counter"), fixture.Environment, request)!);
        Assert.Contains($"Built {name}: incremental, 3 unit(s) compiled, 0 reused", fixture.Errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(Expected, LinkAndRun(library, kind));

        fixture.Errors.GetStringBuilder().Clear();
        var pool = Path.Combine(fixture.Root, "pool", "src", "lib.sb");
        File.WriteAllText(pool, File.ReadAllText(pool).Replace("self.name = name", "self.name = name + \"!\"", StringComparison.Ordinal));
        Assert.Empty(PackageBuilder.Build(fixture.Load("counter"), fixture.Environment, request)!);
        Assert.Contains($"Built {name}: incremental, 1 unit(s) compiled, 2 reused", fixture.Errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("9\n42\n45\nreleased the pool!\n", LinkAndRun(library, kind));

        fixture.Errors.GetStringBuilder().Clear();
        Assert.Empty(PackageBuilder.Build(fixture.Load("counter"), fixture.Environment, request)!);
        Assert.Contains($"Built {name}: fresh", fixture.Errors.ToString(), StringComparison.Ordinal);
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
            return LinkAndRun(library, kind);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>A C program linked against <paramref name="library" />, beside it, and run with the leak check on: what it printed.</summary>
    static string LinkAndRun(string library, LibraryKind kind) {
        var directory = Path.GetDirectoryName(library)!;
        var host = Path.Combine(directory, "host.c");
        File.WriteAllText(host, Host);
        var executable = Platform.Executable(Path.Combine(directory, "host"));
        // Windows: the C runtime the library's objects were compiled for,
        // and a shared library's import library, which the link wrote
        // beside it under the static one's name. macOS and Linux: the
        // shared library is found beside the program. A static library's
        // objects call the DLL C runtime too, which clang's driver, linking
        // the static one (libcmt) by default, would conflict with: so the
        // program is linked with only the one, as Microsoft's /MD links it.
        List<string> arguments = [..NativeTarget.Host.ClangFlags, host];
        arguments.AddRange((kind, NativeTarget.Host.Platform) switch {
            (LibraryKind.Dynamic, NativePlatform.Windows) => [Path.ChangeExtension(library, ".lib")],
            (LibraryKind.Static, NativePlatform.Windows) => [library, "-Wl,/NODEFAULTLIB:libcmt"],
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
    }

    static (int ExitCode, string Output) Run(string program, IReadOnlyList<string> arguments, string? checkLeaks) {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
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
