using System.Text;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Repl;

/// <summary>What one entry produced: text to show, and whether it failed or ended the session.</summary>
public sealed record ReplResult(string Output, bool Failed = false, bool Quit = false);

/// <summary>
///     The state and logic of <c>sable repl</c>, apart from the console.
/// </summary>
/// <remarks>
///     <para>
///         Each entry is a declaration, a statement or an expression, run by
///         the SIL interpreter. An entry's statements become the body of a
///         function of their own, and each binding at its top level —
///         <c>let x = …</c> — a global of the session's program, set up where
///         the binding stood. The session keeps the imports, the declarations,
///         those globals and the entries' functions, and compiles each new
///         entry with them. An expression's value is bound to <c>resN</c>,
///         which later entries can use, and shown with its type.
///     </para>
///     <para>
///         What earlier entries computed is not computed again: the
///         interpreter's heap and globals are carried from one entry's program
///         to the next (<see cref="SilInterpreter.Session" />), and a global
///         set up once keeps its value. So an entry's effects — a line
///         printed, a file written, the clock read — happen once. The earlier
///         entries' functions are compiled with each new one, so that a
///         closure an entry made, kept in a global, still has its code.
///     </para>
///     <para>
///         An entry that fails leaves nothing behind in the session: not its
///         statements, nor the globals it bound. What it changed in globals
///         it found stays changed, as it would in a running program.
///     </para>
///     <para>
///         Natively a program has the whole stack and as long as it likes;
///         here the interpreter's own limits are raised to match: calls as
///         deep as an eight-megabyte stack allows, and no limit on steps —
///         Ctrl-C (<see cref="Interrupt" />) stops a runaway entry instead.
///     </para>
/// </remarks>
public sealed class ReplSession(IReadOnlyList<SyntaxTree>? package = null) {
    /// <summary>
    ///     The package the session was started in, with its dependencies
    ///     (<c>sable repl</c> in a package): compiled with every entry, so that
    ///     its modules can be imported as the library's are.
    /// </summary>
    readonly IReadOnlyList<SyntaxTree> _package = package ?? [];

    readonly HashSet<string> _packageModules = [
        ..(package ?? []).Select(static t => t.Root is CompilationUnitSyntax { Module: { } module } ? module.Name.ToString().Trim() : null).OfType<string>(),
    ];

    readonly List<string> _imports = [];
    readonly List<(HashSet<string> Names, string Text)> _declarations = [];
    /// <summary>The session's bindings, as the globals they became: <c>let x: Int = 1</c>, in the order bound.</summary>
    readonly List<(string Name, string Text)> _bindings = [];

    /// <summary>The entries that ran, each a function: <c>fn __repl0() { … }</c>.</summary>
    readonly List<string> _entries = [];

    /// <summary>What the entries' programs have made and set up, carried from one to the next.</summary>
    SilInterpreter.Session _session = new();

    int _results;

    /// <summary>The name of the function the next entry runs as.</summary>
    string EntryName => $"__repl{_entries.Count}";

    /// <summary>
    ///     How deep calls nest before an entry traps: about what a native
    ///     program's eight-megabyte stack holds. The interpreter's thread has
    ///     room for it.
    /// </summary>
    public const int DepthLimit = 100_000;

    /// <summary>The entry running now, to interrupt; null between entries.</summary>
    CancellationTokenSource? _running;

    /// <summary>How many instructions the last entry's program carried out: what running it cost.</summary>
    public long LastSteps { get; private set; }

    /// <summary>Stops the entry that is running, if one is, with a trap: what Ctrl-C does.</summary>
    /// <returns>Whether an entry was running.</returns>
    public bool Interrupt() {
        if (Volatile.Read(ref _running) is not { } running) {
            return false;
        }

        running.Cancel();
        return true;
    }

    /// <summary>Entries kept so far, for <c>:history</c>-like inspection in tests.</summary>
    public int StatementCount => _entries.Count;

    public static string Help => """
        Enter a declaration, a statement or an expression; an expression's value
        is shown with its type and kept as res0, res1, ...

          :type <expr>   show an expression's type without running it
          :reset         forget everything entered so far
          :help          this text
          :quit          leave (or end of input)

        'import Sable.Collections' brings in a library module. An entry may span
        lines: it continues while a bracket is open. Up and down recall earlier
        lines, Tab completes a name, and Ctrl-C stops an entry that runs too long.
        """;

