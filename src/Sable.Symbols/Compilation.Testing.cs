using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>Which harness a function is for: <c>@test</c>'s, <c>@bench</c>'s or <c>@fuzz</c>'s.</summary>
public enum TestKind {
    Test,
    Bench,
    Fuzz,
}

/// <summary>
///     A function marked <c>@test</c>, <c>@bench</c> or <c>@fuzz</c> that the
///     harness can call, and how.
/// </summary>
/// <param name="CallPath">
///     What names it from a file of its own module: the function's name, or
///     its type's path and its name for a static method (<c>Suite.checks</c>).
/// </param>
/// <param name="Throws">Whether it is declared to throw: an error it throws fails the test.</param>
/// <param name="TakesBencher">A benchmark that takes the <c>Bencher</c> and calls <c>iterate</c> itself.</param>
public sealed partial record TestFunction(
    FunctionSymbol Symbol,
    FunctionDeclarationSyntax Syntax,
    SyntaxTree Tree,
    TestKind Kind,
    string Module,
    string CallPath,
    bool Throws,
    bool TakesBencher) {
    /// <summary>An async test: the harness awaits it.</summary>
    public bool IsAsync => Symbol.IsAsync;

    /// <summary>
    ///     <c>@test(traps)</c>: null for a test that must not trap; for one
    ///     that must, what the trap's message must contain — empty for any.
    /// </summary>
    public string? ExpectedTrap { get; init; }

    /// <summary><c>@test(tags: ["slow"])</c>: what a filter may ask for it by.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary><c>@test(timeout: .seconds(5))</c>: how long it may run; null for as long as it takes.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    ///     <c>@test(arguments: [1, 2])</c>: the arrays its cases are made
    ///     from, as they are written — one of what it is called with, a tuple
    ///     for several parameters; or one for each parameter, every value of
    ///     one with every value of the others. Empty for a test that takes nothing.
    /// </summary>
    public IReadOnlyList<ExpressionSyntax> Arguments { get; init; } = [];

    /// <summary>The parameters <c>arguments:</c> gives values for: all but a benchmark's <c>Bencher</c>, which comes first.</summary>
    public IReadOnlyList<ParameterSymbol> ArgumentParameters => TakesBencher ? [..Symbol.Parameters.Skip(1)] : Symbol.Parameters;

    /// <summary>As the harness lists and filters it: <c>Module.function</c>, or <c>Module.Type.function</c>.</summary>
    public string QualifiedName => Module.Length == 0 ? CallPath : $"{Module}.{CallPath}";

    /// <summary>Where it is declared: <c>file:line</c>.</summary>
    public string Location => $"{Tree.Text.FileName}:{Tree.Text.GetLinePosition(Syntax.Identifier.Span.Start).Line}";
}

