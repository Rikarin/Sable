using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sable.Driver.Packages;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Generators;

/// <summary>A file a generator wrote, and the file whose declaration it was written for.</summary>
/// <param name="Origin">The file with the declaration; for a package generator's file, its first declaration's, or the package's first file.</param>
/// <param name="Generator">The generator's name.</param>
/// <param name="FileName">The name the generator gave it.</param>
/// <param name="Text">The text to compile: the module line and the origin's imports, then what the generator wrote.</param>
public sealed record GeneratedFile(SyntaxTree Origin, string Generator, string FileName, string Text);

/// <summary>What running the generators over a package's files gave: the files, and what went wrong.</summary>
public sealed record GenerationResult(IReadOnlyList<GeneratedFile> Files, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> Diagnostics) {
    public static readonly GenerationResult Empty = new([], []);

    public bool HasErrors => Diagnostics.Any(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
}

/// <summary>A file a package lists for its generators (docs/08 §8.3): its path relative to the package, and its text.</summary>
public sealed record GeneratorInputFile(string Path, string Text);

/// <summary>What the generators of one package are run over.</summary>
/// <param name="Package">The package's name.</param>
/// <param name="Visible">The generators it can see: its own, and its direct dependencies'.</param>
/// <param name="Files">Its files, the declarations in which are given to the generators.</param>
public sealed record GenerationInput(string Package, IReadOnlyList<GeneratorSet> Visible, IReadOnlyList<SyntaxTree> Files) {
    /// <summary>What else its types resolve against: its dependencies' files, with what their generators wrote.</summary>
    public IReadOnlyList<SyntaxTree> References { get; init; } = [];

    /// <summary>The files its manifest lists for its generators.</summary>
    public IReadOnlyList<GeneratorInputFile> Inputs { get; init; } = [];

    /// <summary>The steps one declaration may take a generator.</summary>
    public int StepLimit { get; init; } = SourceGenerators.StepLimit;
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
///         A run is cached by the program's key, the generator and the run's
///         input as it is given, so a declaration that did not change is never
///         given to a generator again.
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
        "inline", "inlinable", "vectorised", "inline_layout", "noncopy", "reflectable", "immutable", "immortal", "flags", "escaping", "polymorphic",
        "deprecated", "available", "extern", "export", "packed", "align", "test", "bench", "fuzz", "main", "discardable",
        "MainActor", "frozen", "derive", "generator", "builtin", "comptime", "allow", "warn", "deny", "indirect", "intrinsic",
    };

    const char FieldSeparator = '\u001f';
    const char RecordStart = '\u001e';
    const string HostModule = "SableGeneratorHost";

    static readonly ConcurrentDictionary<string, Lazy<CompiledGenerators>> Compiled = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, RunOutput> Runs = new(StringComparer.Ordinal);

    /// <summary>How many times a generator program has been run, for tests of the cache.</summary>
    public static int Executions => _executions;

    static int _executions;

    /// <summary>A generator program, compiled: its module, or why it could not be.</summary>
    sealed record CompiledGenerators(SilModule? Module, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> Diagnostics) {
        public readonly object Gate = new();

        /// <summary>The trees of the set it was compiled from, which its diagnostics are on.</summary>
        public IReadOnlyList<SyntaxTree> Files { get; init; } = [];

        /// <summary>
        ///     Its diagnostics, on a set's own trees. The program is cached by
        ///     its text, so the set asking may hold that same text parsed again
        ///     — a file opened in the editor after it was read from disk — and
        ///     a diagnostic on another set's tree would be reported on no file
        ///     the asker has. Each goes to the tree in its file's place.
        /// </summary>
        public IEnumerable<(Diagnostic Diagnostic, SyntaxTree Tree)> DiagnosticsFor(GeneratorSet set) {
            foreach (var (diagnostic, tree) in Diagnostics) {
                var index = -1;
                for (var i = 0; i < Files.Count && index < 0; i++) {
                    if (ReferenceEquals(Files[i], tree)) {
                        index = i;
                    }
                }

                yield return (diagnostic, index >= 0 && index < set.Files.Count ? set.Files[index] : tree);
            }
        }
    }

