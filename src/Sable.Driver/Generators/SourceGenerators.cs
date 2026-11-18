using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sable.Driver.Packages;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Generators;

/// <summary>A function marked <c>@generator("name")</c>, and where it is.</summary>
public sealed record GeneratorFunction(string Name, string Module, string Function, SyntaxTree Tree, FunctionDeclarationSyntax Declaration, AttributeSyntax Attribute);

/// <summary>
///     A package's generator program (docs/08 §8.1): the files under its
///     <c>generators/</c> directory and the generators they declare.
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

    /// <summary>The package the generators are in.</summary>
    public string Package { get; }

    /// <summary>The generator program's files.</summary>
    public IReadOnlyList<SyntaxTree> Files { get; }

    /// <summary>The functions marked <c>@generator</c>, in the order the files declare them.</summary>
    public IReadOnlyList<GeneratorFunction> Generators { get; }

    /// <summary>What a cache of anything the program computes is keyed by: the toolchain, the library and its files.</summary>
    public string Key { get; }

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

        var files = Directory.GetFiles(generators, "*.sb", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .Select(f => (nameOf(f), File.ReadAllText(f)))
            .ToList();
        return files.Count == 0 ? null : FromSources(package, files);
    }

    /// <summary>A generator program from its files' names and text.</summary>
    public static GeneratorSet FromSources(string package, IReadOnlyList<(string Name, string Text)> files) =>
        new(package, [..files.Select(static f => SyntaxTree.Parse(new SourceText(f.Text, f.Name)))]);

    /// <summary>The name in <c>@generator("name")</c>, if it is written as a string literal.</summary>
    static string? GeneratorName(AttributeSyntax attribute) =>
        attribute.ArgumentList is { Arguments: [{ NameToken.IsNone: true, Expression: LiteralExpressionSyntax { Token: { Kind: SyntaxKind.StringLiteralToken, Value: string name } } }] }
            ? name
            : null;
}

/// <summary>A file a generator wrote, and the file whose declaration it was written for.</summary>
/// <param name="Origin">The file with the declaration.</param>
/// <param name="Generator">The generator's name.</param>
/// <param name="FileName">The name the generator gave it.</param>
/// <param name="Text">The text to compile: the module line and the origin's imports, then what the generator wrote.</param>
public sealed record GeneratedFile(SyntaxTree Origin, string Generator, string FileName, string Text);

/// <summary>What running the generators over a package's files gave: the files, and what went wrong.</summary>
public sealed record GenerationResult(IReadOnlyList<GeneratedFile> Files, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> Diagnostics) {
    public static readonly GenerationResult Empty = new([], []);

    public bool HasErrors => Diagnostics.Any(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
}

/// <summary>
///     Runs source generators (docs/08): finds the declarations each answers
///     to, gives them to the generator program in the SIL interpreter, and
///     reads back the files it writes.
/// </summary>
/// <remarks>
///     <para>
///         A generator program is compiled once per text (<see cref="GeneratorSet.Key" />),
///         with a <c>main</c> the compiler writes: it reads each request from
///         standard input, calls the generator, and prints what it produced
///         (<c>Sable.Generators</c>' <c>readGeneratorRequest</c> and
///         <c>writeGeneratorOutput</c>).
///     </para>
///     <para>
///         A run is cached by the program's key, the generator and the
///         declaration as it is given, so a declaration that did not change
///         is never given to a generator again.
///     </para>
/// </remarks>
public static class SourceGenerators {
    /// <summary>Where a package's generators are, under its directory.</summary>
    public const string DirectoryName = "generators";

    /// <summary>The library a generator program is compiled with.</summary>
    public const string LibraryModule = "Sable.Generators";

    /// <summary>The steps one declaration may take a generator: far more than any generator should need.</summary>
    public const int StepLimit = 50_000_000;

