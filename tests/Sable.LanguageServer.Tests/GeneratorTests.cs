using Sable.Driver.Packages;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     Source generators in the editor (docs/08 §8.7): a package is compiled
///     with what its generators write for its documents as they are, and a
///     generator's error is reported on the attribute.
/// </summary>
public sealed class GeneratorTests : IDisposable {
    readonly string _top = Directory.CreateTempSubdirectory("sable-lsp-generators-").FullName;

    string Root => Path.Combine(_top, "root");

    public void Dispose() {
        try {
            Directory.Delete(_top, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    string Write(string relative, string text) {
        var path = Path.Combine(_top, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    const string Generator = """
                             module Greet

                             import Sable.Generators

                             @generator("greet")
                             fn greet(_ d: Declaration, into output: inout Output) {
                                 if d.keyword != "struct" {
                                     output.error("only a struct can be greeted")
                                     return
                                 }
                                 output.add(file: "\(d.name).greet.sb", "extension \(d.name) {\n    fn greeting() -> String => \"hello from \(d.name)\"\n}\n")
                             }
                             """;

    const string Main = """
                        module App

                        @greet
                        struct Person {
                            var name: String
                        }

                        fn main() {
                            print(Person(name: "a").greeting())
                        }
                        """;

    TestClient Client() {
        var environment = PackageEnvironment.At(Path.Combine(_top, "home"));
        return new TestClient(projects: root => LanguageProjects.For(root, environment));
    }

    string WritePackages() {
        Write("greet/sable.toml", "[package]\nname = \"greet\"\nversion = \"1.0.0\"\n");
        Write("greet/generators/greet.sb", Generator);
        Write("root/app/sable.toml", "[package]\nname = \"app\"\nversion = \"0.1.0\"\n\n[dependencies]\ngreet = { path = \"../../greet\" }\n");
        return Write("root/app/src/main.sb", Main);
    }

    [Fact]
    public async Task AGeneratedMemberIsKnownAndOpensItsFile() {
        var main = WritePackages();
        var uri = Workspace.UriOf(main);
        await using var client = Client();
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, Main);

        var diagnostics = await client.DiagnosticsAsync(uri);
        Assert.True(diagnostics.GetArrayLength() == 0, diagnostics.ToString());

        var definition = await client.AtAsync("textDocument/definition", uri, Main, Main.IndexOf("greeting", StringComparison.Ordinal));
        Assert.Equal(Workspace.UriOf(Path.Combine(Root, "app", "target", "generated", "app", "greet", "Person.greet.sb")), definition.GetString("uri"));
    }

    [Fact]
    public async Task AnEditIsGeneratedForAsItIsTyped() {
        var main = WritePackages();
        var uri = Workspace.UriOf(main);
        await using var client = Client();
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, Main);
        Assert.Equal(0, (await client.DiagnosticsAsync(uri)).GetArrayLength());

        // A struct becomes a union: the generator says so, at the attribute.
        var start = Main.IndexOf("struct Person {", StringComparison.Ordinal);
        await client.ChangeAsync(uri, 2, Main, start, start + "struct".Length, "union");
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.EnumerateArray().Any(static e => e.GetString("code") == "S3232"));
        var error = diagnostics.EnumerateArray().First(static e => e.GetString("code") == "S3232");
        Assert.Equal("greet: only a struct can be greeted", error.GetString("message"));
        var at = error.Get("range")!.Value.Get("start")!.Value;
        Assert.Equal((2, 0), (at.Get("line")!.Value.GetInt32(), at.Get("character")!.Value.GetInt32()));
    }

    [Fact]
    public void TheGeneratorsAreAProjectOfTheirOwn() {
        WritePackages();
        Write("root/app/generators/local.sb", "module Local\n\nimport Sable.Generators\n");
        var projects = LanguageProjects.For(Root, PackageEnvironment.At(Path.Combine(_top, "home")));
        Assert.Equal(["app", "app generators"], projects.Select(static p => p.Name).Order());
        Assert.NotNull(projects.Single(static p => p.Name == "app").Generate);
    }
}
