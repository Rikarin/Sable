using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>What may import a module of the standard library: any module, or only another extension.</summary>
public enum ModuleTier {
    /// <summary>A module of the core: it never imports an extension module, directly or through another.</summary>
    Core,

    /// <summary>A module built on the core, which no core module imports.</summary>
    Extension,
}

/// <summary>
///     The tier of each module of the standard library, as
///     <c>lib/package.sb</c> lists them (<c>modules: [.core("..."),
///     .extension("...")]</c>, Sable.Build's <c>Module</c>), embedded in this
///     assembly: the one list, which the compiler reads from the file's
///     syntax rather than evaluating it, so that every compilation can tell a
///     core module that imports an extension one (S3239).
/// </summary>
public static class LibraryTiers {
    const string Resource = "lib/package.sb";

    static readonly Lazy<IReadOnlyList<KeyValuePair<string, ModuleTier>>> Listed = new(Load);

    static readonly Lazy<IReadOnlyDictionary<string, ModuleTier>> ByName = new(static () =>
        Listed.Value.ToDictionary(static m => m.Key, static m => m.Value, StringComparer.Ordinal));

    /// <summary>Every module the list names, with its tier, in the order written.</summary>
    public static IReadOnlyList<KeyValuePair<string, ModuleTier>> Modules => Listed.Value;

    /// <summary>The tier of a module of the standard library; null for any other module, or one the list leaves out.</summary>
    public static ModuleTier? TierOf(string? module) =>
        module is not null && ByName.Value.TryGetValue(module, out var tier) ? tier : null;

    /// <summary>
    ///     The modules <paramref name="text" /> — a <c>package.sb</c> — lists
    ///     with their tiers. It must be written as the library's is: one
    ///     <c>let manifest = Package { ... }</c> whose <c>modules</c> is a
    ///     literal list of <c>.core("name")</c> and <c>.extension("name")</c>,
    ///     each named once.
    /// </summary>
    /// <exception cref="FormatException">It is written some other way.</exception>
    public static IReadOnlyList<KeyValuePair<string, ModuleTier>> Read(string text, string fileName) {
        var unit = (CompilationUnitSyntax)SyntaxTree.Parse(text, fileName).Root;
        var package = unit.Members.OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(static f => f.Declarator.Pattern is BindingPatternSyntax { Identifier.Text: "manifest" })
                ?.Declarator.Initializer?.Value as RecordExpressionSyntax
            ?? throw new FormatException($"{fileName}: declares no `let manifest = Package {{ ... }}`");
        var list = package.Fields.FirstOrDefault(static f => f.NameToken.Text == "modules")?.Expression as ArrayExpressionSyntax
            ?? throw new FormatException($"{fileName}: its Package has no `modules: [...]`, a literal list");

        var modules = new List<KeyValuePair<string, ModuleTier>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in list.Elements) {
            if (element is not InvocationExpressionSyntax {
                    Expression: ImplicitMemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier.Text: var kind and ("core" or "extension") } },
                    ArgumentList.Arguments: [{ NameToken.IsNone: true, Expression: LiteralExpressionSyntax { Token.Value: string name } }],
                }) {
                throw new FormatException($"{fileName}: `{element.ToString().Trim()}` is not `.core(\"name\")` or `.extension(\"name\")`");
            }

            if (!seen.Add(name)) {
                throw new FormatException($"{fileName}: '{name}' is listed twice");
            }

            modules.Add(new(name, kind == "core" ? ModuleTier.Core : ModuleTier.Extension));
        }

        return modules;
    }

    static IReadOnlyList<KeyValuePair<string, ModuleTier>> Load() {
        using var stream = typeof(LibraryTiers).Assembly.GetManifestResourceStream(Resource)!;
        using var reader = new StreamReader(stream);
        return Read(reader.ReadToEnd(), Resource);
    }
}