    /// <summary>What a generator may import: the library, and the pure parts of the standard library.</summary>
    static readonly HashSet<string> AllowedImports = new(StringComparer.Ordinal) { LibraryModule, "Sable.Text", "Sable.Collections", "Sable.Math" };

    /// <summary>The attributes the compiler gives a meaning: no generator may answer to one (SYNTAX.md §22).</summary>
    static readonly HashSet<string> BuiltInAttributes = new(StringComparer.Ordinal) {
        "inline", "inline_layout", "noncopy", "reflectable", "immutable", "immortal", "flags", "escaping", "polymorphic",
        "deprecated", "available", "extern", "export", "packed", "align", "test", "bench", "main", "discardable",
        "MainActor", "frozen", "derive", "generator", "builtin", "comptime", "allow", "warn", "deny",
    };

    const char FieldSeparator = '\u001f';
    const char RecordStart = '\u001e';
    const char LineBreak = '\u001d';
    const string HostModule = "SableGeneratorHost";

    static readonly ConcurrentDictionary<string, Lazy<CompiledGenerators>> Compiled = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, RunOutput> Runs = new(StringComparer.Ordinal);

    /// <summary>How many times a generator program has been run, for tests of the cache.</summary>
    public static int Executions => _executions;

    static int _executions;

    /// <summary>A generator program, compiled: its module, or why it could not be.</summary>
    sealed record CompiledGenerators(SilModule? Module, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> Diagnostics) {
        public readonly object Gate = new();
    }

    /// <summary>What one run of a generator for one declaration produced.</summary>
    sealed record RunOutput(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<(string Name, string Text)> Files);

    /// <summary>One declaration to give to one generator.</summary>
    sealed record Request(GeneratorSet Set, GeneratorFunction Generator, SyntaxTree Tree, CompilationUnitSyntax Unit, AttributeSyntax Attribute, string Input) {
        public string Key => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Set.Key}\n{Generator.Name}\n{Input}")));
    }

    /// <summary>
    ///     Runs the generators of <paramref name="visible" /> over the files of
    ///     <paramref name="package" />: the generators of the package itself
    ///     and of those it depends on directly (docs/08 §8.2).
    /// </summary>
    public static GenerationResult Run(string package, IReadOnlyList<GeneratorSet> visible, IReadOnlyList<SyntaxTree> files, int stepLimit = StepLimit) {
        if (visible.Count == 0) {
            return GenerationResult.Empty;
        }

        var diagnostics = new List<(Diagnostic, SyntaxTree)>();
        var byName = Visible(visible, diagnostics);

        var requests = new List<Request>();
        foreach (var tree in files) {
            if (tree.Root is CompilationUnitSyntax unit) {
                requests.AddRange(RequestsIn(tree, unit, byName));
            }
        }

        var generated = new List<GeneratedFile>();
        foreach (var group in requests.GroupBy(static r => r.Set)) {
            var compiled = Compile(group.Key);
            diagnostics.AddRange(compiled.Diagnostics);
            if (compiled.Module is not { } module) {
                continue;
            }

            var outputs = Execute(compiled, module, [..group], stepLimit, diagnostics);
            var names = new Dictionary<(string Generator, string Name), Request>();
            foreach (var request in group) {
                if (outputs.TryGetValue(request, out var output)) {
                    generated.AddRange(Collect(package, request, output, names, diagnostics));
                }
            }
        }

        return new(generated, diagnostics);
    }

