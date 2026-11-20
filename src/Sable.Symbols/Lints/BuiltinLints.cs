using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>How a local came to be: what a lint may say about it depends on it.</summary>
internal enum LocalOrigin {
    /// <summary><c>let x = …</c> or <c>var x = …</c>.</summary>
    Declaration,

    /// <summary>A name in a destructuring <c>let (a, b) = …</c>.</summary>
    Destructuring,

    /// <summary><c>if let x = …</c>, <c>guard let</c>, <c>while let</c>.</summary>
    Condition,

    /// <summary><c>for x in …</c>.</summary>
    Loop,

    /// <summary>A binding in a match arm or a catch.</summary>
    Pattern
}

/// <summary>
///     One walk over a bound body: the locals it declares, and which locals
///     and parameters it reads and which locals it may change.
/// </summary>
/// <remarks>
///     "Read" is any use but being assigned to: <c>x = 1</c> does not read
///     <c>x</c>, <c>x += 1</c> does. "Changed" is conservative — an
///     assignment to it or into it (<c>p.x = 1</c>, <c>xs[0] = 1</c>), a
///     mutating method called on it, a reference taken to it — so that a
///     <c>var</c> reported as never changed always compiles as a <c>let</c>.
/// </remarks>
internal sealed class BodyFacts {
    public List<(LocalSymbol Local, LocalOrigin Origin)> Declared { get; } = [];
    public HashSet<Symbol> Read { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<Symbol> Changed { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Local functions in the body, whose parameters are the body's to check too.</summary>
    public List<FunctionSymbol> LocalFunctions { get; } = [];

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BoundBlock, BodyFacts> Cache = [];

    public static BodyFacts Of(LintContext context, BoundBlock body) {
        if (Cache.TryGetValue(body, out var cached)) {
            return cached;
        }

        var facts = new BodyFacts();
        facts.Walk(body, PatternOrigin: LocalOrigin.Pattern);
        facts.ChangedThroughArguments(context, body.Syntax);
        Cache.AddOrUpdate(body, facts);
        return facts;
    }

    void Walk(BoundNode node, LocalOrigin PatternOrigin) {
        switch (node) {
            case BoundLocalDeclaration declaration:
                Declared.Add((declaration.Local, LocalOrigin.Declaration));
                break;

            case BoundPatternDeclaration declaration:
                Walk(declaration.Initializer, PatternOrigin);
                Walk(declaration.Pattern, LocalOrigin.Destructuring);
                return;

            case BoundForEach { Element: { } element }:
                Declared.Add((element, LocalOrigin.Loop));
                break;

            case BoundConditionBinding binding:
                if (binding.Local is { } bound) {
                    Declared.Add((bound, LocalOrigin.Condition));
                }

                Walk(binding.Source, PatternOrigin);
                if (binding.Pattern is { } conditionPattern) {
                    Walk(conditionPattern, LocalOrigin.Condition);
                }

                return;

            case BoundBindingPattern pattern:
                Declared.Add((pattern.Local, PatternOrigin));
                break;

            case BoundLocalAccess access:
                Read.Add(access.Local);
                break;

            case BoundParameterAccess access:
                Read.Add(access.Parameter);
                break;

            case BoundAssignment assignment:
                // Assigned to is not read; assigned into ('p.x = 1') reads the rest of the path.
                if (BaseBinding(assignment.Target) is { } owner) {
                    Changed.Add(owner);
                }

                if (assignment.Target is not (BoundLocalAccess or BoundParameterAccess)) {
                    Walk(assignment.Target, PatternOrigin);
                }

                Walk(assignment.Value, PatternOrigin);
                return;

            case BoundCall call:
                if (call is { Function.IsMutating: true, Receiver: { } receiver } && BaseBinding(receiver) is { } mutated) {
                    Changed.Add(mutated);
                }

                // An 'inout' or 'out' argument is written by the callee.
                for (var i = 0; i < call.Arguments.Length && i < call.Function.Parameters.Length; i++) {
                    if (call.Function.Parameters[i].Modifier is ParameterModifier.InOut or ParameterModifier.Out
                        && BaseBinding(call.Arguments[i] is BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf } address ? address.Operand : call.Arguments[i]) is { } written) {
                        Changed.Add(written);
                    }
                }

                break;

            case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf, Operand: var operand }:
                if (BaseBinding(operand) is { } referenced) {
                    Changed.Add(referenced);
                }

                break;

            case BoundConversion { ConversionKind: ConversionKind.Reference, Operand: var place }:
                if (BaseBinding(place) is { } referenced2) {
                    Changed.Add(referenced2);
                }

                break;

            case BoundClosure closure:
                foreach (var capture in closure.Captures) {
                    Read.Add(capture.Symbol);
                }

                break;

            case BoundLocalFunction local:
                LocalFunctions.Add(local.Symbol);
                break;
        }

        foreach (var child in node.Children) {
            Walk(child, PatternOrigin);
        }
    }

