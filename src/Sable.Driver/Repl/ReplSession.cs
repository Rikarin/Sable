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
///         the SIL interpreter. State persists by replay: the session keeps
///         the imports, the declarations and every statement that ran, and an
///         entry becomes the next statement of a <c>main</c> that runs them
///         all again. What the earlier statements print is dropped, up to a
///         mark printed just before the new entry. An expression's value is
///         bound to <c>resN</c>, which later entries can use, and shown with
///         its type.
///     </para>
///     <para>
///         Replay is exact because what the program does to the world goes
///         through an <see cref="EffectJournal" />: a statement's effects —
///         a file written, a line read, the clock — are carried out when it
///         first runs, and on every replay it is given what they gave then
///         instead, so nothing happens twice and the clock reads as it did.
///         An entry that fails leaves nothing behind in the journal, as it
///         leaves nothing in the session.
///     </para>
///     <para>
///         Natively a program has the whole stack and as long as it likes;
///         here the interpreter's own limits are raised to match: calls as
///         deep as an eight-megabyte stack allows, and no limit on steps —
///         Ctrl-C (<see cref="Interrupt" />) stops a runaway entry instead.
///     </para>
/// </remarks>
public sealed class ReplSession(IReadOnlyList<SyntaxTree>? package = null) {
    const string Mark = "\u0001repl-mark\u0001";

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
    readonly List<string> _statements = [];
    readonly EffectJournal _journal = new();
    int _results;

    /// <summary>
    ///     How deep calls nest before an entry traps: about what a native
    ///     program's eight-megabyte stack holds. The interpreter's thread has
    ///     room for it.
    /// </summary>
    public const int DepthLimit = 100_000;

    /// <summary>The entry running now, to interrupt; null between entries.</summary>
    CancellationTokenSource? _running;

    /// <summary>Stops the entry that is running, if one is, with a trap: what Ctrl-C does.</summary>
    /// <returns>Whether an entry was running.</returns>
    public bool Interrupt() {
        if (Volatile.Read(ref _running) is not { } running) {
            return false;
        }

        running.Cancel();
        return true;
    }

    /// <summary>Statements and declarations kept so far, for <c>:history</c>-like inspection in tests.</summary>
    public int StatementCount => _statements.Count;

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
        names.AddRange(_statements.SelectMany(static s => System.Text.RegularExpressions.Regex.Matches(s, @"\b(?:let|var)\s+([A-Za-z_][A-Za-z0-9_]*)").Select(static m => m.Groups[1].Value)));
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
                _statements.Clear();
                _journal.Clear();
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
        var program = Program(kept.Select(static d => d.Text).Append(entry), _statements, null, out var entryStart);
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
        var program = Program(_declarations.Select(static d => d.Text), _statements, binding, out var start);
        var (compilation, tree, errors) = Check(program, start, binding.Length);
        if (errors is null && TypeOfResult(compilation!, tree!) is { } type && type != compilation!.Core.Void) {
            var display = type.ToDisplayString();
            if (!run) {
                return new(display);
            }

            var withValue = Program(_declarations.Select(static d => d.Text), _statements, $"{binding}\nprint(Reflect.describe({result}))", out _);
            var outcome = Run(withValue);
            if (outcome.Failed) {
                return outcome;
            }

            _statements.Add(binding);
            _results++;
            var lines = outcome.Output.TrimEnd('\n').Split('\n');
            var printed = string.Join("", lines[..^1].Select(static l => l + "\n"));
            return new($"{printed}{result}: {display} = {lines[^1]}");
        }

        if (!run) {
            return errors ?? new("Void");
        }

        program = Program(_declarations.Select(static d => d.Text), _statements, entry, out start);
        (_, _, errors) = Check(program, start, entry.Length);
        if (errors is not null) {
            return errors;
        }

        var ran = Run(program);
        if (!ran.Failed) {
            _statements.Add(entry);
        }

        return ran with { Output = ran.Output.TrimEnd('\n') };
    }

    /// <summary>The type the entry's <c>let resN = (…)</c>, the last statement of main, bound to.</summary>
    static TypeSymbol? TypeOfResult(Compilation compilation, SyntaxTree tree) {
        var main = tree.Root.DescendantNodes().OfType<FunctionDeclarationSyntax>().LastOrDefault(static f => f.Identifier.Text == "main");
        if (main?.Body?.Statements.LastOrDefault() is not LocalDeclarationStatementSyntax { Declarator.Initializer: { } initializer }) {
            return null;
        }

        return compilation.GetSemanticModel(tree).GetTypeOf(initializer.Value);
    }

    // ---- Programs ------------------------------------------------------------

    /// <summary>
    ///     The program an entry runs as, and where the entry starts in it:
    ///     the imports and declarations, then a main replaying the statements,
    ///     printing the mark, and running the entry.
    /// </summary>
    string Program(IEnumerable<string> declarations, IEnumerable<string> statements, string? entry, out int entryStart) {
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

        text.Append("@main\nfn main() {\n");
        foreach (var statement in statements) {
            text.Append(statement).Append('\n');
        }

        text.Append("print(\"").Append(Mark).Append("\")\n");
        if (entry is not null) {
            entryStart = text.Length;
            text.Append(entry).Append('\n');
        }

        text.Append("}\n");
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

    /// <summary>Lowers and interprets a program that type-checks; its output after the mark.</summary>
    ReplResult Run(string program) {
        // Its effects are kept only if it succeeds, as its statement is.
        var journaled = _journal.Count;
        var result = RunJournaled(program);
        if (result.Failed) {
            _journal.Truncate(journaled);
        }

        return result;
    }

    ReplResult RunJournaled(string program) {
        var tree = SyntaxTree.Parse(new SourceText(program, "repl"));
        var compilation = Compile(tree);
        var (module, lowering) = SilGen.Lower(compilation);
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
        var interpreter = new SilInterpreter(module, long.MaxValue, DepthLimit) { Journal = _journal, Interrupt = running.Token };
        Volatile.Write(ref _running, running);
        try {
            interpreter.Run(module.EntryPoint!);
        } catch (SilTrap trap) {
            return new(After(interpreter.Output.ToString()) + $"trap: {trap.Message}", Failed: true);
        } finally {
            Volatile.Write(ref _running, null);
        }

        return new(After(interpreter.Output.ToString()));
    }

    static string After(string output) {
        var mark = output.IndexOf(Mark + "\n", StringComparison.Ordinal);
        return mark < 0 ? output : output[(mark + Mark.Length + 1)..];
    }
}