    /// <summary>
    ///     The generators by the attribute they answer to, with what is wrong
    ///     with their names reported: none, a built-in one, or one taken twice.
    /// </summary>
    static Dictionary<string, (GeneratorSet Set, GeneratorFunction Generator)> Visible(IReadOnlyList<GeneratorSet> visible, List<(Diagnostic, SyntaxTree)> diagnostics) {
        var byName = new Dictionary<string, (GeneratorSet Set, GeneratorFunction Generator)>(StringComparer.Ordinal);
        foreach (var set in visible) {
            foreach (var generator in set.Generators) {
                if (generator.Name.Length == 0) {
                    diagnostics.Add((Rule(generator.Attribute.Span, "a generator is named by a string: '@generator(\"name\")' runs for types marked '@name'"), generator.Tree));
                } else if (BuiltInAttributes.Contains(generator.Name)) {
                    diagnostics.Add((Rule(generator.Attribute.Span, $"'@{generator.Name}' is the compiler's own attribute; a generator cannot answer to it"), generator.Tree));
                } else if (byName.TryGetValue(generator.Name, out var other)) {
                    var where = other.Set == set ? "this package" : $"package '{other.Set.Package}'";
                    diagnostics.Add((Rule(generator.Attribute.Span, $"a generator called '{generator.Name}' is already declared, in {where}: two generators cannot answer to one attribute"), generator.Tree));
                } else {
                    byName[generator.Name] = (set, generator);
                }
            }
        }

        return byName;
    }

    /// <summary>The top-level types in the file that carry a generator's attribute, one request for each such attribute.</summary>
    static IEnumerable<Request> RequestsIn(SyntaxTree tree, CompilationUnitSyntax unit, Dictionary<string, (GeneratorSet Set, GeneratorFunction Generator)> byName) {
        foreach (var member in unit.Members) {
            if (TypeParts(member) is not { } parts) {
                continue;
            }

            foreach (var attribute in parts.Attributes) {
                if (byName.TryGetValue(attribute.Name.ToString().Trim(), out var found)) {
                    yield return new(found.Set, found.Generator, tree, unit, attribute, Describe(tree, unit, member, parts));
                }
            }
        }
    }

    /// <summary>What every kind of type declaration has.</summary>
    sealed record TypeDeclarationParts(
        string Keyword,
        SyntaxToken Identifier,
        SyntaxList<AttributeSyntax> Attributes,
        SyntaxTokenList Modifiers,
        GenericParameterListSyntax? TypeParameters,
        BaseTypeListSyntax? BaseTypes,
        SyntaxList<SyntaxNode> Members);

    static TypeDeclarationParts? TypeParts(SyntaxNode member) => member switch {
        StructDeclarationSyntax s => new("struct", s.Identifier, s.Attributes, s.Modifiers, s.TypeParameters, s.BaseTypes, s.Members),
        ClassDeclarationSyntax c => new("class", c.Identifier, c.Attributes, c.Modifiers, c.TypeParameters, c.BaseTypes, c.Members),
        ActorDeclarationSyntax a => new("actor", a.Identifier, a.Attributes, a.Modifiers, a.TypeParameters, a.BaseTypes, a.Members),
        UnionDeclarationSyntax u => new("union", u.Identifier, u.Attributes, u.Modifiers, u.TypeParameters, u.BaseTypes, u.Members),
        EnumDeclarationSyntax e => new("enum", e.Identifier, e.Attributes, e.Modifiers, null, e.BaseTypes, e.Members),
        _ => null,
    };

