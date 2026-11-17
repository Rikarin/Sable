using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>Which harness a function is for: <c>@test</c>'s or <c>@bench</c>'s.</summary>
public enum TestKind {
    Test,
    Bench,
}

/// <summary>
///     A function marked <c>@test</c> or <c>@bench</c> that the harness can
///     call, and how.
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
    /// <summary>As the harness lists and filters it: <c>Module.function</c>, or <c>Module.Type.function</c>.</summary>
    public string QualifiedName => Module.Length == 0 ? CallPath : $"{Module}.{CallPath}";

    /// <summary>Where it is declared: <c>file:line</c>.</summary>
    public string Location => $"{Tree.Text.FileName}:{Tree.Text.GetLinePosition(Syntax.Identifier.Span.Start).Line}";
}

/// <summary>
///     <c>@test</c> and <c>@bench</c> (docs/05 §5.1): the functions
///     <c>sable test</c> and <c>sable bench</c> collect into a harness.
/// </summary>
/// <remarks>
///     <para>
///         A test takes nothing and gives nothing back; it may throw, and an
///         error it throws fails it. A benchmark takes nothing, and its whole
///         body is timed, or takes a <c>Sable.Testing.Bencher</c>, and times
///         what it hands the bencher's <c>iterate</c>. Either is a function of
///         a module or a static method of a type that is not generic, so that
///         the harness has one thing to call; neither is async or generic.
///     </para>
///     <para>
///         In an ordinary build they are functions like any other. The
///         checks are made in every build, so that a malformed test is found
///         when it is written rather than when the tests are first run.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    const string TestAttribute = "test", BenchAttribute = "bench";

    /// <summary>Every well-formed <c>@test</c> (or <c>@bench</c>) function, in file and source order.</summary>
    public IReadOnlyList<TestFunction> GetTestFunctions(TestKind kind) {
        var found = new List<TestFunction>();
        foreach (var tree in SyntaxTrees) {
            if (!_fileScopes.ContainsKey(tree)) {
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

    /// <summary>What is wrong with the file's <c>@test</c> and <c>@bench</c> functions; its semantic model reports these.</summary>
    internal IReadOnlyList<Diagnostic> CheckTestFunctions(SyntaxTree tree) {
        var diagnostics = new List<Diagnostic>();
        foreach (var declaration in tree.Root.DescendantNodes().OfType<FunctionDeclarationSyntax>()) {
            if (KindOf(declaration) is not null) {
                Examine(declaration, tree, diagnostics);
            }
        }

        return diagnostics;
    }

    static TestKind? KindOf(FunctionDeclarationSyntax declaration) {
        foreach (var attribute in declaration.Attributes) {
            switch (attribute.Name.ToString().Trim()) {
                case TestAttribute: return TestKind.Test;
                case BenchAttribute: return TestKind.Bench;
            }
        }

        return null;
    }

    /// <summary>The test the declaration is, or null, reporting why not when there is somewhere to.</summary>
    TestFunction? Examine(FunctionDeclarationSyntax declaration, SyntaxTree tree, List<Diagnostic>? diagnostics) {
        var attributes = declaration.Attributes.Where(static a => a.Name.ToString().Trim() is TestAttribute or BenchAttribute).ToList();
        var first = attributes[0];
        var kind = first.Name.ToString().Trim() == TestAttribute ? TestKind.Test : TestKind.Bench;
        var marker = kind == TestKind.Test ? "'@test'" : "'@bench'";
        var ok = true;

        void Report(ErrorCode code, TextSpan span, string message) {
            ok = false;
            diagnostics?.Add(Diagnostic.Error(code, span, message));
        }

        if (attributes.Select(static a => a.Name.ToString().Trim()).Distinct().Count() > 1) {
            Report(ErrorCode.ERR_TestAndBench, attributes[1].Span, "a function is a test or a benchmark, not both");
        }

        foreach (var attribute in attributes.Where(static a => a.ArgumentList is not null)) {
            Report(ErrorCode.ERR_TestAttributeArguments, attribute.ArgumentList!.Span, $"{(attribute.Name.ToString().Trim() == TestAttribute ? "'@test'" : "'@bench'")} takes no arguments");
        }

        if (GetDeclaredSymbol(declaration) is not FunctionSymbol function || function.ContainingSymbol is FunctionSymbol) {
            Report(ErrorCode.ERR_TestIsLocal, first.Span, $"{marker} marks a function of a module or a static method; the harness cannot reach a function declared inside a body");
            return null;
        }

        if (declaration.TypeParameters is not null) {
            Report(ErrorCode.ERR_TestIsGeneric, declaration.TypeParameters.Span, $"a {Noun(kind)} cannot be generic: the harness calls it with no type arguments to give");
        }

        if (function.IsAsync) {
            Report(ErrorCode.ERR_TestIsAsync, declaration.Identifier.Span, $"a {Noun(kind)} cannot be async yet: the harness calls it synchronously");
        }

        if (function.ReturnType is var returned && !ReferenceEquals(returned, Core.Void) && returned is not ErrorTypeSymbol) {
            Report(ErrorCode.ERR_TestReturnsValue, declaration.ReturnClause?.Span ?? declaration.Identifier.Span, $"a {Noun(kind)} returns nothing: it fails by failing an assertion{(kind == TestKind.Test ? " or throwing" : "")}, not by what it returns");
        }

        var takesBencher = false;
        if (kind == TestKind.Test) {
            if (function.Parameters.Length > 0) {
                Report(ErrorCode.ERR_TestTakesParameters, declaration.Parameters.Span, "a test takes no parameters: the harness has nothing to pass it");
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
            ? new(function, declaration, tree, kind, _fileScopes[tree].Module.Name, callPath, function.ThrownType is not null, takesBencher)
            : null;
    }

    static string Noun(TestKind kind) => kind == TestKind.Test ? "test" : "benchmark";

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
