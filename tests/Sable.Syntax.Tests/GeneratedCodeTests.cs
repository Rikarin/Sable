using Sable.Syntax.Generator;

namespace Sable.Syntax.Tests;

/// <summary>
/// Guards against drift between Syntax.xml and the generated node classes.
/// The generated files are committed so that a plain `dotnet build` works
/// without running the generator first; this test is what keeps them honest.
/// </summary>
public sealed class GeneratedCodeTests
{
    [Fact]
    public void GeneratedFilesMatchTheSchema()
    {
        var files = SyntaxGenerator.Generate(
            File.ReadAllText(RepositoryPaths.SchemaFile),
            File.ReadAllText(RepositoryPaths.SyntaxKindFile));

        foreach (var (name, expected) in files)
        {
            var path = Path.Combine(RepositoryPaths.OutputDirectory, name);
            Assert.True(File.Exists(path), $"{name} has not been generated");
            Assert.True(expected == File.ReadAllText(path),
                $"{name} is out of date.\nRun: dotnet run --project tools/Sable.Syntax.Generator");
        }
    }

    [Fact]
    public void SchemaKindsAllExistInSyntaxKind()
    {
        // Generate() throws if the schema names a kind the enum does not have.
        var exception = Record.Exception(() => SyntaxGenerator.Generate(
            File.ReadAllText(RepositoryPaths.SchemaFile),
            File.ReadAllText(RepositoryPaths.SyntaxKindFile)));

        Assert.Null(exception);
    }

    [Fact]
    public void AnUnknownKindIsRejected()
    {
        const string schema = """
            <Syntax>
              <Node Name="BogusSyntax" Base="SyntaxNode" Kind="NoSuchKind">
                <Field Name="Token" Type="Token" />
              </Node>
            </Syntax>
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => SyntaxGenerator.Generate(schema, "    None = 0,\n"));
        Assert.Contains("NoSuchKind", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFieldTypeIsRejected()
    {
        const string schema = """
            <Syntax>
              <Node Name="BogusSyntax" Base="SyntaxNode" Kind="None">
                <Field Name="Child" Type="NoSuchSyntax" />
              </Node>
            </Syntax>
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => SyntaxGenerator.Generate(schema, "    None = 0,\n"));
        Assert.Contains("NoSuchSyntax", exception.Message, StringComparison.Ordinal);
    }
}