    /// <summary>The declaration as the generator is given it: the lines <c>readGeneratorRequest</c> reads, but the first.</summary>
    static string Describe(SyntaxTree tree, CompilationUnitSyntax unit, SyntaxNode declaration, TypeDeclarationParts parts) {
        var lines = new StringBuilder();

        void Line(params string[] fields) => lines.Append(string.Join(FieldSeparator, fields.Select(Encoded))).Append('\n');

        var module = unit.Module is { } m ? m.Name.ToString().Trim() : "";
        Line("D", parts.Keyword, parts.Identifier.Text, module, Flag(IsPublic(parts.Modifiers)), tree.Text.FileName);
        foreach (var import in unit.Imports) {
            Line("I", import.Name.ToString().Trim());
        }

        foreach (var parameter in parts.TypeParameters?.Parameters ?? default) {
            Line("T", parameter.Identifier.Text);
        }

        foreach (var conformance in parts.BaseTypes?.Types ?? default) {
            Line("C", conformance.ToString().Trim());
        }

        Attributes(parts.Attributes, "d");
        foreach (var member in parts.Members) {
            switch (member) {
                case FieldDeclarationSyntax field when IsStored(field):
                    Line("F", NameOf(field), field.Declarator.TypeAnnotation?.Type.ToString().Trim() ?? "", Flag(field.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword),
                        Flag(field.Declarator.Initializer is not null), Flag(IsPublic(field.Modifiers)));
                    Attributes(field.Attributes, "f");
                    break;
                case UnionCaseDeclarationSyntax unionCase:
                    Line("K", unionCase.Identifier.Text, "0", "");
                    Attributes(unionCase.Attributes, "c");
                    foreach (var payload in unionCase.Payload?.Fields ?? default) {
                        Line("P", payload.NameToken.IsNone ? "" : payload.NameToken.Text, payload.Type.ToString().Trim());
                    }

                    break;
                case EnumCaseDeclarationSyntax enumCase:
                    Line("K", enumCase.Identifier.Text, Flag(enumCase.Value is not null), enumCase.Value?.ToString().Trim() ?? "");
                    Attributes(enumCase.Attributes, "c");
                    break;
            }
        }

        Line("E");
        return lines.ToString();

        void Attributes(SyntaxList<AttributeSyntax> attributes, string target) {
            foreach (var attribute in attributes) {
                Line("A", target, attribute.Name.ToString().Trim());
                foreach (var argument in attribute.ArgumentList?.Arguments ?? default) {
                    var text = argument.Expression.ToString().Trim();
                    var value = argument.Expression is LiteralExpressionSyntax { Token: { Kind: SyntaxKind.StringLiteralToken, Value: string literal } } ? literal : null;
                    Line("G", argument.NameToken.IsNone ? "" : argument.NameToken.Text, text, Flag(value is not null), value ?? "");
                }
            }
        }
    }