    /// <summary>A message a run reported, and where: a location's id, or "" for the attribute that started it.</summary>
    sealed record RunMessage(DiagnosticSeverity Severity, string Location, string Text);

    /// <summary>What one run produced.</summary>
    sealed record RunOutput(IReadOnlyList<RunMessage> Messages, IReadOnlyList<(string Name, string Module, string Text)> Files);

    /// <summary>
    ///     One run: a declaration for a declaration generator, or every
    ///     declaration with the attribute for a package generator.
    /// </summary>
    sealed class Request(GeneratorSet set, GeneratorFunction generator, IReadOnlyList<GeneratorItem> items, string input) {
        public GeneratorSet Set { get; } = set;
        public GeneratorFunction Generator { get; } = generator;
        public IReadOnlyList<GeneratorItem> Items { get; } = items;

        /// <summary>The lines after <c>R</c>, through <c>Z</c>.</summary>
        public string Input { get; } = input;

        public string Key { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{set.Key}\n{generator.Name}\n{input}")));

        /// <summary>What it is about, for messages.</summary>
        public string Subject => Items.Count == 0 ? "the package" : Generator.Kind == GeneratorKind.Package ? $"{Items.Count} declaration(s)" : $"'{Items[0].Name}'";
    }

    /// <summary>
    ///     Runs the generators of <paramref name="visible" /> over the files of
    ///     <paramref name="package" />: the generators of the package itself
    ///     and of those it depends on directly (docs/08 §8.2).
    /// </summary>
    public static GenerationResult Run(string package, IReadOnlyList<GeneratorSet> visible, IReadOnlyList<SyntaxTree> files, int stepLimit = StepLimit) =>
        Run(new GenerationInput(package, visible, files) { StepLimit = stepLimit });

    /// <summary>Runs a package's generators over its files, as <paramref name="input" /> describes them.</summary>
    public static GenerationResult Run(GenerationInput input) {
        if (input.Visible.Count == 0) {
            return GenerationResult.Empty;
        }

        var diagnostics = new List<(Diagnostic, SyntaxTree)>();
        var byName = Visible(input.Visible, diagnostics);
        var requests = Requests(input, byName);

        var generated = new List<GeneratedFile>();
        var modules = input.Files.Select(static f => (f.Root as CompilationUnitSyntax)?.Module?.Name.ToString().Trim()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var group in requests.GroupBy(static r => r.Set)) {
            var compiled = Compile(group.Key);
            diagnostics.AddRange(compiled.DiagnosticsFor(group.Key));
            if (compiled.Module is not { } module) {
                continue;
            }

            var outputs = Execute(compiled, module, [..group], input.StepLimit, diagnostics);
            var names = new Dictionary<(string Generator, string Name), Request>();
            foreach (var request in group) {
                if (outputs.TryGetValue(request, out var output)) {
                    generated.AddRange(Collect(input, request, output, names, modules, diagnostics));
                }
            }
        }

        return new(generated, diagnostics);
    }