    /// <summary>
    ///     What a name being typed could become, for Tab: the commands after
    ///     a colon; otherwise the keywords, the core's common names, and what
    ///     the session has declared, bound and imported.
    /// </summary>
    public IReadOnlyList<string> Completions(string prefix) {
        if (prefix.StartsWith(':')) {
            return [":type", ":reset", ":help", ":quit"];
        }

        var names = new List<string>(SyntaxFacts.KeywordTexts) { "Int", "String", "Bool", "F64", "Char", "print", "Runtime", "Reflect" };
        names.AddRange(_imports);
        names.AddRange(_declarations.SelectMany(static d => d.Names));
        names.AddRange(_bindings.Select(static b => b.Name));
        return [..names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>True when an entry's brackets and strings are closed, so it can be submitted.</summary>
    public static bool IsComplete(string text) {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (inString) {
                if (c == '\\') {
                    i++;
                } else if (c == '"') {
                    inString = false;
                }

                continue;
            }

            switch (c) {
                case '"': inString = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    while (i < text.Length && text[i] != '\n') {
                        i++;
                    }

                    break;
            }
        }

        return depth <= 0 && !inString;
    }

    public ReplResult Submit(string input) {
        var entry = input.Trim();
        if (entry.Length == 0) {
            return new("");
        }

        if (entry.StartsWith(':')) {
            return Command(entry);
        }

        var asFile = SyntaxTree.Parse(new SourceText("module Repl\n" + entry, "repl"));
        if (asFile.Root is CompilationUnitSyntax unit && asFile.Diagnostics.Count == 0) {
            if (unit.Imports.Count > 0 && unit.Members.Count == 0) {
                return Import(unit.Imports);
            }

            if (unit.Members.Count > 0 && unit.Members.All(static m => m is DeclarationSyntax and not (FieldDeclarationSyntax or ConstDeclarationSyntax))) {
                return Declare(entry, unit.Members);
            }
        }

        return Evaluate(entry, run: true);
    }

    ReplResult Command(string entry) {
        var space = entry.IndexOf(' ', StringComparison.Ordinal);
        var name = space < 0 ? entry : entry[..space];
        var argument = space < 0 ? "" : entry[(space + 1)..].Trim();
        switch (name) {
            case ":quit" or ":q" or ":exit":
                return new("", Quit: true);
            case ":help" or ":h" or ":?":
                return new(Help);
            case ":reset":
                _imports.Clear();
                _declarations.Clear();
                _bindings.Clear();
                _entries.Clear();
                _session = new();
                _results = 0;
                return new("forgot everything");
            case ":type" or ":t" when argument.Length > 0:
                return Evaluate(argument, run: false);
            default:
                return new($"no command '{name}'; ':help' lists them", Failed: true);
        }
    }

    ReplResult Import(SyntaxList<ImportDeclarationSyntax> imports) {
        var added = new List<string>();
        foreach (var import in imports) {
            var module = import.Name.ToString().Trim();
            if (module != Library.Core && !Library.ModuleNames.Contains(module) && !_packageModules.Contains(module)) {
                return new($"no library module '{module}'; there are {string.Join(", ", Library.ModuleNames.Concat(_packageModules).Order(StringComparer.Ordinal))}", Failed: true);
            }

            if (!_imports.Contains(module)) {
                _imports.Add(module);
                added.Add(module);
            }
        }

        return new(added.Count == 0 ? "already imported" : $"imported {string.Join(", ", added)}");
    }

    ReplResult Declare(string entry, SyntaxList<SyntaxNode> members) {
        var names = members.Select(NameOf).OfType<string>().ToHashSet(StringComparer.Ordinal);

        // Declaring a name again replaces what it named.
        var kept = _declarations.Where(d => !d.Names.Overlaps(names)).ToList();
        var program = Program(kept.Select(static d => d.Text).Append(entry), _bindings.Select(static b => b.Text), null, out var entryStart);
        var (_, _, errors) = Check(program, entryStart, entry.Length);
        if (errors is not null) {
            return errors;
        }

        _declarations.Clear();
        _declarations.AddRange(kept);
        _declarations.Add((names, entry));
        return new(names.Count == 0 ? "" : $"defined {string.Join(", ", names.Order(StringComparer.Ordinal))}");
    }

    static string? NameOf(SyntaxNode declaration) =>
        declaration.GetType().GetProperty("Identifier")?.GetValue(declaration) is SyntaxToken { IsNone: false } identifier
            ? identifier.Text
            : null;

    /// <summary>
    ///     Runs an entry as an expression if it is one with a value, binding
    ///     it to the next <c>resN</c>; otherwise as a statement. With
    ///     <paramref name="run" /> false, only says the expression's type.
    /// </summary>
    ReplResult Evaluate(string entry, bool run) {
        var result = $"res{_results}";
        var binding = $"let {result} = (\n{entry}\n)";
        var program = Program(_declarations.Select(static d => d.Text), _bindings.Select(static b => b.Text), binding, out var start);
        var (compilation, tree, errors) = Check(program, start, binding.Length);
        if (errors is null && TypeOfResult(compilation!, tree!) is { } type && type != compilation!.Core.Void) {
            var display = type.ToDisplayString();
            if (!run) {
                return new(display);
            }

            var outcome = Execute($"{binding}\nprint(Reflect.describe({result}))");
            if (outcome.Failed) {
                return outcome;
            }

            _results++;
            var lines = outcome.Output.TrimEnd('\n').Split('\n');
            var printed = string.Join("", lines[..^1].Select(static l => l + "\n"));
            return new($"{printed}{result}: {display} = {lines[^1]}");
        }

        if (!run) {
            return errors ?? new("Void");
        }

        var ran = Execute(entry);
        return ran with { Output = ran.Output.TrimEnd('\n') };
    }

    /// <summary>The type the entry's <c>let resN = (…)</c>, the first statement of its function, bound to.</summary>
    TypeSymbol? TypeOfResult(Compilation compilation, SyntaxTree tree) {
        if (EntryBody(tree)?.Statements.FirstOrDefault() is not LocalDeclarationStatementSyntax { Declarator.Initializer: { } initializer }) {
            return null;
        }

        return compilation.GetSemanticModel(tree).GetTypeOf(initializer.Value);
    }

    /// <summary>The body of the function the new entry runs as.</summary>
    BlockSyntax? EntryBody(SyntaxTree tree) =>
        tree.Root.DescendantNodes().OfType<FunctionDeclarationSyntax>().LastOrDefault(f => f.Identifier.Text == EntryName)?.Body;

    /// <summary>
    ///     Type-checks an entry's statements, makes the bindings at their top
    ///     level globals, and runs them: kept in the session if they run to
    ///     the end.
    /// </summary>
    ReplResult Execute(string entry) {
        var program = Program(_declarations.Select(static d => d.Text), _bindings.Select(static b => b.Text), entry, out var start);
        var (compilation, tree, errors) = Check(program, start, entry.Length);
        if (errors is not null) {
            return errors;
        }

        // Each binding at the top level becomes a global, its type written
        // out (a global's is not inferred from a call), set up where the
        // binding was: 'let _ = x' reaches it, and reaching it sets it up.
        var model = compilation!.GetSemanticModel(tree!);
        var globals = new List<(string Name, string Text)>();
        var body = new StringBuilder();
        var copied = start;
        foreach (var statement in EntryBody(tree!)!.Statements) {
            if (statement is not LocalDeclarationStatementSyntax {
                    AsyncKeyword.IsNone: true, UsingKeyword.IsNone: true,
                    Declarator: { Pattern: BindingPatternSyntax { Identifier: var identifier }, Initializer: { } initializer } declarator,
                } local
                || model.GetSymbolOfToken(identifier) is not LocalSymbol symbol) {
                continue;
            }

            var name = identifier.Text;
            if (_bindings.Any(b => b.Name == name)) {
                var line = tree!.Text.GetLinePosition(local.Span.Start);
                return new($"error[S2000]: '{name}' is already declared in this session\n  {tree.Text.GetLineText(line.Line)}\n  {new string(' ', Math.Max(0, line.Column - 1))}^", Failed: true);
            }

            var typeText = declarator.TypeAnnotation is { } annotation ? annotation.Type.ToString().Trim() : symbol.Type.ToDisplayString();
            globals.Add((name, $"{local.LetOrVarKeyword.Text} {name}: {typeText} = {initializer.Value.ToString().Trim()}"));
            body.Append(program, copied, local.Span.Start - copied).Append("let _ = ").Append(name);
            copied = local.Span.End;
        }

        body.Append(program, copied, start + entry.Length - copied);
        var rewritten = body.ToString();
        var withGlobals = Program(_declarations.Select(static d => d.Text), _bindings.Concat(globals).Select(static b => b.Text), rewritten, out _);
        if (globals.Count > 0 && Check(withGlobals, -1, 0).Errors is not null) {
            // What a binding's value needs may not be had outside the entry
            // — a name the entry bound in a 'guard', say: the entry runs as
            // written, and its bindings last only while it does.
            globals.Clear();
            rewritten = entry;
            withGlobals = program;
        }

        var ran = Run(withGlobals);
        if (!ran.Failed) {
            _bindings.AddRange(globals);
            _entries.Add($"fn {EntryName}() {{\n{rewritten}\n}}");
        }

        return ran;
    }

    // ---- Programs ------------------------------------------------------------

    /// <summary>
    ///     The program an entry runs as, and where the entry starts in it: the
    ///     imports, declarations and bindings, the earlier entries' functions,
    ///     the entry's own, and a main that calls it.
    /// </summary>
    string Program(IEnumerable<string> declarations, IEnumerable<string> bindings, string? entry, out int entryStart) {
        var text = new StringBuilder("module Repl\n");
        foreach (var import in _imports) {
            text.Append("import ").Append(import).Append('\n');
        }

        text.Append('\n');
        entryStart = -1;
        foreach (var declaration in declarations) {
            entryStart = text.Length;
            text.Append(declaration).Append("\n\n");
        }

        foreach (var binding in bindings) {
            text.Append(binding).Append("\n\n");
        }

        foreach (var earlier in _entries) {
            text.Append(earlier).Append("\n\n");
        }

        text.Append("fn ").Append(EntryName).Append("() {\n");
        if (entry is not null) {
            entryStart = text.Length;
            text.Append(entry).Append('\n');
        }

        text.Append("}\n\n@main\nfn main() {\n    ").Append(EntryName).Append("()\n}\n");
        return text.ToString();
    }

    /// <summary>
    ///     Compiles a program; its errors, if it has any, as a result to show,
    ///     each placed in the entry when it falls inside it.
    /// </summary>
    (Compilation? Compilation, SyntaxTree? Tree, ReplResult? Errors) Check(string program, int entryStart, int entryLength) {
        var tree = SyntaxTree.Parse(new SourceText(program, "repl"));
        var compilation = Compile(tree);
        var errors = compilation.GetAllDiagnosticsByFile()
            .Where(d => d.Diagnostic.Severity == DiagnosticSeverity.Error && (d.Tree is null || d.Tree == tree))
            .Select(static d => d.Diagnostic)
            .DistinctBy(static d => (d.Id, d.Message))
            .ToList();
        if (errors.Count == 0) {
            return (compilation, tree, null);
        }

        var text = new StringBuilder();
        foreach (var error in errors.OrderBy(static d => d.Span.Start)) {
            if (text.Length > 0) {
                text.Append('\n');
            }

            text.Append($"error[{error.Id}]: {error.Message}");
            var offset = error.Span.Start - entryStart;
            if (entryStart >= 0 && offset >= 0 && offset <= entryLength) {
                var line = tree.Text.GetLinePosition(error.Span.Start);
                var lineText = tree.Text.GetLineText(line.Line);
                text.Append($"\n  {lineText}\n  {new string(' ', Math.Max(0, line.Column - 1))}^");
            }
        }

        return (compilation, tree, new(text.ToString(), Failed: true));
    }

    /// <summary>The session's program with the package it is in, and the library modules either imports.</summary>
    Compilation Compile(SyntaxTree tree) => Compilation.Create([tree, .._package, ..Library.For([tree, .._package])]);

    /// <summary>
    ///     Lowers and interprets a program that type-checks, going on from
    ///     what the session's earlier programs left; its output.
    /// </summary>
    ReplResult Run(string program) {
        // The globals it sets up are kept only if it succeeds, as its bindings are.
        var before = _session.GlobalNames.ToHashSet(StringComparer.Ordinal);
        var result = RunInSession(program);
        if (result.Failed) {
            _session.Forget(before);
        }

        return result;
    }

    ReplResult RunInSession(string program) {
        var tree = SyntaxTree.Parse(new SourceText(program, "repl"));
        var compilation = Compile(tree);
        var (module, lowering) = SilGen.LowerForSession(compilation);
        if (lowering.Any(static d => d.Severity == DiagnosticSeverity.Error)) {
            return new(string.Join("\n", lowering.Select(static d => $"error[{d.Id}]: {d.Message}")), Failed: true);
        }

        if (SilVerifier.Verify(module) is { Count: > 0 } problems) {
            return new($"internal error: SIL verification failed: {problems[0]}", Failed: true);
        }

        try {
            SilPassManager.Run(module, SilPassManager.Default);
        } catch (SilPassException failure) {
            return new($"internal error: {failure.Message}", Failed: true);
        }

        using var running = new CancellationTokenSource();
        var interpreter = new SilInterpreter(module, long.MaxValue, DepthLimit) { Carried = _session, Interrupt = running.Token };
        Volatile.Write(ref _running, running);
        try {
            interpreter.Run(module.EntryPoint!);
        } catch (SilTrap trap) {
            return new($"{interpreter.Output}trap: {trap.Message}", Failed: true);
        } finally {
            Volatile.Write(ref _running, null);
            LastSteps = interpreter.Steps;
        }

        return new(interpreter.Output.ToString());
    }
}
