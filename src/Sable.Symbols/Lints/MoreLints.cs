using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>shadowed_local</c>: a <c>let</c> or <c>var</c> in an inner block
///     that hides a local of the same name from a block around it.
/// </summary>
/// <remarks>
///     Declaring the name again in the same block (<c>let x = x + 1</c> after
///     <c>let x</c>) is a deliberate rebinding, and <c>if let x = x</c> a
///     deliberate unwrapping; neither is reported. A parameter hidden is
///     <c>shadowed_parameter</c>'s.
/// </remarks>
internal sealed class ShadowedLocalLint : Lint {
    public override string Name => "shadowed_local";
    public override ErrorCode Code => ErrorCode.LNT_ShadowedLocal;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a local in an inner block that hides a local of the same name from a block around it";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            var declared = BodyFacts.Of(context, body.Body).Declared
                .Where(static d => !d.Local.DeclaringSyntax.IsEmpty && !BodyFacts.Ignored(d.Local.Name))
                .ToList();

            foreach (var (inner, innerOrigin) in declared) {
                if (innerOrigin is not (LocalOrigin.Declaration or LocalOrigin.Destructuring)) {
                    continue;
                }

                var at = inner.DeclaringSyntax[0];
                var innerBlock = BlockOf(at);
                foreach (var (outer, outerOrigin) in declared) {
                    if (ReferenceEquals(outer, inner) || outer.Name != inner.Name || ScopeOf(outer, outerOrigin) is not { } scope) {
                        continue;
                    }

                    // In scope where the inner one is declared, and from a block outside the inner one's.
                    var visible = outerOrigin == LocalOrigin.Loop
                        ? scope.Span.Contains(at.Span.Start)
                        : scope.Span.Contains(at.Span.Start) && outer.DeclaringSyntax[0].Span.Start < at.Span.Start;
                    if (visible && !ReferenceEquals(scope, innerBlock)) {
                        context.Report(at, $"'{inner.Name}' hides the local of that name declared on line {LineOf(context, outer.DeclaringSyntax[0])}; give one of them another name");
                        break;
                    }
                }
            }
        }
    }

    static int LineOf(LintContext context, SyntaxNode node) => context.Tree.Text.GetLinePosition(node.Span.Start).Line;

    /// <summary>The block a declaration is a statement of.</summary>
    static BlockSyntax? BlockOf(SyntaxNode node) => BodyFacts.AncestorsAndSelf(node.Parent).OfType<BlockSyntax>().FirstOrDefault();

    /// <summary>
    ///     Where a local can be named: the rest of its block for a <c>let</c>
    ///     or <c>var</c>, the loop's body for a loop variable. Null for a
    ///     binding of a condition or a pattern, whose reach is not a block of
    ///     its own here.
    /// </summary>
    static SyntaxNode? ScopeOf(LocalSymbol local, LocalOrigin origin) =>
        origin switch {
            LocalOrigin.Declaration or LocalOrigin.Destructuring => BlockOf(local.DeclaringSyntax[0]),
            LocalOrigin.Loop => BodyFacts.AncestorsAndSelf(local.DeclaringSyntax[0]).OfType<ForStatementSyntax>().FirstOrDefault()?.Body,
            _ => null,
        };
}

/// <summary><c>self_assignment</c>: <c>x = x</c>, which changes nothing.</summary>
internal sealed class SelfAssignmentLint : Lint {
    public override string Name => "self_assignment";
    public override ErrorCode Code => ErrorCode.LNT_SelfAssignment;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a place assigned its own value, as 'x = x' or 'self.count = self.count'";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            Walk(context, body.Body);
        }
    }

    static void Walk(LintContext context, BoundNode node) {
        if (node is BoundExpressionStatement { Expression: BoundAssignment assignment } statement && SamePlace(assignment.Target, assignment.Value)) {
            var place = assignment.Target.Syntax.ToString().Trim();
            var message = $"'{place}' is assigned its own value, which changes nothing; was another value meant?";
            if (context.WholeLinesOf(statement.Syntax) is { } lines) {
                context.Report(assignment.Syntax, message, LintContext.Fix("Remove the assignment", TextChange.Delete(lines)));
            } else {
                context.Report(assignment.Syntax, message);
            }
        }

        foreach (var child in node.Children) {
            Walk(context, child);
        }
    }

    /// <summary>
    ///     Whether two expressions name the same stored place: the same
    ///     local or parameter, or the same stored field of the same place.
    ///     A computed property is not one — its getter and setter may do
    ///     anything — and neither is an element, whose index may differ.
    /// </summary>
    static bool SamePlace(BoundExpression target, BoundExpression value) {
        while (value is BoundConversion { ConversionKind: ConversionKind.Identity } conversion) {
            value = conversion.Operand;
        }

        return (target, value) switch {
            (BoundLocalAccess a, BoundLocalAccess b) => ReferenceEquals(a.Local, b.Local),
            (BoundParameterAccess a, BoundParameterAccess b) => ReferenceEquals(a.Parameter, b.Parameter),
            (BoundSelfAccess, BoundSelfAccess) => true,
            (BoundFieldAccess a, BoundFieldAccess b) =>
                !a.Field.IsComputed
                && SameField(a.Field, b.Field)
                && (a.Receiver, b.Receiver) switch {
                    (null, null) => true,
                    ({ } x, { } y) => SamePlace(x, y),
                    _ => false,
                },
            _ => false,
        };
    }

    /// <summary>The same field: one symbol, or the same declaration seen through one generic type's arguments twice.</summary>
    static bool SameField(FieldSymbol a, FieldSymbol b) =>
        ReferenceEquals(a, b) || (a.Name == b.Name && a.DeclaringSyntax is [var x, ..] && b.DeclaringSyntax is [var y, ..] && ReferenceEquals(x, y));
}

