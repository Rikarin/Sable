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
public sealed record TestFunction(
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

    /// <summary>How the suite an instance method is a test of is made, one for each test; null for a function or a static method.</summary>
    public TestMaking? Suite { get; init; }

    /// <summary>The fixtures a function that is not given arguments takes.</summary>
    public IReadOnlyList<TestFixture> Fixtures { get; init; } = [];

    /// <summary>The suite's attribute's options and its own, which win.</summary>
    internal IReadOnlyList<TestOptions> Options { get; init; } = [];

    /// <summary><c>@test(retry: 3)</c>: how many more times it is tried while it fails.</summary>
    public int Retries => Options.Select(static o => o.Retries).FirstOrDefault(static r => r is not null) ?? 0;

    /// <summary><c>collection: "database"</c>: tests of one are not run at once; empty for none.</summary>
    public string Collection => Options.Select(static o => o.Collection).FirstOrDefault(static c => c is not null) ?? "";

    /// <summary><c>serialized</c>: not run while another of its suite so marked is.</summary>
    public bool IsSerialized => Options.Any(static o => o.IsSerialized);

    /// <summary><c>explicit</c>: run only when asked for.</summary>
    public bool IsExplicit => Options.Any(static o => o.IsExplicit);

    /// <summary>Why it is not to be run, and when: the suite's reasons after its own.</summary>
    public IReadOnlyList<TestSkip> Skips => [..Options.Select(static o => o.Skip).OfType<TestSkip>()];

    /// <summary>The suite's name: its type's path in its module, or the module alone.</summary>
    public string SuiteName =>
        CallPath.LastIndexOf('.') is var dot and >= 0 ? QualifiedName[..(QualifiedName.Length - CallPath.Length + dot)] : Module;

    /// <summary>As the harness lists and filters it: <c>Module.function</c>, or <c>Module.Type.function</c>.</summary>
    public string QualifiedName => Module.Length == 0 ? CallPath : $"{Module}.{CallPath}";

    /// <summary>Where it is declared: <c>file:line</c>.</summary>
    public string Location => $"{Tree.Text.FileName}:{Tree.Text.GetLinePosition(Syntax.Identifier.Span.Start).Line}";
}

/// <summary><c>skip: "why"</c>, or <c>skip(if: condition, "why")</c>.</summary>
/// <param name="Condition">As it is written, in the file of what it is written on; null for always.</param>
/// <param name="OnSuite">Whether the suite says it, rather than the test.</param>
public sealed record TestSkip(ExpressionSyntax? Condition, string Reason, bool OnSuite);

/// <summary>A parameter that a fixture (<c>Sable.Testing.Fixture</c>) is passed for.</summary>
/// <param name="Written">Its type, as it is written there.</param>
/// <param name="Making">How one is made.</param>
public sealed record TestFixture(string? Label, string Written, TestMaking Making);

/// <summary>How a suite or a fixture is made: by its initialiser, given fixtures.</summary>
/// <param name="Path">What names the type from a file of its module.</param>
/// <param name="Tree">The file it is declared in; null for the standard library's, whose initialiser takes nothing.</param>
public sealed record TestMaking(NamedTypeSymbol Type, string Module, string Path, SyntaxTree? Tree, IReadOnlyList<TestFixture> Parameters, bool IsAsync, bool Throws) {
    public string QualifiedName => Module.Length == 0 ? Path : $"{Module}.{Path}";