    /// <summary>
    ///     What is wrong with a generator program, as the editor shows it on
    ///     the program's files (docs/08 §8.7): the rules of §8.5 and the
    ///     generators' names. The compiler's own errors are the editor's
    ///     analysis's to report.
    /// </summary>
    public static IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> Check(GeneratorSet set) {
        var diagnostics = new List<(Diagnostic, SyntaxTree)>();
        Visible([set], diagnostics);
        diagnostics.AddRange(Compile(set).DiagnosticsFor(set).Where(static d => d.Diagnostic.Code == ErrorCode.ERR_GeneratorRule));
        return diagnostics;
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
                    diagnostics.Add((Rule(generator.Attribute.Span,
                        "a generator is named by a string: '@generator(\"name\")' runs for declarations marked '@name'; it may also say 'members: [\"a\", \"b\"]', the attributes it reads on fields, cases and parameters, and 'always: true'"),
                        generator.Tree));
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

    /// <summary>
    ///     The runs to make: one per declaration for a declaration generator,
    ///     and one per package for a package generator that has declarations,
    ///     or says <c>always: true</c>.
    /// </summary>
    static List<Request> Requests(GenerationInput input, Dictionary<string, (GeneratorSet Set, GeneratorFunction Generator)> byName) {
        var describer = new GeneratorRequests(input.Files, input.References);
        var names = byName.Keys.ToHashSet(StringComparer.Ordinal);
        var found = new List<(string Name, SyntaxTree Tree, CompilationUnitSyntax Unit, SyntaxNode Node, AttributeSyntax Attribute, string Container)>();
        foreach (var tree in input.Files) {
            if (tree.Root is CompilationUnitSyntax unit) {
                found.AddRange(GeneratorRequests.Find(unit, names).Select(f => (f.Name, tree, unit, f.Node, f.Attribute, f.Container)));
            }
        }

        var requests = new List<Request>();
        foreach (var (name, (set, generator)) in byName.OrderBy(static g => g.Key, StringComparer.Ordinal)) {
            var mine = found.Where(f => f.Name == name).ToList();
            if (generator.Kind == GeneratorKind.Declaration) {
                foreach (var f in mine) {
                    var (lines, item) = describer.Describe(0, f.Tree, f.Unit, f.Node, f.Attribute, f.Container);
                    requests.Add(new(set, generator, [item], Header(generator, "d", input.Package, []) + lines + "Z\n"));
                }

                continue;
            }

            if (mine.Count == 0 && !generator.Always) {
                continue;
            }

            var text = new StringBuilder(Header(generator, "p", input.Package, input.Inputs));
            var items = new List<GeneratorItem>();
            for (var i = 0; i < mine.Count; i++) {
                var (lines, item) = describer.Describe(i, mine[i].Tree, mine[i].Unit, mine[i].Node, mine[i].Attribute, mine[i].Container);
                text.Append(lines);
                items.Add(item);
            }

            requests.Add(new(set, generator, items, text.Append("Z\n").ToString()));
        }

        return requests;
    }

    /// <summary>A request's lines before its declarations, but its <c>R</c> line, which carries its index in a run.</summary>
    static string Header(GeneratorFunction generator, string kind, string package, IReadOnlyList<GeneratorInputFile> inputs) {
        var text = new StringBuilder();
        text.Append(string.Join(FieldSeparator, kind, GeneratorRequests.Encoded(package))).Append('\n');
        foreach (var file in inputs) {
            text.Append(string.Join(FieldSeparator, "N", GeneratorRequests.Encoded(file.Path), GeneratorRequests.Encoded(file.Text))).Append('\n');
        }

        return text.ToString();
    }

    // ---- Compiling ----------------------------------------------------------

    /// <summary>The generator program, compiled once per text for the life of the process.</summary>
    static CompiledGenerators Compile(GeneratorSet set) =>
        Compiled.GetOrAdd(set.Key, _ => new(() => CompileUncached(set) with { Files = set.Files })).Value;

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
                var (type, given) = generator.Kind == GeneratorKind.Package ? ("Package", "request.pkg") : ("Declaration", "request.declaration");
                thunk.Append(CultureInfo.InvariantCulture, $"public fn {name}(_ given: {type}, _ output: inout Output) {{\n    {generator.Function}(given, into: &output)\n}}\n");
                var branch = calls.Length == 0 ? "if" : "} else if";
                calls.Append(CultureInfo.InvariantCulture, $"        {branch} request.generator == {Quote(generator.Name)} {{\n            {name}({given}, &output)\n");
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

    /// <summary>
    ///     Why the generator's signature is not <c>(_: Declaration, into: inout Output)</c>
    ///     or <c>(_: Package, into: inout Output)</c> returning nothing, or null.
    /// </summary>
    static string? SignatureProblem(Compilation compilation, GeneratorFunction generator) {
        const string expected = "a generator is declared 'fn name(_ declaration: Declaration, into output: inout Output)', or, to be given the whole package, 'fn name(_ pkg: Package, into output: inout Output)'";
        if (generator.Always && generator.Kind != GeneratorKind.Package) {
            return "'always: true' is for a generator given the whole package, 'fn name(_ pkg: Package, into output: inout Output)': a generator given one declaration runs only when there is one";
        }

        if (compilation.GetSemanticModel(generator.Tree).GetSymbolOf(generator.Declaration) is not FunctionSymbol function) {
            return expected;
        }

        if (function.TypeParameters.Length > 0 || function.IsAsync || function.Parameters.Length != 2) {
            return expected;
        }

        var (declaration, output) = (function.Parameters[0], function.Parameters[1]);
        var ok = declaration.Label is null or "_"
            && declaration.Modifier == ParameterModifier.None
            && IsLibraryType(declaration.Type is ReferenceTypeSymbol { RelativeWidth: null } shared ? shared.ReferencedType : declaration.Type, generator.Kind == GeneratorKind.Package ? "Package" : "Declaration")
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
            long steps = 0;
            for (var i = 0; i < pending.Count; i++) {
                input.Append(string.Join(FieldSeparator, "R", pending[i].Generator.Name, i.ToString(CultureInfo.InvariantCulture))).Append(FieldSeparator).Append(pending[i].Input);
                steps += (long)stepLimit * Math.Max(1, pending[i].Items.Count);
            }

            string? trap = null;
            string text;
            lock (compiled.Gate) {
                Interlocked.Increment(ref _executions);
                var interpreter = new SilInterpreter(module, (int)Math.Min(steps, int.MaxValue)) { Input = new StringReader(input.ToString()) };
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
                ? $"the generator '{request.Generator.Name}' took more than {stepLimit:N0} steps for {request.Subject}: does it loop forever?"
                : $"the generator '{request.Generator.Name}' trapped on {request.Subject}: {trap}";
            var (tree, span) = DefaultPlace(request);
            diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_GeneratorFailed, span, reason), tree));
            pending = pending.Skip(failed + 1).ToList();
        }