    /// <summary><c>f(&amp;x)</c> and <c>f(out x)</c> change <c>x</c>, however the binder spelled them.</summary>
    void ChangedThroughArguments(LintContext context, SyntaxNode body) {
        foreach (var argument in body.DescendantNodes().OfType<ArgumentSyntax>()) {
            if (argument.ModifierToken.IsNone || argument.ModifierToken.Kind == SyntaxKind.MoveKeyword) {
                continue;
            }

            for (var expression = argument.Expression; expression is not null;) {
                if (context.Model.GetSymbolOf(expression) is LocalSymbol local) {
                    Changed.Add(local);
                    break;
                }

                expression = expression switch {
                    MemberAccessExpressionSyntax member => member.Expression,
                    ElementAccessExpressionSyntax element => element.Expression,
                    _ => null,
                };
            }
        }
    }

    /// <summary>The binding a place ultimately belongs to: <c>x</c> for <c>x.a[1].b</c>.</summary>
    public static Symbol? BaseBinding(BoundExpression expression) =>
        expression switch {
            BoundLocalAccess local => local.Local,
            BoundParameterAccess parameter => parameter.Parameter,
            BoundFieldAccess { Receiver: { } receiver } => BaseBinding(receiver),
            BoundIndexAccess index => BaseBinding(index.Receiver),
            BoundTupleElementAccess tuple => BaseBinding(tuple.Receiver),
            BoundConversion conversion => BaseBinding(conversion.Operand),
            BoundCall { Function.FunctionKind: FunctionKind.Subscript, Receiver: { } receiver } => BaseBinding(receiver),
            _ => null,
        };

    /// <remarks>A '$' name is the compiler's own, as the element of 'for _ in xs' is.</remarks>
    public static bool Ignored(string name) => name.StartsWith('_') || name.StartsWith('$');

    /// <summary>The identifier that names a binding in its declaration, if it is written there.</summary>
    public static SyntaxToken? NameToken(SyntaxNode declaration, string name) {
        foreach (var token in declaration.DescendantTokens()) {
            if (token.Kind == SyntaxKind.IdentifierToken && token.Text == name) {
                return token;
            }
        }

        return null;
    }

    public static IEnumerable<SyntaxNode> AncestorsAndSelf(SyntaxNode? node) {
        for (; node is not null; node = node.Parent) {
            yield return node;
        }
    }
}

/// <summary><c>unused_local</c>: a binding nothing reads.</summary>
internal sealed class UnusedLocalLint : Lint {
    public override string Name => "unused_local";
    public override ErrorCode Code => ErrorCode.LNT_UnusedLocal;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a local binding that is never read; name it '_' or start it with '_' if that is meant";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            var facts = BodyFacts.Of(context, body.Body);
            foreach (var (local, origin) in facts.Declared) {
                if (BodyFacts.Ignored(local.Name) || facts.Read.Contains(local) || local.DeclaringSyntax.IsEmpty) {
                    continue;
                }

                var what = origin switch {
                    LocalOrigin.Loop => "loop variable",
                    LocalOrigin.Condition or LocalOrigin.Pattern or LocalOrigin.Destructuring => "binding",
                    _ => local.IsMutable ? "variable" : "constant",
                };
                var message = $"{what} '{local.Name}' is never read; use '_' for a value nobody needs";
                if (BodyFacts.NameToken(local.DeclaringSyntax[0], local.Name) is { } name) {
                    context.Report(local.DeclaringSyntax[0], message, LintContext.Fix($"Prefix '{local.Name}' with '_'", TextChange.Insert(name.Span.Start, "_")));
                } else {
                    context.Report(local.DeclaringSyntax[0], message);
                }
            }
        }
    }
}

