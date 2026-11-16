namespace Sable.Syntax.Generator;

/// <summary>Locates the schema and its generated output from anywhere in the tree.</summary>
public static class RepositoryPaths
{
    /// <summary>The directory containing Sable.slnx.</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx")))
                dir = dir.Parent;

            return dir?.FullName
                ?? throw new InvalidOperationException(
                    $"could not find Sable.slnx above {AppContext.BaseDirectory}");
        }
    }

    public static string SchemaFile => Path.Combine(Root, "src", "Sable.Syntax", "Syntax.xml");
    public static string SyntaxKindFile => Path.Combine(Root, "src", "Sable.Syntax", "SyntaxKind.cs");
    public static string OutputDirectory => Path.Combine(Root, "src", "Sable.Syntax", "Generated");
}