    /// <summary>A field with storage: not static, and with no accessors of its own.</summary>
    static bool IsStored(FieldDeclarationSyntax field) =>
        field.Accessors is null
        && field.Declarator.Pattern is BindingPatternSyntax
        && !field.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);

    static string NameOf(FieldDeclarationSyntax field) => ((BindingPatternSyntax)field.Declarator.Pattern).Identifier.Text;

    static bool IsPublic(SyntaxTokenList modifiers) => modifiers.Any(static m => m.Kind == SyntaxKind.PublicKeyword);

    static string Flag(bool value) => value ? "1" : "0";

    /// <summary>A value as one field of one line: line breaks as U+001D, and the separators the protocol uses taken out.</summary>
    static string Encoded(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(FieldSeparator, ' ')
            .Replace(RecordStart, ' ')
            .Replace('\n', LineBreak);

    // ---- Compiling ----------------------------------------------------------

    /// <summary>The generator program, compiled once per text for the life of the process.</summary>
    static CompiledGenerators Compile(GeneratorSet set) =>
        Compiled.GetOrAdd(set.Key, _ => new(() => CompileUncached(set))).Value;

    static CompiledGenerators CompileUncached(GeneratorSet set) {
        var diagnostics = new List<(Diagnostic, SyntaxTree)>();
        foreach (var tree in set.Files) {
            diagnostics.AddRange(tree.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).Select(d => (d, tree)));
            CheckShape(tree, set, diagnostics);
        }

        if (diagnostics.Count > 0) {
            return new(null, diagnostics);
        }

        var generators = set.Generators.Where(static g => g.Name.Length > 0).ToList();
        var host = Host(generators);
        IReadOnlyList<SyntaxTree> program = [host.Main, ..host.Thunks, ..set.Files];
        var compilation = Compilation.Create([..program, ..Library.For(program)]);

        // A generator whose signature is wrong is said once, at the generator:
        // the call the compiler wrote to it would fail too, less helpfully.
        foreach (var generator in generators) {
            if (SignatureProblem(compilation, generator) is { } problem) {
                diagnostics.Add((Rule(generator.Declaration.Identifier.Span, problem), generator.Tree));
            }
        }

        if (diagnostics.Count > 0) {
            return new(null, diagnostics);
        }

        var own = set.Files.ToHashSet();
        foreach (var (diagnostic, file) in compilation.GetAllDiagnosticsByFile().Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)) {
            if (file is not null && own.Contains(file)) {
                diagnostics.Add((diagnostic, file));
            } else {
                // The compiler's files fail only on what a generator's file does.
                diagnostics.Add((diagnostic with { Message = $"in the code the compiler writes to run the generators: {diagnostic.Message}" }, file ?? set.Files[0]));
            }
        }

        if (diagnostics.Count > 0) {
            return new(null, diagnostics);
        }

        foreach (var tree in set.Files) {
            CheckEffects(compilation, tree, diagnostics);
        }

        if (diagnostics.Count > 0) {
            return new(null, diagnostics);
        }

        var (module, lowering) = SilGen.Lower(compilation);
        foreach (var diagnostic in lowering.Where(static d => d.Severity == DiagnosticSeverity.Error)) {
            diagnostics.Add((diagnostic, set.Files.FirstOrDefault(f => diagnostic.Span.End <= f.Text.Length) ?? set.Files[0]));
        }

        if (diagnostics.Count > 0) {
            return new(null, diagnostics);
        }

        if (SilVerifier.Verify(module) is { Count: > 0 } problems) {
            return new(null, [(Diagnostic.Error(ErrorCode.ERR_GeneratorRule, default, $"internal error: SIL verification failed: {problems[0]}"), set.Files[0])]);
        }

        return new(module, []);
    }

    /// <summary>
    ///     What the compiler writes to run a program's generators: a thunk in
    ///     each module, which the generator need not make public, and a main
    ///     that reads the requests and calls the thunks.
    /// </summary>
    static (SyntaxTree Main, IReadOnlyList<SyntaxTree> Thunks) Host(IReadOnlyList<GeneratorFunction> generators) {
        var thunks = new List<SyntaxTree>();
        var main = new StringBuilder($"module {HostModule}\n\nimport {LibraryModule}\n");
        var calls = new StringBuilder();
        var index = 0;
        foreach (var module in generators.GroupBy(static g => g.Module)) {
            var thunk = new StringBuilder();
            if (module.Key.Length > 0) {
                thunk.Append(CultureInfo.InvariantCulture, $"module {module.Key}\n\n");
            }

            thunk.Append(CultureInfo.InvariantCulture, $"import {LibraryModule}\n\n// Written by the compiler to run this module's generators (docs/08).\n");
            var names = new List<string>();
            foreach (var generator in module) {
                var name = $"sableGenerator{index++}";
                names.Add(name);
                thunk.Append(CultureInfo.InvariantCulture, $"public fn {name}(_ declaration: Declaration, _ output: inout Output) {{\n    {generator.Function}(declaration, into: &output)\n}}\n");
                var branch = calls.Length == 0 ? "if" : "} else if";
                calls.Append(CultureInfo.InvariantCulture, $"        {branch} request.generator == {Quote(generator.Name)} {{\n            {name}(request.declaration, &output)\n");
            }

            thunks.Add(SyntaxTree.Parse(new SourceText(thunk.ToString(), $"<generators:{module.Key}>")));
            if (module.Key.Length > 0) {
                main.Append(CultureInfo.InvariantCulture, $"import {module.Key}.{{ {string.Join(", ", names)} }}\n");
            }
        }

        if (calls.Length > 0) {
            calls.Append("        }\n");
        }

        main.Append(CultureInfo.InvariantCulture, $$"""

            fn main() {
                while let request = readGeneratorRequest() {
                    var output = Output()
            {{calls}}        writeGeneratorOutput(output, for: request.index)
                }
            }

            """);
        return (SyntaxTree.Parse(new SourceText(main.ToString(), "<generators>")), thunks);
    }

    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>What can be told from the syntax: the imports a generator may make, and no conditional compilation.</summary>
    static void CheckShape(SyntaxTree tree, GeneratorSet set, List<(Diagnostic, SyntaxTree)> diagnostics) {
        if (tree.Root is not CompilationUnitSyntax unit) {
            return;
        }

        var own = set.Files.Select(static f => f.Root).OfType<CompilationUnitSyntax>()
            .Select(static u => u.Module?.Name.ToString().Trim()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var import in unit.Imports) {
            var name = import.Name.ToString().Trim();
            if (!AllowedImports.Contains(name) && !own.Contains(name)) {
                diagnostics.Add((Rule(import.Name.Span,
                    $"a generator cannot import '{name}': it may import only {string.Join(", ", AllowedImports.Order(StringComparer.Ordinal))} and its own modules, so that it cannot read, write or wait (docs/08 §8.5)"), tree));
            }
        }

        foreach (var token in tree.Root.DescendantTokens()) {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia)) {
                if (trivia.Kind is SyntaxKind.DirectiveTrivia or SyntaxKind.DisabledTextTrivia) {
                    diagnostics.Add((Rule(trivia.Span,
                        "a generator has no conditional compilation: it gives the same output on every machine, and what depends on the platform goes in the code it writes (docs/08 §8.5)"), tree));
                    return;
                }
            }
        }
    }

    /// <summary>Why the generator's signature is not <c>(_: Declaration, into: inout Output)</c> returning nothing, or null.</summary>
    static string? SignatureProblem(Compilation compilation, GeneratorFunction generator) {
        const string expected = "a generator is declared 'fn name(_ declaration: Declaration, into output: inout Output)'";
        if (compilation.GetSemanticModel(generator.Tree).GetSymbolOf(generator.Declaration) is not FunctionSymbol function) {
            return expected;
        }

        if (function.TypeParameters.Length > 0 || function.IsAsync || function.Parameters.Length != 2) {
            return expected;
        }

        var (declaration, output) = (function.Parameters[0], function.Parameters[1]);
        var ok = declaration.Label is null or "_"
            && declaration.Modifier is ParameterModifier.None or ParameterModifier.Borrowing
            && IsLibraryType(declaration.Type, "Declaration")
            && output.Label == "into"
            && output.Modifier == ParameterModifier.InOut
            && IsLibraryType(output.Type, "Output")
            && function.ReturnType.Name == "Void";
        return ok ? null : expected;
    }

    static bool IsLibraryType(TypeSymbol type, string name) =>
        type is NamedTypeSymbol { ContainingSymbol: ModuleSymbol { Name: LibraryModule } } named && named.Name == name;

    /// <summary>Nothing a generator names may read, write or wait, nor talk to the compiler for itself.</summary>
    static void CheckEffects(Compilation compilation, SyntaxTree tree, List<(Diagnostic, SyntaxTree)> diagnostics) {
        var model = compilation.GetSemanticModel(tree);
        var reported = new HashSet<int>();
        foreach (var node in tree.Root.DescendantNodes()) {
            if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax) || model.GetSymbolOf(node) is not { } symbol) {
                continue;
            }

            var effect = ManifestEvaluator.Effect(symbol)
                ?? (symbol is FunctionSymbol { Name: "readGeneratorRequest" or "writeGeneratorOutput", ContainingSymbol: ModuleSymbol { Name: LibraryModule } } ? symbol.Name : null);
            if (effect is not null && reported.Add(node.Span.Start)) {
                diagnostics.Add((Rule(node.Span, $"a generator cannot use '{effect}': it may not read, write or wait, so that it gives the same output on every machine (docs/08 §8.5)"), tree));
            }
        }
    }

    static Diagnostic Rule(TextSpan span, string message) => Diagnostic.Error(ErrorCode.ERR_GeneratorRule, span, message);

    // ---- Running ------------------------------------------------------------

    /// <summary>
    ///     Each request's output: from the cache, or from running the program
    ///     over those it does not have, all in one run. A request whose run
    ///     traps is reported, and the run goes on from the one after it.
    /// </summary>
    static Dictionary<Request, RunOutput> Execute(CompiledGenerators compiled, SilModule module, IReadOnlyList<Request> requests, int stepLimit, List<(Diagnostic, SyntaxTree)> diagnostics) {
        var outputs = new Dictionary<Request, RunOutput>();
        var pending = new List<Request>();
        foreach (var request in requests) {
            if (Runs.TryGetValue(request.Key, out var known)) {
                outputs[request] = known;
            } else {
                pending.Add(request);
            }
        }

        while (pending.Count > 0) {
            var input = new StringBuilder();
            for (var i = 0; i < pending.Count; i++) {
                input.Append(string.Join(FieldSeparator, "R", pending[i].Generator.Name, i.ToString(CultureInfo.InvariantCulture))).Append('\n').Append(pending[i].Input);
            }

            string? trap = null;
            string text;
            lock (compiled.Gate) {
                Interlocked.Increment(ref _executions);
                var interpreter = new SilInterpreter(module, checked(stepLimit * pending.Count)) { Input = new StringReader(input.ToString()) };
                try {
                    interpreter.Run(module.EntryPoint!);
                } catch (SilTrap failure) {
                    trap = failure.Message;
                }

                text = interpreter.Output.ToString();
            }

            var read = Parse(text);
            for (var i = 0; i < pending.Count; i++) {
                if (read.TryGetValue(i, out var output)) {
                    outputs[pending[i]] = Runs[pending[i].Key] = output;
                }
            }

            if (trap is null) {
                break;
            }

            // Output is written when a generator returns, so the first
            // request without any is the one that trapped.
            var failed = Enumerable.Range(0, pending.Count).FirstOrDefault(i => !read.ContainsKey(i));
            var request = pending[failed];
            var reason = trap.Contains("step limit", StringComparison.Ordinal)
                ? $"the generator '{request.Generator.Name}' took more than {stepLimit:N0} steps for '{TypeName(request)}': does it loop forever?"
                : $"the generator '{request.Generator.Name}' trapped on '{TypeName(request)}': {trap}";
            diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_GeneratorFailed, request.Attribute.Span, reason), request.Tree));
            pending = pending.Skip(failed + 1).ToList();
        }

        return outputs;
    }

    static string TypeName(Request request) => request.Attribute.Parent is { } declaration && TypeParts(declaration) is { } parts ? parts.Identifier.Text : "?";

    /// <summary>The program's output, by the index of the request each run was for.</summary>
    static Dictionary<int, RunOutput> Parse(string text) {
        var runs = new Dictionary<int, RunOutput>();
        List<string>? errors = null, warnings = null;
        List<(string, string)>? files = null;
        string? file = null;
        var body = new StringBuilder();
        var index = -1;

        void EndFile() {
            if (file is not null) {
                files!.Add((file, body.ToString()));
                file = null;
                body.Clear();
            }
        }

        void EndRun() {
            EndFile();
            if (index >= 0) {
                runs[index] = new(errors!, warnings!, files!);
            }
        }

        var lines = text.Split('\n');
        // print ends each line with a break: the last, empty, piece is not a line.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (var i = 0; i < count; i++) {
            var line = lines[i];
            if (line.Length > 0 && line[0] == RecordStart) {
                var parts = line[1..].Split(FieldSeparator, 2);
                var value = parts.Length > 1 ? parts[1] : "";
                switch (parts[0]) {
                    case "run":
                        EndRun();
                        index = int.Parse(value, CultureInfo.InvariantCulture);
                        (errors, warnings, files) = ([], [], []);
                        continue;
                    case "error":
                        EndFile();
                        errors?.Add(value);
                        continue;
                    case "warning":
                        EndFile();
                        warnings?.Add(value);
                        continue;
                    case "file":
                        EndFile();
                        file = value;
                        continue;
                }
            }

            if (file is not null) {
                body.Append(line).Append('\n');
            }
        }

        EndRun();
        return runs;
    }

    // ---- What comes back ----------------------------------------------------

    /// <summary>A run's files, checked and made ready to compile, and its messages reported at the attribute.</summary>
    static IEnumerable<GeneratedFile> Collect(
        string package,
        Request request,
        RunOutput output,
        Dictionary<(string Generator, string Name), Request> names,
        List<(Diagnostic, SyntaxTree)> diagnostics
    ) {
        var at = request.Attribute.Span;
        var generator = request.Generator.Name;
        foreach (var error in output.Errors) {
            diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_GeneratorReported, at, $"{generator}: {error}"), request.Tree));
        }

        foreach (var warning in output.Warnings) {
            diagnostics.Add((Diagnostic.Warning(ErrorCode.WRN_GeneratorReported, at, $"{generator}: {warning}"), request.Tree));
        }

        foreach (var (name, text) in output.Files) {
            if (FileNameProblem(name) is { } problem) {
                diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_GeneratedFileInvalid, at, $"the generator '{generator}' wrote a file called '{name}': {problem}"), request.Tree));
                continue;
            }

            if (names.TryGetValue((generator, name), out var first)) {
                diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_GeneratedFileInvalid, at,
                    $"the generator '{generator}' wrote '{name}' for '{TypeName(first)}' already, in package '{package}': each file it writes needs a name of its own, such as one with the type's name in it"), request.Tree));
                continue;
            }

            var parsed = SyntaxTree.Parse(new SourceText(text, name));
            if (parsed.Root is CompilationUnitSyntax { Module: not null }) {
                diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_GeneratedFileInvalid, at,
                    $"the generator '{generator}' wrote a 'module' line in '{name}': the compiler puts the file in the module of '{TypeName(request)}' itself"), request.Tree));
                continue;
            }

            names[(generator, name)] = request;
            yield return new(request.Tree, generator, name, WithHeader(request, generator, text, parsed));
        }
    }

    static string? FileNameProblem(string name) =>
        name.Length == 0 ? "a file needs a name"
        : name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal) ? "a name is one file's, without '/', '\\' or '..'"
        : !name.EndsWith(".sb", StringComparison.Ordinal) ? "a source file's name ends in '.sb'"
        : name.Any(static c => char.IsControl(c)) ? "a name cannot have control characters in it"
        : null;

    /// <summary>
    ///     The file as it is compiled: in the module of the declaration it is
    ///     for, with that declaration's file's imports but those it makes itself.
    /// </summary>
    static string WithHeader(Request request, string generator, string text, SyntaxTree parsed) {
        var header = new StringBuilder();
        header.Append(CultureInfo.InvariantCulture, $"// Written by the generator '{generator}' for '{TypeName(request)}' in {request.Tree.Text.FileName}. Do not edit: it is written again on every build.\n");
        if (request.Unit.Module is { } module) {
            header.Append(CultureInfo.InvariantCulture, $"module {module.Name.ToString().Trim()}\n");
        }

        var own = parsed.Root is CompilationUnitSyntax unit
            ? unit.Imports.Select(static i => i.Name.ToString().Trim()).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var import in request.Unit.Imports.Where(i => !own.Contains(i.Name.ToString().Trim()))) {
            header.Append(import.ToString().Trim()).Append('\n');
        }

        return header.Append('\n').Append(text).ToString();
    }
}