/// <summary><c>unused_parameter</c>: a parameter a body never reads, where the signature is the function's own choice.</summary>
internal sealed class UnusedParameterLint : Lint {
    public override string Name => "unused_parameter";
    public override ErrorCode Code => ErrorCode.LNT_UnusedParameter;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a parameter the body never reads, outside an override or a protocol's requirement";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            if (body.Function is not { } function) {
                continue;
            }

            var facts = BodyFacts.Of(context, body.Body);
            foreach (var candidate in facts.LocalFunctions.Prepend(function)) {
                if (SignatureIsDictated(candidate)) {
                    continue;
                }

                foreach (var parameter in candidate.Parameters) {
                    if (BodyFacts.Ignored(parameter.Name) || facts.Read.Contains(parameter) || parameter.DeclaringSyntax is not [ParameterSyntax syntax]) {
                        continue;
                    }

                    // Callers write the label, so it stays: a parameter
                    // whose name is its label keeps that name as the label.
                    var renamed = "_" + syntax.Identifier.Text;
                    var fix = syntax.Label.IsNone || syntax.Label.IsMissing
                        ? LintContext.Fix($"Name the parameter '{renamed}', keeping its label", TextChange.Replace(syntax.Identifier.Span, $"{syntax.Identifier.Text} {renamed}"))
                        : LintContext.Fix($"Name the parameter '{renamed}'", TextChange.Replace(syntax.Identifier.Span, renamed));
                    context.Report(syntax, $"parameter '{parameter.Name}' is never read; name it '_{parameter.Name}' if the signature needs it", fix);
                }
            }
        }
    }

    /// <summary>
    ///     A signature the function does not choose: an accessor's, an
    ///     override's, an entry point's, or one that may satisfy a protocol's
    ///     requirement of that name.
    /// </summary>
    internal static bool SignatureIsDictated(FunctionSymbol function) {
        if (function.FunctionKind is FunctionKind.Accessor or FunctionKind.Deinitializer
            || function.IsOverride || function.IsAbstract || function.Name == "main") {
            return true;
        }

        var owner = function.ContainingSymbol switch {
            ExtensionSymbol extension => extension.ExtendedType,
            NamedTypeSymbol type => type,
            _ => null,
        };
        if (owner is null) {
            return false;
        }

        if (owner.TypeKind == TypeKind.Protocol) {
            return true;
        }

        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<NamedTypeSymbol>(owner.BaseTypes.Concat(owner.DerivedConformances).OfType<NamedTypeSymbol>());
        while (pending.TryPop(out var supertype)) {
            if (!seen.Add(supertype.OriginalDefinition)) {
                continue;
            }

            // A protocol's requirement, or a base class's method an override would replace.
            if (supertype.OriginalDefinition.GetMembers(function.Name).Any()) {
                return true;
            }

            foreach (var further in supertype.OriginalDefinition.BaseTypes.OfType<NamedTypeSymbol>()) {
                pending.Push(further);
            }
        }

        return false;
    }
}

/// <summary><c>unused_import</c>: an import nothing in the file names anything from.</summary>
internal sealed class UnusedImportLint : Lint {
    public override string Name => "unused_import";
    public override ErrorCode Code => ErrorCode.LNT_UnusedImport;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "an import from which the file uses nothing";

    public override void Check(LintContext context) {
        if (context.Tree.Root is not CompilationUnitSyntax unit || unit.Imports.Count == 0) {
            return;
        }

        // Every name the file writes, outside its module and import lines.
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in unit.DescendantTokens()) {
            if (BodyFacts.AncestorsAndSelf(token.Parent).Any(static n => n is ImportDeclarationSyntax or ModuleDeclarationSyntax)) {
                continue;
            }

            written.Add(token.Text);
        }

        foreach (var import in unit.Imports) {
            if (context.Compilation.GetModule(import.Name.ToString().Trim()) is not { } module) {
                continue;
            }

            if (!NamesFrom(context.Compilation, module).Overlaps(written)) {
                var message = $"nothing from '{module.Name}' is used here";
                if (context.WholeLinesOf(import) is { } lines) {
                    // The last import between blank lines takes one of them with it.
                    var text = context.Tree.Text.Text;
                    if (lines.End < text.Length && text[lines.End] == '\n' && lines.Start >= 2 && text[lines.Start - 1] == '\n' && text[lines.Start - 2] == '\n') {
                        lines = TextSpan.FromBounds(lines.Start, lines.End + 1);
                    }

                    context.Report(import, message, LintContext.Fix("Remove the import", TextChange.Delete(lines)));
                } else {
                    context.Report(import, message);
                }
            }
        }
    }

    /// <summary>
    ///     What a file could name from a module: its declarations, the members
    ///     its extensions add to other modules' types, and its operators.
    /// </summary>
    static HashSet<string> NamesFrom(Compilation compilation, ModuleSymbol module) {
        var names = new HashSet<string>(module.Members.Select(static m => m.Name), StringComparer.Ordinal);
        foreach (var type in compilation.Modules.SelectMany(static m => m.Members).OfType<NamedTypeSymbol>()) {
            if (type.ContainingModule == module) {
                continue;
            }

            foreach (var member in type.Members) {
                if (!member.DeclaringSyntax.IsEmpty && compilation.FileScopeOf(member.DeclaringSyntax[0])?.Module == module) {
                    names.Add(member.Name);
                }
            }
        }

        foreach (var type in module.Members.OfType<NamedTypeSymbol>()) {
            foreach (var member in type.Members.OfType<FunctionSymbol>().Where(static f => f.FunctionKind == FunctionKind.Operator)) {
                names.Add(member.Name);
            }
        }

        return names;
    }
}

