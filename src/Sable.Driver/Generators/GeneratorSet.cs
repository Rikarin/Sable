using System.Security.Cryptography;
using System.Text;
using Sable.Syntax;

namespace Sable.Driver.Generators;

/// <summary>What a generator is given: one declaration a run, or the package's all at once (docs/08 §8.1).</summary>
public enum GeneratorKind {
    Declaration,
    Package,
}

/// <summary>A function marked <c>@generator("name")</c>, and where it is.</summary>
public sealed record GeneratorFunction(string Name, string Module, string Function, SyntaxTree Tree, FunctionDeclarationSyntax Declaration, AttributeSyntax Attribute) {
    /// <summary>
    ///     What it is given, by its first parameter's type as written; the
    ///     signature is checked against the symbols when the program compiles.
    /// </summary>
    public GeneratorKind Kind =>
        Declaration.Parameters.Parameters is [var first, ..] && first.Type.ToString().Trim() is "Package" or "Sable.Generators.Package"
            ? GeneratorKind.Package
            : GeneratorKind.Declaration;

    /// <summary><c>always: true</c>: a package generator that runs even for a package with no declaration for it.</summary>
    public bool Always => GeneratorSet.Argument(Attribute, "always") is LiteralExpressionSyntax { Token.Kind: SyntaxKind.TrueKeyword };

    /// <summary>The attributes it reads on members and parameters: its <c>members:</c>.</summary>
    public IReadOnlyList<string> Members => GeneratorSet.Names(Attribute, "members");

    /// <summary>
    ///     The attributes of the declarations a package generator is given
    ///     beside its own, and runs for: its <c>with:</c>.
    /// </summary>
    public IReadOnlyList<string> With => GeneratorSet.Names(Attribute, "with");
}