        return outputs;
    }

    /// <summary>Where a message about a run goes when it says nothing else: its first declaration's attribute, or the generator's own.</summary>
    static (SyntaxTree Tree, TextSpan Span) DefaultPlace(Request request) =>
        request.Items.Count > 0 ? (request.Items[0].Tree, request.Items[0].Attribute.Span) : (request.Generator.Tree, request.Generator.Attribute.Span);

    /// <summary>Where a location's id points: <c>declaration:part</c> within the request.</summary>
    static (SyntaxTree Tree, TextSpan Span) Place(Request request, string location) {
        var parts = location.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var item) && item < request.Items.Count
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var part) && part < request.Items[item].Spans.Count) {
            return (request.Items[item].Tree, request.Items[item].Spans[part]);
        }

        return DefaultPlace(request);
    }

    /// <summary>The program's output, by the index of the request each run was for.</summary>
    static Dictionary<int, RunOutput> Parse(string text) {
        var runs = new Dictionary<int, RunOutput>();
        List<RunMessage>? messages = null;
        List<(string, string, string)>? files = null;
        (string Name, string Module)? file = null;
        var body = new StringBuilder();
        var index = -1;

        void EndFile() {
            if (file is { } open) {
                files!.Add((open.Name, open.Module, body.ToString()));
                file = null;
                body.Clear();
            }
        }

        void EndRun() {
            EndFile();
            if (index >= 0) {
                runs[index] = new(messages!, files!);
            }
        }

        var lines = text.Split('\n');
        // print ends each line with a break: the last, empty, piece is not a line.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (var i = 0; i < count; i++) {
            var line = lines[i];
            if (line.Length > 0 && line[0] == RecordStart) {
                var parts = line[1..].Split(FieldSeparator, 3);
                string At(int n) => parts.Length > n ? parts[n] : "";
                switch (parts[0]) {
                    case "run":
                        EndRun();
                        index = int.Parse(At(1), CultureInfo.InvariantCulture);
                        (messages, files) = ([], []);
                        continue;
                    case "error" or "warning":
                        EndFile();
                        messages?.Add(new(parts[0] == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, At(1), At(2)));
                        continue;
                    case "file":
                        EndFile();
                        file = (At(1), At(2));
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

    /// <summary>A run's files, checked and made ready to compile, and its messages reported where they point.</summary>
    static IEnumerable<GeneratedFile> Collect(
        GenerationInput input,
        Request request,
        RunOutput output,
        Dictionary<(string Generator, string Name), Request> names,
        HashSet<string> modules,
        List<(Diagnostic, SyntaxTree)> diagnostics
    ) {
        var generator = request.Generator.Name;
        foreach (var message in output.Messages) {
            var (tree, span) = Place(request, message.Location);
            diagnostics.Add((message.Severity == DiagnosticSeverity.Error
                ? Diagnostic.Error(ErrorCode.ERR_GeneratorReported, span, $"{generator}: {message.Text}")
                : Diagnostic.Warning(ErrorCode.WRN_GeneratorReported, span, $"{generator}: {message.Text}"), tree));
        }

        var (atTree, at) = DefaultPlace(request);
        var home = Home(input, request);
        foreach (var (name, module, text) in output.Files) {
            void Invalid(string why) =>
                diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_GeneratedFileInvalid, at, $"the generator '{generator}' wrote {why}"), atTree));

            if (FileNameProblem(name) is { } problem) {
                Invalid($"a file called '{name}': {problem}");
                continue;
            }

            if (names.TryGetValue((generator, name), out var first)) {
                Invalid($"'{name}' for {first.Subject} already, in package '{input.Package}': each file it writes needs a name of its own, such as one with the type's name in it");
                continue;
            }

            if (home.Tree is null) {
                Invalid($"'{name}' for a package with no file that declares a module: there is no module to put it in");
                continue;
            }

            var parsed = SyntaxTree.Parse(new SourceText(text, name));
            if (parsed.Root is CompilationUnitSyntax { Module: not null }) {
                Invalid($"a 'module' line in '{name}': the compiler puts the file in a module itself; to choose it, say 'inModule:'");
                continue;
            }

            if (module.Length > 0 && !modules.Contains(module)) {
                Invalid($"'{name}' in module '{module}', which package '{input.Package}' does not declare: a generated file goes in one of the package's own modules");
                continue;
            }

            names[(generator, name)] = request;
            yield return new(home.Tree, generator, name, WithHeader(request, home.Unit!, module, generator, text, parsed));
        }
    }

    /// <summary>
    ///     The file a run's output goes beside: its first declaration's, or,
    ///     with none, the package's first file that declares a module.
    /// </summary>
    static (SyntaxTree? Tree, CompilationUnitSyntax? Unit) Home(GenerationInput input, Request request) {
        if (request.Items.Count > 0) {
            return (request.Items[0].Tree, request.Items[0].Unit);
        }

        foreach (var file in input.Files) {
            if (file.Root is CompilationUnitSyntax { Module: not null } unit) {
                return (file, unit);
            }
        }

        return (null, null);
    }

    static string? FileNameProblem(string name) =>
        name.Length == 0 ? "a file needs a name"
        : name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal) ? "a name is one file's, without '/', '\\' or '..'"
        : !name.EndsWith(".sb", StringComparison.Ordinal) ? "a source file's name ends in '.sb'"
        : name.Any(static c => char.IsControl(c)) ? "a name cannot have control characters in it"
        : null;

    /// <summary>
    ///     The file as it is compiled: in the module asked for, or the one of
    ///     the file it goes beside, with that file's imports when it is in its
    ///     module, but those it makes itself.
    /// </summary>
    static string WithHeader(Request request, CompilationUnitSyntax home, string module, string generator, string text, SyntaxTree parsed) {
        var homeModule = home.Module?.Name.ToString().Trim() ?? "";
        var target = module.Length > 0 ? module : homeModule;
        var header = new StringBuilder();
        var about = request.Items.Count == 1 ? $"for '{request.Items[0].Name}' in {request.Items[0].Tree.Text.FileName}" : $"for {request.Subject}";
        header.Append(CultureInfo.InvariantCulture, $"// Written by the generator '{generator}' {about}. Do not edit: it is written again on every build.\n");
        if (target.Length > 0) {
            header.Append(CultureInfo.InvariantCulture, $"module {target}\n");
        }

        if (target == homeModule) {
            var own = parsed.Root is CompilationUnitSyntax unit
                ? unit.Imports.Select(static i => i.Name.ToString().Trim()).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var import in home.Imports.Where(i => !own.Contains(i.Name.ToString().Trim()))) {
                header.Append(import.ToString().Trim()).Append('\n');
            }
        }

        return header.Append('\n').Append(text).ToString();
    }
}