/// <summary>
///     <c>@test</c>, <c>@bench</c> and <c>@fuzz</c> (docs/05 §5.1): the
///     functions <c>sable test</c>, <c>sable bench</c> and <c>sable fuzz</c>
///     collect into a harness.
/// </summary>
/// <remarks>
///     <para>
///         A test takes nothing and gives nothing back; it may throw, and an
///         error it throws fails it. One given <c>arguments:</c>, arrays, takes
///         parameters, and is a test for each value. A benchmark takes nothing, and its whole
///         body is timed, or takes a <c>Sable.Testing.Bencher</c>, and times
///         what it hands the bencher's <c>iterate</c>; given <c>arguments:</c>,
///         it takes parameters for them after that. Either is a function of
///         a module or a static method of a type that is not generic, so that
///         the harness has one thing to call, and is not generic; either may
///         be async, and is awaited.
///     </para>
///     <para>
///         A fuzz target takes one <c>U8[:]</c>, the fuzzer's input, or values
///         made of it, of types that conform to <c>Sable.Testing.Arbitrary</c>
///         (<see cref="CheckFuzzTarget" />), and gives nothing back; it fails by trapping — a failed assertion, an index
///         out of range, a <c>fatalError</c> — or by throwing. It is called
///         from C, once an input, so it is not async; like the others it is a
///         function of a module or a static method of a type that is not
///         generic, and is not generic itself.
///     </para>
///     <para>
///         <c>@test(traps)</c> marks a test that passes by trapping — an index
///         out of range, an overflow, a <c>fatal</c> — and fails if it returns;
///         <c>@test(traps: "out of range")</c> also wants the trap's message
///         to contain that. A failed assertion is not the trap: it fails it.
///     </para>
///     <para>
///         In an ordinary build they are functions like any other. The
///         checks are made in every build, so that a malformed test is found
///         when it is written rather than when the tests are first run.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    const string BenchTakes = "'@bench' takes nothing, or 'arguments: [...]', the values it is run with, and 'tags: [\"text\"]'";
    const string TestAttribute = "test", BenchAttribute = "bench", FuzzAttribute = "fuzz";

    /// <summary>Every well-formed <c>@test</c> (or <c>@bench</c>) function, in file and source order.</summary>
    public IReadOnlyList<TestFunction> GetTestFunctions(TestKind kind) {
        var found = new List<TestFunction>();
        foreach (var tree in SyntaxTrees) {
            if (!HasFileScope(tree)) {
                continue;
            }

            foreach (var declaration in tree.Root.DescendantNodes().OfType<FunctionDeclarationSyntax>()) {
                if (KindOf(declaration) == kind && Examine(declaration, tree, null) is { } test) {
                    found.Add(test);
                }
            }
        }

        return found;
    }

    /// <summary>What is wrong with the file's <c>@test</c>, <c>@bench</c> and <c>@fuzz</c> functions; its semantic model reports these.</summary>
    /// <param name="arguments">Filled with the arrays of the tests' <c>arguments:</c>, each with its type and where it is bound, for the model to bind.</param>
    internal IReadOnlyList<Diagnostic> CheckTestFunctions(SyntaxTree tree, List<(ExpressionSyntax Array, TypeSymbol Type, Symbol Owner)> arguments) {
        var diagnostics = new List<Diagnostic>();
        foreach (var declaration in tree.Root.DescendantNodes().OfType<FunctionDeclarationSyntax>()) {
            if (KindOf(declaration) is not null && Examine(declaration, tree, diagnostics) is { Arguments.Count: > 0 } test) {
                var parameters = test.ArgumentParameters;
                for (var i = 0; i < test.Arguments.Count; i++) {
                    var element = test.Arguments.Count == 1 && parameters.Count > 1
                        ? new TupleTypeSymbol([..parameters.Select(static p => p.Type)], [..parameters.Select(static _ => (string?)null)])
                        : parameters[i].Type;
                    arguments.Add((test.Arguments[i], new ArrayTypeSymbol(element, ArrayKind.Grow), test.Symbol.ContainingSymbol!));
                }
            }
        }

        return diagnostics;
    }

    static TestKind? KindOf(FunctionDeclarationSyntax declaration) {
        foreach (var attribute in declaration.Attributes) {
            switch (attribute.Name.ToString().Trim()) {
                case TestAttribute: return TestKind.Test;
                case BenchAttribute: return TestKind.Bench;
                case FuzzAttribute: return TestKind.Fuzz;
            }
        }

        return null;
    }

    /// <summary>The test the declaration is, or null, reporting why not when there is somewhere to.</summary>
    TestFunction? Examine(FunctionDeclarationSyntax declaration, SyntaxTree tree, List<Diagnostic>? diagnostics) {
        var attributes = declaration.Attributes.Where(static a => a.Name.ToString().Trim() is TestAttribute or BenchAttribute or FuzzAttribute).ToList();
        var first = attributes[0];
        var kind = first.Name.ToString().Trim() switch {
            TestAttribute => TestKind.Test,
            BenchAttribute => TestKind.Bench,
            _ => TestKind.Fuzz,
        };
        var marker = $"'@{first.Name.ToString().Trim()}'";
        var ok = true;

        void Report(ErrorCode code, TextSpan span, string message) {
            ok = false;
            diagnostics?.Add(Diagnostic.Error(code, span, message));
        }

        if (attributes.Select(static a => a.Name.ToString().Trim()).Distinct().Count() > 1) {
            Report(ErrorCode.ERR_TestAndBench, attributes[1].Span, "a function is one of a test, a benchmark and a fuzz target, not more");
        }

        var options = new TestOptions();
        string? mutator = null;
        foreach (var attribute in attributes.Where(static a => a.ArgumentList is not null)) {
            if (attribute.Name.ToString().Trim() == FuzzAttribute) {
                mutator = FuzzMutator(attribute, tree);
                if (mutator is null) {
                    Report(ErrorCode.ERR_TestAttributeArguments, attribute.ArgumentList!.Span, MutatorExpected);
                }
            } else if (attribute.Name.ToString().Trim() is BenchAttribute) {
                if (options.Read(attribute) is { } wrong) {
                    Report(ErrorCode.ERR_TestAttributeArguments, wrong.Span, BenchTakes);
                } else if (options.Trap is not null || options.Timeout is not null) {
                    Report(ErrorCode.ERR_TestAttributeArguments, attribute.ArgumentList!.Span, BenchTakes);
                }
            } else if (options.Read(attribute) is { } bad) {
                Report(ErrorCode.ERR_TestAttributeArguments, bad.Span,
                    "'@test' takes nothing, or 'traps' for a test that must trap, or 'traps: \"text\"' for one whose trap must say that; "
                    + "'arguments: [...]', the values it is run with; 'tags: [\"text\"]'; and 'timeout: .seconds(5)'");
            }
        }

        if (GetDeclaredSymbol(declaration) is not FunctionSymbol function || function.ContainingSymbol is FunctionSymbol) {
            Report(ErrorCode.ERR_TestIsLocal, first.Span, $"{marker} marks a function of a module or a static method; the harness cannot reach a function declared inside a body");
            return null;
        }

        if (declaration.TypeParameters is not null) {
            Report(ErrorCode.ERR_TestIsGeneric, declaration.TypeParameters.Span, $"a {Noun(kind)} cannot be generic: the harness calls it with no type arguments to give");
        }

        if (function.ReturnType is var returned && !ReferenceEquals(returned, Core.Void) && returned is not ErrorTypeSymbol) {
            Report(ErrorCode.ERR_TestReturnsValue, declaration.ReturnClause?.Span ?? declaration.Identifier.Span, $"a {Noun(kind)} returns nothing: it fails by failing an assertion{(kind == TestKind.Bench ? "" : " or throwing")}, not by what it returns");
        }

        var takesBencher = false;
        if (kind == TestKind.Test && options.Arguments.Count > 0) {
            CheckTestArguments(function.Parameters, declaration, options.Arguments, Report);
        } else if (kind == TestKind.Bench && options.Arguments.Count > 0) {
            takesBencher = function.Parameters.Length > 0 && IsBencher(function.Parameters[0].Type);
            CheckTestArguments(takesBencher ? function.Parameters.RemoveAt(0) : function.Parameters, declaration, options.Arguments, Report);
        } else if (kind == TestKind.Test) {
            if (function.Parameters.Length > 0) {
                Report(ErrorCode.ERR_TestTakesParameters, declaration.Parameters.Span, "a test takes no parameters: the harness has nothing to pass it, unless '@test(arguments: [...])' says what");
            }
        } else if (kind == TestKind.Fuzz) {
            CheckFuzzTarget(function, declaration, Report);
        } else if (function.Parameters.Length == 1 && IsBencher(function.Parameters[0].Type)) {
            takesBencher = true;
        } else if (function.Parameters.Length > 0) {
            Report(ErrorCode.ERR_BenchParameters, declaration.Parameters.Span, "a benchmark takes nothing, or one 'Bencher' (from Sable.Testing) to call 'iterate' on, unless '@bench(arguments: [...])' says what more");
        }

        if (kind == TestKind.Bench && function.ThrownType is not null) {
            Report(ErrorCode.ERR_BenchThrows, declaration.ThrowsSpec?.Span ?? declaration.Identifier.Span, "a benchmark cannot throw: there is no test for its error to fail");
        }

        // Where it is: a module's function, or a static method of a type the
        // harness can name.
        string callPath;
        switch (function.ContainingSymbol) {
            case ModuleSymbol:
                callPath = function.Name;
                break;

            case NamedTypeSymbol or ExtensionSymbol when !function.IsStatic:
                Report(ErrorCode.ERR_TestIsInstanceMethod, declaration.Identifier.Span, $"{marker} marks an instance method, which needs a value to call it on; make it 'static', or a function of the module");
                return null;

            case NamedTypeSymbol or ExtensionSymbol: {
                var type = function.ContainingSymbol as NamedTypeSymbol ?? ((ExtensionSymbol)function.ContainingSymbol).ExtendedType;
                if (TypePath(type) is not { } path) {
                    Report(ErrorCode.ERR_TestNotReachable, declaration.Identifier.Span, $"{marker} marks a method of a generic type, which the harness has no type arguments for");
                    return null;
                }

                if (function.DeclaredAccessibility == Accessibility.Private) {
                    Report(ErrorCode.ERR_TestNotReachable, declaration.Identifier.Span, $"{marker} marks a private method, which the harness cannot call from outside its type; make it internal");
                    return null;
                }

                callPath = $"{path}.{function.Name}";
                break;
            }

            default:
                Report(ErrorCode.ERR_TestNotReachable, declaration.Identifier.Span, $"{marker} marks a function the harness cannot reach");
                return null;
        }

        if (declaration.Body is null && declaration.ExpressionBody is null) {
            Report(ErrorCode.ERR_TestNotReachable, declaration.Identifier.Span, $"a {Noun(kind)} needs a body");
        }

        return ok
            ? new(function, declaration, tree, kind, GetFileScope(tree).Module.Name, callPath, function.ThrownType is not null, takesBencher) {
                ExpectedTrap = options.Trap, Tags = options.Tags, Timeout = options.Timeout, Arguments = options.Arguments, Mutator = mutator,
            }
            : null;
    }

    /// <summary>
    ///     That there are as many arrays to make a test's cases from as it
    ///     takes: one for one parameter, one of tuples for several, or one for
    ///     each. Whether they are of what it takes is found as the file's
    ///     semantic model binds them, where the test is declared.
    /// </summary>
    static void CheckTestArguments(ImmutableArray<ParameterSymbol> parameters, FunctionDeclarationSyntax declaration, IReadOnlyList<ExpressionSyntax> arguments, Action<ErrorCode, TextSpan, string> report) {
        if (parameters.Any(static p => p.Modifier is not ParameterModifier.None || p.IsVariadic || p.IsAutoclosure || p.IsByReference)) {
            report(ErrorCode.ERR_TestTakesParameters, declaration.Parameters.Span, "its parameters are plain ones, each given a value of 'arguments: [...]'");
            return;
        }

        if (parameters.IsEmpty || arguments.Count != 1 && arguments.Count != parameters.Length) {
            report(
                ErrorCode.ERR_TestTakesParameters,
                declaration.Parameters.Span,
                parameters.IsEmpty
                    ? "what is given 'arguments:' takes a parameter for them"
                    : $"'arguments:' is one array, of {(parameters.Length == 1 ? "what it takes" : $"tuples of the {parameters.Length} values it takes")}"
                      + $"{(parameters.Length == 1 ? "" : $", or {parameters.Length} arrays, one for each")}; here are {arguments.Count}"
            );
        }
    }

    /// <summary>What <c>@test(…)</c> says of a test.</summary>
    sealed class TestOptions {
        public string? Trap { get; private set; }
        public List<string> Tags { get; } = [];
        public TimeSpan? Timeout { get; private set; }
        public List<ExpressionSyntax> Arguments { get; } = [];

        /// <summary>Reads the attribute's arguments; gives the one that is not one '@test' takes, or null.</summary>
        public SyntaxNode? Read(AttributeSyntax attribute) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var inArguments = false;
            if (attribute.ArgumentList!.Arguments.Count == 0) {
                return attribute.ArgumentList;
            }

            foreach (var argument in attribute.ArgumentList.Arguments) {
                var name = argument.NameToken.IsNone ? "" : argument.NameToken.Text;
                if (name.Length == 0) {
                    // 'traps' alone, as a name; or another array of arguments.
                    if (argument.Expression.ToString().Trim() == "traps" && seen.Add("traps")) {
                        Trap = "";
                        inArguments = false;
                    } else if (inArguments) {
                        Arguments.Add(argument.Expression);
                    } else {
                        return argument;
                    }

                    continue;
                }

                inArguments = name == "arguments";
                if (!seen.Add(name)) {
                    return argument;
                }

                switch (name) {
                    case "traps" when Text(argument.Expression) is { } text:
                        Trap = text;
                        break;
                    case "tags" when argument.Expression is ArrayExpressionSyntax array && array.Elements.All(static e => Text(e) is { } tag && IsTag(tag)):
                        Tags.AddRange(array.Elements.Select(static e => Text(e)!));
                        break;
                    case "timeout" when Duration(argument.Expression) is { } timeout:
                        Timeout = timeout;
                        break;
                    case "arguments":
                        Arguments.Add(argument.Expression);
                        break;
                    default:
                        return argument;
                }
            }

            return null;
        }

        /// <summary>A plain string: no interpolation, nothing to evaluate.</summary>
        static string? Text(ExpressionSyntax expression) =>
            expression is LiteralExpressionSyntax { Token.Kind: SyntaxKind.StringLiteralToken } literal
            && literal.Token.Parts.All(static p => p.Kind == StringPartKind.Literal)
                ? literal.Token.Value as string
                : null;

        /// <summary>A tag a filter can be written with: no space, nor what a filter is put together with.</summary>
        static bool IsTag(string tag) => tag.Length > 0 && tag.All(static c => !char.IsWhiteSpace(c) && !char.IsControl(c) && !"&|!()*,\"".Contains(c));

        /// <summary><c>.seconds(5)</c>, <c>.milliseconds(200)</c> or <c>.minutes(2)</c>, as Sable.Time's Duration has them, of a literal.</summary>
        static TimeSpan? Duration(ExpressionSyntax expression) {
            var parts = expression.ToString().Trim().TrimEnd(')').Split('(');
            if (parts is not [var unit, var number] || !double.TryParse(number.Replace("_", "", StringComparison.Ordinal), System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0) {
                return null;
            }

            double? seconds = unit switch {
                ".seconds" => value,
                ".milliseconds" => value / 1000,
                ".minutes" => value * 60,
                _ => null,
            };
            return seconds is { } length and < 1e9 ? TimeSpan.FromSeconds(length) : null;
        }
    }

    static string Noun(TestKind kind) => kind switch {
        TestKind.Test => "test",
        TestKind.Bench => "benchmark",
        _ => "fuzz target",
    };

    static bool IsBencher(TypeSymbol type) =>
        type is NamedTypeSymbol { Name: "Bencher" } named && named.ContainingModule?.Name == "Sable.Testing";

    /// <summary>The type's name from inside its module, through the types it is nested in; null for a generic one.</summary>
    static string? TypePath(NamedTypeSymbol type) {
        var parts = new List<string>();
        for (Symbol? here = type; here is NamedTypeSymbol named; here = named.ContainingSymbol) {
            if (!named.TypeParameters.IsEmpty) {
                return null;
            }

            parts.Insert(0, named.Name);
        }

        return string.Join('.', parts);
    }
}