/// <summary>
///     A package's generator program (docs/08 §8.1): the files under its
///     <c>generators/</c> directory, but its <c>tests/</c>, and the
///     generators they declare.
/// </summary>
public sealed class GeneratorSet {
    GeneratorSet(string package, IReadOnlyList<SyntaxTree> files) {
        Package = package;
        Files = files;
        var generators = new List<GeneratorFunction>();
        foreach (var tree in files) {
            if (tree.Root is not CompilationUnitSyntax unit) {
                continue;
            }

            var module = unit.Module is { } declaration ? declaration.Name.ToString().Trim() : "";
            foreach (var function in unit.Members.OfType<FunctionDeclarationSyntax>()) {
                foreach (var attribute in function.Attributes) {
                    if (attribute.Name.ToString().Trim() == "generator") {
                        generators.Add(new(GeneratorName(attribute) ?? "", module, function.Identifier.Text, tree, function, attribute));
                    }
                }
            }
        }

        Generators = generators;
        var key = new StringBuilder()
            .Append(typeof(GeneratorSet).Assembly.ManifestModule.ModuleVersionId).Append('\n')
            .Append(Library.Source(SourceGenerators.LibraryModule)).Append('\n');
        foreach (var tree in files) {
            key.Append(tree.Text.FileName).Append('\n').Append(tree.Text.Text).Append('\n');
        }

        Key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));
    }

    /// <summary>Whether it is the standard library's own (<see cref="LibraryGenerators" />), and so the compiler's.</summary>
    public bool IsLibrary { get; init; }

    /// <summary>The package the generators are in.</summary>
    public string Package { get; }

    /// <summary>The generator program's files.</summary>
    public IReadOnlyList<SyntaxTree> Files { get; }

    /// <summary>The functions marked <c>@generator</c>, in the order the files declare them.</summary>
    public IReadOnlyList<GeneratorFunction> Generators { get; }

    /// <summary>The attributes the generators answer to, and read on members: what a file that can see them may write.</summary>
    public (IReadOnlyList<string> Types, IReadOnlyList<string> Members) Attributes =>
        ([..Generators.SelectMany(static g => g.With.Prepend(g.Name)).Where(static n => n.Length > 0).Distinct()],
            [..Generators.SelectMany(static g => g.Members)]);

    /// <summary>What a cache of anything the program computes is keyed by: the toolchain, the library and its files.</summary>
    public string Key { get; }

    /// <summary>The directory of a package's generator snapshot tests (docs/08 §8.8).</summary>
    public static string TestsDirectory(string packageDirectory) =>
        Path.Combine(packageDirectory, SourceGenerators.DirectoryName, "tests");

    /// <summary>
    ///     The generators of the package in <paramref name="directory" />, each
    ///     file named as <paramref name="nameOf" /> says; null when it has no
    ///     <c>generators/</c> directory, or nothing in it.
    /// </summary>
    public static GeneratorSet? Load(string package, string directory, Func<string, string> nameOf) {
        var generators = Path.Combine(directory, SourceGenerators.DirectoryName);
        if (!Directory.Exists(generators)) {
            return null;
        }

        var tests = TestsDirectory(directory) + Path.DirectorySeparatorChar;
        var files = Directory.GetFiles(generators, "*.sb", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(f => !f.StartsWith(tests, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => (nameOf(f), File.ReadAllText(f)))
            .ToList();
        return files.Count == 0 ? null : FromSources(package, files);
    }

    /// <summary>A generator program from its files' names and text.</summary>
    public static GeneratorSet FromSources(string package, IReadOnlyList<(string Name, string Text)> files) =>
        new(package, [..files.Select(static f => SyntaxTree.Parse(new SourceText(f.Text, f.Name)))]);

    /// <summary>One of the standard library's, by the module it is part of.</summary>
    public static GeneratorSet OfLibrary(string module, IReadOnlyList<(string Name, string Text)> files) =>
        new(module, [..files.Select(static f => SyntaxTree.Parse(new SourceText(f.Text, f.Name)))]) { IsLibrary = true };

    /// <summary>A generator program from trees already parsed: the editor's documents, so that what is said about them is said about those.</summary>
    public static GeneratorSet FromTrees(string package, IReadOnlyList<SyntaxTree> files) => new(package, files);

    /// <summary>
    ///     The name in <c>@generator("name")</c>, if it is written as a string
    ///     literal, with nothing else but <c>members:</c>, <c>with:</c> and <c>always:</c>.
    /// </summary>
    static string? GeneratorName(AttributeSyntax attribute) {
        if (attribute.ArgumentList is not { Arguments: [{ NameToken.IsNone: true, Expression: var first }, ..] arguments }
            || StringValue(first) is not { } name) {
            return null;
        }

        var rest = arguments.Skip(1).ToList();
        var valid = rest.Count == rest.Select(static a => a.NameToken.Text).Distinct().Count()
            && rest.All(static a => a.NameToken.Text switch {
                "members" or "with" => Names(a.Expression) is not null,
                "always" => a.Expression is LiteralExpressionSyntax { Token.Kind: SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword },
                _ => false,
            });
        return valid ? name : null;
    }

    internal static ExpressionSyntax? Argument(AttributeSyntax attribute, string label) =>
        attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameToken.Text == label)?.Expression;

    /// <summary>The names in <c>members: ["rename", "skip"]</c>, or another argument that lists attributes.</summary>
    internal static IReadOnlyList<string> Names(AttributeSyntax attribute, string label) =>
        Argument(attribute, label) is { } names ? Names(names) ?? [] : [];

    static IReadOnlyList<string>? Names(ExpressionSyntax expression) {
        if (expression is not ArrayExpressionSyntax array) {
            return null;
        }

        var names = array.Elements.Select(StringValue).ToList();
        return names.Contains(null) ? null : [..names.OfType<string>()];
    }

    internal static string? StringValue(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token: { Kind: SyntaxKind.StringLiteralToken, Value: string value } } ? value : null;
}