/// <summary>
///     <c>unused_private</c>: a <c>private</c> function or type nothing
///     outside its own declaration names.
/// </summary>
/// <remarks>
///     <c>private</c> reaches the declaration around it and its extensions
///     in the module, so every file but the core library's is searched. A
///     function only its own body calls — recursion and nothing else — is
///     unused too. A function whose signature something else dictates (an
///     override, a protocol's requirement, <c>main</c>) or that the harness
///     or another language calls (<c>@test</c>, <c>@bench</c>,
///     <c>@export</c>) is never reported; nor are operators, initialisers and
///     accessors, which are used without being named.
/// </remarks>
internal sealed class UnusedPrivateLint : Lint {
    public override string Name => "unused_private";
    public override ErrorCode Code => ErrorCode.LNT_UnusedPrivate;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a private function or type that nothing outside its own declaration uses";

    static readonly HashSet<string> CalledFromOutside = new(StringComparer.Ordinal) { "test", "suite", "bench", "fuzz", "export", "extern" };

    public override void Check(LintContext context) {
        var trees = context.Compilation.SyntaxTrees.Where(static t => !CoreLibrary.Contains(t) && t.Root is CompilationUnitSyntax).ToList();
        HashSet<string>? inactive = null;
        foreach (var declaration in context.Tree.Root.DescendantNodes().OfType<DeclarationSyntax>()) {
            if (declaration.Parent is DeclarationStatementSyntax || context.Compilation.GetDeclaredSymbol(declaration) is not { DeclaredAccessibility: Accessibility.Private } symbol) {
                continue;
            }

            var what = symbol switch {
                FunctionSymbol { FunctionKind: FunctionKind.Ordinary } function when !function.IsOverride && !UnusedParameterLint.SignatureIsDictated(function) => "function",
                NamedTypeSymbol { TypeKind: not TypeKind.Error } => "type",
                _ => null,
            };
            if (what is null || BodyFacts.Ignored(symbol.Name) || Compilation.AttributesOf(declaration).Any(static a => CalledFromOutside.Contains(Compilation.AttributeName(a)))) {
                continue;
            }

            // An '#if' branch not compiled may use it.
            inactive ??= [..trees.SelectMany(static t => BodyFacts.NamesInInactiveCode(t.Root))];
            if (inactive.Contains(symbol.Name)) {
                continue;
            }

            if (!trees.Any(tree => UsedIn(context.Compilation.GetSemanticModel(tree), tree == context.Tree, symbol, declaration))) {
                context.Report(NameOf(declaration, symbol.Name), $"the private {what} '{symbol.Name}' is never used");
            }
        }
    }

    /// <summary>Whether a file names the symbol anywhere outside its own declaration.</summary>
    static bool UsedIn(SemanticModel model, bool declaredHere, Symbol symbol, SyntaxNode declaration) =>
        model.FindReferences(symbol).Any(span => !declaredHere || !declaration.Span.Contains(span.Start));

    /// <summary>The name in a declaration, which is what is reported; the declaration when it is not found.</summary>
    static SyntaxNode NameOf(SyntaxNode declaration, string name) =>
        BodyFacts.NameToken(declaration, name) is { Parent: { } parent } token && parent.Span == token.Span ? parent : declaration;
}

/// <summary>
///     <c>constant_arithmetic</c>: integer arithmetic in a function whose
///     operands are all written out, and that overflows its type or divides
///     by zero — a trap whenever it runs; and division by a zero written out.
/// </summary>
/// <remarks>
///     A <c>const</c>'s initialiser is already worked out by the compiler, and
///     its overflow is the error S3201; this is the same arithmetic in a body,
///     which the compiler leaves to run.
/// </remarks>
internal sealed class ConstantArithmeticLint : Lint {
    public override string Name => "constant_arithmetic";
    public override ErrorCode Code => ErrorCode.LNT_ConstantArithmetic;
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "integer arithmetic on constants that overflows or divides by zero, and so traps whenever it runs";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            Walk(context, body.Body);
        }
    }

    static void Walk(LintContext context, BoundNode node) {
        var core = context.Compilation.Core;
        if (node is BoundBinaryOperator { Method: null } or BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Negate } && core.IsInteger(((BoundExpression)node).Type)) {
            var found = new DiagnosticBag();
            if (ConstantFolding.Fold((BoundExpression)node, core, found) is not null) {
                // All constant, and in range: nothing in it traps.
                return;
            }

            if (found.FirstOrDefault() is { } overflow) {
                context.Report(node.Syntax, $"{overflow.Message}, so it traps whenever it runs");
                return;
            }

            if (node is BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder } division
                && ConstantFolding.Fold(division.Right, core, new DiagnosticBag()) is { IsZero: true }) {
                context.Report(node.Syntax, $"'{node.Syntax.ToString().Trim()}' divides by zero, so it traps whenever it runs");
            }
        }

        foreach (var child in node.Children) {
            Walk(context, child);
        }
    }
}
