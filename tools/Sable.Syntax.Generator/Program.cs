using Sable.Syntax.Generator;

var check = args.Contains("--check", StringComparer.Ordinal);

var files = SyntaxGenerator.Generate(
    File.ReadAllText(RepositoryPaths.SchemaFile),
    File.ReadAllText(RepositoryPaths.SyntaxKindFile));

Directory.CreateDirectory(RepositoryPaths.OutputDirectory);

var stale = new List<string>();
foreach (var (name, content) in files)
{
    var path = Path.Combine(RepositoryPaths.OutputDirectory, name);
    var existing = File.Exists(path) ? File.ReadAllText(path) : null;

    if (existing == content)
    {
        Console.WriteLine($"  unchanged  {name}");
        continue;
    }

    if (check) { stale.Add(name); continue; }

    File.WriteAllText(path, content);
    Console.WriteLine($"  written    {name}  ({content.Length:N0} chars)");
}

if (stale.Count > 0)
{
    Console.Error.WriteLine(
        "Generated syntax files are out of date: " + string.Join(", ", stale));
    Console.Error.WriteLine("Run: dotnet run --project tools/Sable.Syntax.Generator");
    return 1;
}

return 0;