/// <summary><c>var_never_mutated</c>: a <c>var</c> that could be a <c>let</c>.</summary>
internal sealed class VarNeverMutatedLint : Lint {
    public override string Name => "var_never_mutated";
    public override ErrorCode Code => ErrorCode.LNT_VarNeverMutated;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a 'var' that is never changed after its initial value, and could be a 'let'";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            var facts = BodyFacts.Of(context, body.Body);
            foreach (var (local, origin) in facts.Declared) {
                if (origin != LocalOrigin.Declaration || !local.IsMutable || facts.Changed.Contains(local) || BodyFacts.Ignored(local.Name)) {
                    continue;
                }

                // A 'var' declared without a value is assigned later, which a 'let' allows only once; leave it.
                if (local.DeclaringSyntax is not [var declarator] || BodyFacts.AncestorsAndSelf(declarator).OfType<LocalDeclarationStatementSyntax>().FirstOrDefault() is not { Declarator.Initializer: not null } statement) {
                    continue;
                }

                context.Report(statement.LetOrVarKeyword.Parent ?? declarator, $"'{local.Name}' is never changed; declare it with 'let'",
                    LintContext.Fix("Declare it with 'let'", TextChange.Replace(statement.LetOrVarKeyword.Span, "let")));
            }
        }
    }
}

/// <summary><c>shadowed_parameter</c>: a <c>let</c> or <c>var</c> that hides a parameter of the same name.</summary>
internal sealed class ShadowedParameterLint : Lint {
    public override string Name => "shadowed_parameter";
    public override ErrorCode Code => ErrorCode.LNT_ShadowedParameter;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a local declaration that hides a parameter; 'if let x = x' is not one";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            if (body.Function is not { } function) {
                continue;
            }

            var parameters = function.Parameters.Select(static p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (local, origin) in BodyFacts.Of(context, body.Body).Declared) {
                if (origin is LocalOrigin.Declaration or LocalOrigin.Destructuring
                    && parameters.Contains(local.Name)
                    && !BodyFacts.Ignored(local.Name)
                    && !local.DeclaringSyntax.IsEmpty) {
                    context.Report(local.DeclaringSyntax[0], $"'{local.Name}' hides the parameter of that name");
                }
            }
        }
    }
}

/// <summary>
///     <c>unused_result</c>: a call whose result is left unused, as a statement
///     of its own, where the function does not say that is fine with
///     <c>@discardable</c>.
/// </summary>
internal sealed class UnusedResultLint : Lint {
    public override string Name => "unused_result";
    public override ErrorCode Code => ErrorCode.LNT_UnusedResult;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a call's result left unused, where the function is not '@discardable'; write 'let _ = f()' if that is meant";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            Walk(context, body.Body);
        }
    }

    static void Walk(LintContext context, BoundNode node) {
        if (node is BoundExpressionStatement { Expression: var expression }) {
            // 'try f()' and 'try? f()' discard what f gives as much as 'f()' does.
            while (expression is BoundTry attempt) {
                expression = attempt.Expression;
            }

            if (expression is BoundCall call && Discards(context.Compilation, call)) {
                context.Report(call.Syntax, $"the result of '{call.Function.Name}' is unused; write 'let _ = ...' if that is meant, or mark '{call.Function.Name}' '@discardable'",
                    LintContext.Fix("Drop the result with 'let _ ='", TextChange.Insert(node.Syntax.Span.Start, "let _ = ")));
            }
        }

        foreach (var child in node.Children) {
            Walk(context, child);
        }
    }

    /// <summary>Whether leaving the call's result unused loses something: a value, from a function that does not say it may be dropped.</summary>
    static bool Discards(Compilation compilation, BoundCall call) {
        var type = call.Type;
        if (type.IsError || type.Equals(compilation.Core.Void) || type.Equals(compilation.Core.Never)
            || type is TupleTypeSymbol { ElementTypes.IsEmpty: true }
            || call.Function.FunctionKind is FunctionKind.Initializer) {
            return false;
        }

        return call.Function.DeclaringSyntax is not [var declaration, ..]
            || !Compilation.AttributesOf(declaration).Any(static a => Compilation.AttributeName(a) == "discardable");
    }
}