    /// <summary>Whether a file of any other module may name it.</summary>
    public bool IsPublic => Type.DeclaredAccessibility == Accessibility.Public;
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
///         what it hands the bencher's <c>iterate</c>. Either is a function of
///         a module or a static method of a type that is not generic, so that
///         the harness has one thing to call; neither is generic, and a
///         benchmark is not async. A test may be async, and is awaited.
///     </para>
///     <para>
///         A fuzz target takes one <c>U8[:]</c>, the fuzzer's input, and gives
///         nothing back; it fails by trapping — a failed assertion, an index
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
    const string TestAttribute = "test", BenchAttribute = "bench", FuzzAttribute = "fuzz", SuiteAttribute = "suite";

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
    /// <param name="arguments">Filled with the arrays of the tests' <c>arguments:</c>, and the conditions they are skipped on, each with its type and where it is bound, for the model to bind.</param>
    internal IReadOnlyList<Diagnostic> CheckTestFunctions(SyntaxTree tree, List<(ExpressionSyntax Array, TypeSymbol Type, Symbol Owner)> arguments) {
        var diagnostics = new List<Diagnostic>();
        foreach (var declaration in tree.Root.DescendantNodes().Where(static n => n is StructDeclarationSyntax or ClassDeclarationSyntax)) {
            if (SuiteOptions(declaration, diagnostics) is { Skip.Condition: { } condition } && GetDeclaredSymbol(declaration) is { } suite) {
                arguments.Add((condition, Core.Bool, suite));
            }
        }

        foreach (var declaration in tree.Root.DescendantNodes().OfType<FunctionDeclarationSyntax>()) {
            if (KindOf(declaration) is null || Examine(declaration, tree, diagnostics) is not { } test) {
                continue;
            }

            foreach (var skip in test.Skips) {
                if (!skip.OnSuite && skip.Condition is { } condition) {
                    arguments.Add((condition, Core.Bool, test.Symbol.ContainingSymbol!));
                }
            }

            if (test.Arguments.Count > 0) {
                var parameters = test.Symbol.Parameters;
                for (var i = 0; i < test.Arguments.Count; i++) {
                    var element = test.Arguments.Count == 1 && parameters.Length > 1
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
        foreach (var attribute in attributes.Where(static a => a.ArgumentList is not null)) {
            if (attribute.Name.ToString().Trim() is BenchAttribute or FuzzAttribute) {
                Report(ErrorCode.ERR_TestAttributeArguments, attribute.ArgumentList!.Span, $"'@{attribute.Name.ToString().Trim()}' takes no arguments");
            } else if (options.Read(attribute) is { } bad) {
                Report(ErrorCode.ERR_TestAttributeArguments, bad.Span,
                    "'@test' takes nothing, or 'traps' for a test that must trap, or 'traps: \"text\"' for one whose trap must say that; "
                    + $"'arguments: [...]', the values it is run with; and {TestOptions.Common}");
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
        IReadOnlyList<TestFixture> fixtures = [];
        TestMaking? suiteMaking = null;
        TestOptions? suiteOptions = null;
        if (kind == TestKind.Test && options.Arguments.Count > 0) {
            CheckTestArguments(function, declaration, options.Arguments, Report);
        } else if (kind == TestKind.Test) {
            fixtures = FixturesOf(function.Parameters, declaration.Parameters.Parameters.Select(static p => p.Type.ToString().Trim()), new(), out var problem);
            if (problem is not null) {
                Report(ErrorCode.ERR_TestTakesParameters, declaration.Parameters.Span, $"{problem}: a test is passed fixtures (a type that is a 'Sable.Testing.Fixture'), or what '@test(arguments: [...])' says");
            }
        } else if (kind == TestKind.Fuzz) {
            if (function.Parameters is not [{ Type: ArrayTypeSymbol { ArrayKind: ArrayKind.Slice, ElementType: var element } }] || !ReferenceEquals(element, Core.U8)) {
                Report(ErrorCode.ERR_FuzzSignature, declaration.Parameters.Span, "a fuzz target takes one 'U8[:]': the bytes the fuzzer chose");
            }

            if (function.IsAsync) {
                Report(ErrorCode.ERR_FuzzSignature, declaration.Identifier.Span, "a fuzz target is not async: C calls it once an input, and waits for nothing");
            }
        } else if (function.Parameters.Length == 1 && IsBencher(function.Parameters[0].Type)) {
            takesBencher = true;
        } else if (function.Parameters.Length > 0) {
            Report(ErrorCode.ERR_BenchParameters, declaration.Parameters.Span, "a benchmark takes nothing, or one 'Bencher' (from Sable.Testing) to call 'iterate' on");
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

            case NamedTypeSymbol or ExtensionSymbol: {
                var type = function.ContainingSymbol as NamedTypeSymbol ?? ((ExtensionSymbol)function.ContainingSymbol).ExtendedType;
                suiteOptions = type.DeclaringSyntax.Select(d => SuiteOptions(d, null)).FirstOrDefault(static o => o is not null);
                if (!function.IsStatic && (kind != TestKind.Test || suiteOptions is null)) {
                    Report(
                        ErrorCode.ERR_TestIsInstanceMethod,
                        declaration.Identifier.Span,
                        $"{marker} marks an instance method, which needs a value to call it on; make it 'static', or a function of the module{(kind == TestKind.Test ? $", or '{type.Name}' a '@suite'" : "")}"
                    );
                    return null;
                }

                if (TypePath(type) is not { } path) {
                    Report(ErrorCode.ERR_TestNotReachable, declaration.Identifier.Span, $"{marker} marks a method of a generic type, which the harness has no type arguments for");
                    return null;
                }

                if (function.DeclaredAccessibility == Accessibility.Private) {
                    Report(ErrorCode.ERR_TestNotReachable, declaration.Identifier.Span, $"{marker} marks a private method, which the harness cannot call from outside its type; make it internal");
                    return null;
                }

                if (!function.IsStatic) {
                    suiteMaking = MakingOf(type, new(), out var problem);
                    if (suiteMaking is null) {
                        Report(ErrorCode.ERR_SuiteInit, declaration.Identifier.Span, $"the suite '{type.Name}' cannot be made for its tests: {problem}");
                        return null;
                    }
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
                ExpectedTrap = options.Trap,
                Tags = [..options.Tags.Concat(suiteOptions?.Tags ?? []).Distinct()],
                Timeout = options.Timeout ?? suiteOptions?.Timeout,
                Arguments = options.Arguments,
                Suite = suiteMaking,
                Fixtures = fixtures,
                Options = suiteOptions is null ? [options] : [options, suiteOptions],
            }
            : null;
    }

    /// <summary>What <c>@suite(…)</c> says of a type; null for one that is none.</summary>
    static TestOptions? SuiteOptions(SyntaxNode declaration, List<Diagnostic>? diagnostics) {
        if (AttributesOf(declaration).FirstOrDefault(static a => AttributeName(a) == SuiteAttribute) is not { } attribute) {
            return null;
        }

        var options = new TestOptions { OnSuite = true };
        if (attribute.ArgumentList is not null && options.Read(attribute) is { } bad) {
            diagnostics?.Add(Diagnostic.Error(ErrorCode.ERR_TestAttributeArguments, bad.Span, $"'@suite' takes nothing, or {TestOptions.Common}"));
        }

        return options;
    }

    /// <summary>The fixtures passed for the parameters, with their types as they are written; why not, when they are not all fixtures.</summary>
    /// <param name="within">What is being made, each within the one before: none of them is made from itself.</param>
    IReadOnlyList<TestFixture> FixturesOf(IEnumerable<ParameterSymbol> parameters, IEnumerable<string> written, List<NamedTypeSymbol> within, out string? problem) {
        problem = null;
        var fixtures = new List<TestFixture>();
        foreach (var (parameter, type) in parameters.Zip(written)) {
            if (parameter.Modifier is not ParameterModifier.None || parameter.IsVariadic || parameter.IsAutoclosure || parameter.IsByReference) {
                problem = $"'{parameter.Name}' is not a plain parameter";
                return [];
            }

            if (FixtureOf(parameter.Type, parameter.Label == "_" ? null : parameter.Label ?? parameter.Name, type, within, out problem) is not { } fixture) {
                return [];
            }

            fixtures.Add(fixture);
        }

        return fixtures;
    }

    TestFixture? FixtureOf(TypeSymbol type, string? label, string written, List<NamedTypeSymbol> within, out string? problem) {
        problem = null;
        if (type is not NamedTypeSymbol named || GetModule("Sable.Testing")?.GetType("Fixture") is not { } protocol || !Conversions.Satisfies(named, protocol)) {
            problem = $"'{type.ToDisplayString()}' is not a fixture";
            return null;
        }

        return MakingOf(named, within, out problem) is { } making ? new(label, written, making) : null;
    }

    /// <summary>How the harness makes one of the type; null, with why not.</summary>
    TestMaking? MakingOf(NamedTypeSymbol type, List<NamedTypeSymbol> within, out string? problem) {
        problem = null;
        if (within.Contains(type)) {
            problem = $"'{type.Name}' is made from itself";
            return null;
        }

        if (TypePath(type) is not { } path || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.ContainingModule is not { } module) {
            problem = $"'{type.Name}' is not a struct or a class that is not generic";
            return null;
        }

        if (type.DeclaringSyntax.FirstOrDefault() is not { } declared || FileScopeOf(declared) is not { } scope) {
            // The library's, taken from its interface.
            return new(type, module.Name, path, null, [], false, false);
        }

        within.Add(type);
        try {
            var made = new List<TestMaking>();
            var initializers = type.GetMembers("init").OfType<FunctionSymbol>().Where(static f => f.DeclaringSyntax is [InitDeclarationSyntax]).ToList();
            foreach (var initializer in initializers) {
                var syntax = (InitDeclarationSyntax)initializer.DeclaringSyntax[0];
                var parameters = FixturesOf(initializer.Parameters, syntax.Parameters.Parameters.Select(static p => p.Type.ToString().Trim()), within, out var why);
                problem ??= why;
                if (why is null) {
                    made.Add(new(type, module.Name, path, scope.Tree, parameters, initializer.IsAsync, initializer.ThrownType is not null));
                }
            }

            if (initializers.Count == 0) {
                // The memberwise initialiser of a struct, or a class's own.
                var parameters = new List<TestFixture>();
                foreach (var field in declared.ChildNodes().OfType<FieldDeclarationSyntax>()) {
                    if (field.Accessors is not null || field.Declarator.Initializer is not null || GetDeclaredSymbol(field) is not FieldSymbol { IsStatic: false } symbol || field.Declarator.TypeAnnotation?.Type is not { } written) {
                        continue;
                    }

                    if (FixtureOf(symbol.Type, symbol.Name, written.ToString().Trim(), within, out problem) is not { } fixture) {
                        return null;
                    }

                    parameters.Add(fixture);
                }

                return new(type, module.Name, path, scope.Tree, parameters, false, false);
            }

            if (made.Count == 1) {
                problem = null;
                return made[0];
            }

            problem = made.Count == 0 ? problem : $"'{type.Name}' has several initialisers that fixtures can be passed to";
            return null;
        } finally {
            within.Remove(type);
        }
    }

    /// <summary>
    ///     That there are as many arrays to make a test's cases from as it
    ///     takes: one for one parameter, one of tuples for several, or one for
    ///     each. Whether they are of what it takes is found as the file's
    ///     semantic model binds them, where the test is declared.
    /// </summary>
    static void CheckTestArguments(FunctionSymbol function, FunctionDeclarationSyntax declaration, IReadOnlyList<ExpressionSyntax> arguments, Action<ErrorCode, TextSpan, string> report) {
        var parameters = function.Parameters;
        if (parameters.Any(static p => p.Modifier is not ParameterModifier.None || p.IsVariadic || p.IsAutoclosure || p.IsByReference)) {
            report(ErrorCode.ERR_TestTakesParameters, declaration.Parameters.Span, "a test's parameters are plain ones, each given a value of '@test(arguments: [...])'");
            return;
        }

        if (parameters.IsEmpty || arguments.Count != 1 && arguments.Count != parameters.Length) {
            report(
                ErrorCode.ERR_TestTakesParameters,
                declaration.Parameters.Span,
                parameters.IsEmpty
                    ? "a test given 'arguments:' takes a parameter for them"
                    : $"'arguments:' is one array, of {(parameters.Length == 1 ? "what the test takes" : $"tuples of the {parameters.Length} values the test takes")}"
                      + $"{(parameters.Length == 1 ? "" : $", or {parameters.Length} arrays, one for each")}; here are {arguments.Count}"
            );
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
