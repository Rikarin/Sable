using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     A node of the bound tree: syntax that has been resolved to symbols and
///     types.
/// </summary>
/// <remarks>
///     The bound tree is what lowering reads, and it is where every question of
///     meaning has already been answered — which overload, which conversion, which
///     local. It keeps a pointer back to its syntax so diagnostics and the
///     language server can find their way to the source.
/// </remarks>
public abstract class BoundNode(SyntaxNode syntax) {
    public SyntaxNode Syntax { get; } = syntax;

    public abstract IEnumerable<BoundNode> Children { get; }

    protected static IEnumerable<BoundNode> Nodes(params BoundNode?[] nodes) {
        foreach (var node in nodes) {
            if (node is not null) {
                yield return node;
            }
        }
    }
}

public abstract class BoundExpression(SyntaxNode syntax, TypeSymbol type) : BoundNode(syntax) {
    public TypeSymbol Type { get; } = type;

    public bool HasError => Type.IsError;

    /// <summary>True when this expression names storage that can be assigned.</summary>
    public virtual bool IsAssignable => false;
}

public abstract class BoundStatement(SyntaxNode syntax) : BoundNode(syntax);

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

public sealed class BoundLiteral(SyntaxNode syntax, TypeSymbol type, object? value)
    : BoundExpression(syntax, type) {
    public object? Value { get; } = value;
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>A string with holes; the pieces are bound expressions.</summary>
public sealed class BoundInterpolatedString(
    SyntaxNode syntax,
    TypeSymbol type,
    ImmutableArray<BoundExpression> parts,
    ImmutableArray<BoundFormatSpec?> formats)
    : BoundExpression(syntax, type) {
    public ImmutableArray<BoundExpression> Parts { get; } = parts;

    /// <summary>Each hole's format spec, <c>\(x:08x)</c>, in step with <see cref="Parts" />: null for a hole without one.</summary>
    public ImmutableArray<BoundFormatSpec?> Formats { get; } = formats;

    public override IEnumerable<BoundNode> Children => [..Parts, ..Formats.OfType<BoundFormatSpec>()];
}

/// <summary>
///     A hole's format spec: the <c>FormatSpec</c> value the hole is written
///     with, and the spec as the compiler read it.
/// </summary>
public sealed class BoundFormatSpec(SyntaxNode syntax, BoundExpression value, FormatSpecifier specifier)
    : BoundExpression(syntax, value.Type) {
    public BoundExpression Value { get; } = value;
    public FormatSpecifier Specifier { get; } = specifier;
    public override IEnumerable<BoundNode> Children => [Value];
}

/// <summary>
///     A value as an interpolation writes it without a format spec — or with
///     <c>?</c>, as <c>Debug</c> describes it — for a hole whose spec is only
///     a width to pad it to: what the lowering pads, for a type that is not
///     <c>Formattable</c>. Made by the lowering, never by the binder.
/// </summary>
/// <summary>
///     One hole of an interpolation, as the text it writes, with its spec:
///     what the lowering makes of each hole when it writes an interpolation
///     into a TextWriter a piece at a time. Made by the lowering only.
/// </summary>
public sealed class BoundInterpolationHole(SyntaxNode syntax, TypeSymbol type, BoundExpression value, BoundFormatSpec? format)
    : BoundExpression(syntax, type) {
    public BoundExpression Value { get; } = value;
    public BoundFormatSpec? Format { get; } = format;
    public override IEnumerable<BoundNode> Children => [Value];
}

public sealed class BoundHoleText(SyntaxNode syntax, TypeSymbol type, BoundExpression value, bool debug)
    : BoundExpression(syntax, type) {
    public BoundExpression Value { get; } = value;
    public bool Debug { get; } = debug;
    public override IEnumerable<BoundNode> Children => [Value];
}

public sealed class BoundLocalAccess(
    SyntaxNode syntax,
    LocalSymbol local,
    TypeSymbol? narrowed = null)
    : BoundExpression(syntax, narrowed ?? local.Type) {
    public LocalSymbol Local { get; } = local;

    /// <summary>True where a test in scope has established a narrower type.</summary>
    public bool IsNarrowed { get; } = narrowed is not null;

    public override bool IsAssignable => Local.IsMutable;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundParameterAccess(
    SyntaxNode syntax,
    ParameterSymbol parameter,
    TypeSymbol? narrowed = null)
    : BoundExpression(syntax, narrowed ?? parameter.Type) {
    public ParameterSymbol Parameter { get; } = parameter;

    /// <summary>True where a test in scope has established a narrower type.</summary>
    public bool IsNarrowed { get; } = narrowed is not null;

    public override bool IsAssignable => Parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out;

    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundFieldAccess(
    SyntaxNode syntax,
    BoundExpression? receiver,
    FieldSymbol field,
    TypeSymbol? type = null)
    : BoundExpression(syntax, type ?? field.Type) {
    public BoundExpression? Receiver { get; } = receiver;
    public FieldSymbol Field { get; } = field;

    /// <summary>
    ///     What the type parameters of the type a static property is read on
    ///     stand for: 'MemoryLayout&lt;Int&gt;.size' reads Int's. Empty for any
    ///     other access, whose receiver's type says.
    /// </summary>
    public TypeSubstitution Substitution { get; init; } = TypeSubstitution.Empty;

    /// <summary>
    ///     '?.' to a field that is optional itself: the chain's result is the
    ///     field's value, not that value wrapped again — 'user?.address' of
    ///     an 'Address?' field is an 'Address?', as 'Address??' would be
    ///     another '?.' away from its members.
    /// </summary>
    public bool FlattensChain { get; init; }

    /// <summary>
    ///     Assignable when the field is mutable, the member has a setter if it is
    ///     computed, and the value it belongs to can be written through.
    /// </summary>
    /// <remarks>
    ///     A struct in a <c>let</c> is immutable all the way down, so
    ///     <c>p.x = 1</c> is refused for <c>let p: Point</c>. A class is a
    ///     reference, so the binding's mutability says nothing about the object's.
    /// </remarks>
    public override bool IsAssignable =>
        Field.IsMutable
        && (!Field.IsComputed || Field.HasSetter)
        && (Field.IsStatic
            || Receiver is null
            || Receiver.Type.IsReferenceType
            || Receiver.IsAssignable);

    public override IEnumerable<BoundNode> Children => Nodes(Receiver);
}

public sealed class BoundSelfAccess(
    SyntaxNode syntax,
    TypeSymbol type,
    bool isMutable = false)
    : BoundExpression(syntax, type) {
    /// <summary>
    ///     True inside a <c>mutating</c> member, or wherever self is a reference.
    /// </summary>
    public bool IsMutable { get; } = isMutable;

    public override bool IsAssignable => IsMutable;
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>
///     <c>super</c>: self, seen as the base class, so a member reached through it
///     is the base's rather than the override.
/// </summary>
public sealed class BoundSuperAccess(SyntaxNode syntax, NamedTypeSymbol baseType)
    : BoundExpression(syntax, baseType) {
    public NamedTypeSymbol BaseType { get; } = baseType;
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>A reference to a type used as a value, as in <c>Int.self</c>.</summary>
public sealed class BoundTypeExpression(SyntaxNode syntax, TypeSymbol referencedType)
    : BoundExpression(syntax, referencedType) {
    public TypeSymbol ReferencedType { get; } = referencedType;
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>
///     <c>N</c> in the body of <c>struct Buf&lt;T, N: Int&gt;</c>: the number a
///     value generic parameter stands for, which each instance knows.
/// </summary>
public sealed class BoundValueParameter(SyntaxNode syntax, TypeParameterSymbol parameter, TypeSymbol type)
    : BoundExpression(syntax, type) {
    public TypeParameterSymbol Parameter { get; } = parameter;
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>
///     <c>Int.self</c>: a type as a value, of type <c>Int.Type</c>
///     (<see cref="MetatypeTypeSymbol" />).
/// </summary>
public sealed class BoundMetatype(SyntaxNode syntax, TypeSymbol instanceType)
    : BoundExpression(syntax, new MetatypeTypeSymbol(instanceType)) {
    /// <summary>The type named: <c>Int</c> for <c>Int.self</c>.</summary>
    public TypeSymbol InstanceType { get; } = instanceType;

    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundCall(
    SyntaxNode syntax,
    BoundExpression? receiver,
    FunctionSymbol function,
    ImmutableArray<BoundExpression> arguments,
    TypeSymbol? type = null)
    : BoundExpression(syntax, type ?? function.ReturnType) {
    public BoundExpression? Receiver { get; } = receiver;
    public FunctionSymbol Function { get; } = function;
    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    /// <summary>
    ///     What the callee's type parameters — its own, and those of the type
    ///     it is a member of — stand for at this call, as inference settled
    ///     them. Empty for a call to nothing generic.
    /// </summary>
    public TypeSubstitution Substitution { get; init; } = TypeSubstitution.Empty;

    public override IEnumerable<BoundNode> Children => Nodes([Receiver, ..Arguments]);
}

/// <summary>A union case used as a value, with its payload.</summary>
public sealed class BoundUnionCaseConstruction(
    SyntaxNode syntax,
    TypeSymbol type,
    UnionCaseSymbol unionCase,
    ImmutableArray<BoundExpression> arguments)
    : BoundExpression(syntax, type) {
    public UnionCaseSymbol UnionCase { get; } = unionCase;
    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;
    public override IEnumerable<BoundNode> Children => Arguments;
}

public enum BinaryOperatorKind {
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    And,
    Or,
    ExclusiveOr,
    LeftShift,
    RightShift,
    Equal,
    NotEqual,
    ReferenceEqual,
    ReferenceNotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LogicalAnd,
    LogicalOr,
    Coalesce,
    UserDefined
}

public sealed class BoundBinaryOperator(
    SyntaxNode syntax,
    TypeSymbol type,
    BinaryOperatorKind operatorKind,
    BoundExpression left,
    BoundExpression right,
    FunctionSymbol? method = null)
    : BoundExpression(syntax, type) {
    public BinaryOperatorKind OperatorKind { get; } = operatorKind;
    public BoundExpression Left { get; } = left;
    public BoundExpression Right { get; } = right;

    /// <summary>The <c>operator</c> member, when the operator was overloaded.</summary>
    public FunctionSymbol? Method { get; } = method;

    /// <summary>
    ///     <c>==</c> or <c>!=</c> that compares its operands part by part:
    ///     two tuples element by element, two optionals by what they hold.
    /// </summary>
    public StructuralComparison? Structural { get; init; }

    public override IEnumerable<BoundNode> Children => Nodes(Left, Right);
}

/// <summary>
///     How two values compare part by part: <see cref="Equal" /> is the
///     comparison written over two stand-ins, which lowering gives the parts
///     — the tuples themselves, or the values two optionals hold.
/// </summary>
/// <remarks>
///     The comparison is bound as source would be, so each part compares
///     with its own '==' — a built-in, a type's own operator, or another
///     tuple's — and a part with none is reported as the operator would be.
/// </remarks>
public sealed record StructuralComparison(LocalSymbol Left, LocalSymbol Right, BoundExpression Equal);

public enum UnaryOperatorKind {
    Negate,
    Plus,
    Not,
    Complement,
    AddressOf,
    Await,
    Move,
    Try,
    UserDefined
}

public sealed class BoundUnaryOperator(
    SyntaxNode syntax,
    TypeSymbol type,
    UnaryOperatorKind operatorKind,
    BoundExpression operand,
    FunctionSymbol? method = null)
    : BoundExpression(syntax, type) {
    public UnaryOperatorKind OperatorKind { get; } = operatorKind;
    public BoundExpression Operand { get; } = operand;
    public FunctionSymbol? Method { get; } = method;
    public override IEnumerable<BoundNode> Children => Nodes(Operand);
}

public sealed class BoundConditional(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression condition,
    BoundExpression whenTrue,
    BoundExpression whenFalse)
    : BoundExpression(syntax, type) {
    public BoundExpression Condition { get; } = condition;
    public BoundExpression WhenTrue { get; } = whenTrue;
    public BoundExpression WhenFalse { get; } = whenFalse;
    public override IEnumerable<BoundNode> Children => Nodes(Condition, WhenTrue, WhenFalse);
}

/// <summary>
///     <c>if c { a } else { b }</c> as a value. <see cref="Body" /> is the
///     <c>if</c>, with an <c>else</c> all the way down; each branch's block
///     ends with an expression statement holding the branch's value, already
///     of <see cref="BoundExpression.Type" />, unless the branch always leaves.
/// </summary>
public sealed class BoundIfExpression(SyntaxNode syntax, TypeSymbol type, BoundIf body)
    : BoundExpression(syntax, type) {
    public BoundIf Body { get; } = body;
    public override IEnumerable<BoundNode> Children => [Body];
}

public sealed class BoundAssignment(
    SyntaxNode syntax,
    BoundExpression target,
    BoundExpression value)
    : BoundExpression(syntax, target.Type) {
    public BoundExpression Target { get; } = target;
    public BoundExpression Value { get; } = value;
    public override IEnumerable<BoundNode> Children => Nodes(Target, Value);
}

public enum ConversionKind {
    Identity,

    /// <summary>A literal taking the type the context asked for.</summary>
    LiteralTyping,

    /// <summary><c>T</c> to <c>T?</c>.</summary>
    OptionalWrap,

    /// <summary><c>Never</c> to anything; the expression does not return.</summary>
    Bottom,

    /// <summary>A concrete type to <c>any P</c>.</summary>
    Existential,

    /// <summary>A derived class to a base class.</summary>
    Upcast,

    /// <summary>A case type to the structural union containing it.</summary>
    UnionInjection,

    /// <summary>Written with <c>as</c>; checked at runtime where needed.</summary>
    Explicit,

    /// <summary><c>T&amp;</c> to <c>T</c>: the value a reference refers to, copied out.</summary>
    Dereference,

    /// <summary>
    ///     A place of type <c>T</c> to <c>T&amp;</c>: a reference to it, where one
    ///     is wanted — a <c>ref</c> result, a reference parameter.
    /// </summary>
    Reference,

    /// <summary>
    ///     An array of any kind as a slice, <c>T[:]</c>: the same elements
    ///     (SYNTAX.md §11). It ranks overloads; the bound tree records it as
    ///     <see cref="Identity" />, since every kind is stored alike.
    /// </summary>
    ArrayView,

    /// <summary>
    ///     <c>T.Type</c> to <c>Type</c>: the type, as the metadata both hold
    ///     (SYNTAX.md "Metatypes"). It ranks overloads; the bound tree records
    ///     it as <see cref="Identity" />.
    /// </summary>
    MetatypeErasure,

    /// <summary>
    ///     A <c>Type</c> given for a generic callee's <c>T.Type</c>, opening it
    ///     (SYNTAX.md "Opening a type", #297): the target is the metatype of
    ///     the opened type (<see cref="TypeParameterSymbol.OpenedFrom" />), and
    ///     the call runs the callee compiled once, with the value's metadata
    ///     as T's.
    /// </summary>
    Open,

    /// <summary>
    ///     A call's result of the type it opened, erased to the existential of
    ///     the callee's constraints on it: <c>make(t)</c> for
    ///     <c>make&lt;T: Zero&gt;(_: T.Type) -&gt; T</c> is an <c>any Zero</c>.
    /// </summary>
    OpenedErasure,
}

public sealed class BoundConversion(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression operand,
    ConversionKind conversionKind)
    : BoundExpression(syntax, type) {
    public BoundExpression Operand { get; } = operand;
    public ConversionKind ConversionKind { get; } = conversionKind;

    /// <summary><c>x unchecked as U8</c>: a numeric conversion that wraps or truncates rather than trapping.</summary>
    public bool IsUnchecked { get; init; }

    public override IEnumerable<BoundNode> Children => Nodes(Operand);
}

public sealed class BoundTypeTest(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression operand,
    TypeSymbol testedType)
    : BoundExpression(syntax, type) {
    public BoundExpression Operand { get; } = operand;
    public TypeSymbol TestedType { get; } = testedType;
    public override IEnumerable<BoundNode> Children => Nodes(Operand);
}

/// <summary><c>a..&lt;b</c>, <c>a...b</c>, and the one-sided forms.</summary>
public sealed class BoundRange(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression? lower,
    BoundExpression? upper,
    bool isInclusive) : BoundExpression(syntax, type) {
    public BoundExpression? Lower { get; } = lower;
    public BoundExpression? Upper { get; } = upper;

    /// <summary>True for <c>...</c>, false for <c>..&lt;</c>.</summary>
    public bool IsInclusive { get; } = isInclusive;

    public override IEnumerable<BoundNode> Children => Nodes(Lower, Upper);
}

/// <summary>A dictionary literal: <c>[:]</c>, <c>["a": 1]</c>.</summary>
public sealed class BoundDictionary(
    SyntaxNode syntax,
    TypeSymbol type,
    ImmutableArray<(BoundExpression Key, BoundExpression Value)> entries)
    : BoundExpression(syntax, type) {
    public ImmutableArray<(BoundExpression Key, BoundExpression Value)> Entries { get; } = entries;

    public override IEnumerable<BoundNode> Children =>
        Entries.SelectMany(static e => new BoundNode[] { e.Key, e.Value });
}

/// <summary>
///     <c>#file</c>, <c>#line</c> and the rest: a value the compiler supplies.
/// </summary>
public sealed class BoundBuiltin(SyntaxNode syntax, TypeSymbol type, string name)
    : BoundExpression(syntax, type) {
    public string Name { get; } = name;
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>A record literal: <c>Point { x: 1, y: 2 }</c>.</summary>
public sealed class BoundRecordExpression(
    SyntaxNode syntax,
    TypeSymbol type,
    ImmutableArray<(FieldSymbol Field, BoundExpression Value)> fields)
    : BoundExpression(syntax, type) {
    public ImmutableArray<(FieldSymbol Field, BoundExpression Value)> Fields { get; } = fields;
    public override IEnumerable<BoundNode> Children => Fields.Select(static f => f.Value);
}

public sealed class BoundTuple(
    SyntaxNode syntax,
    TypeSymbol type,
    ImmutableArray<BoundExpression> elements)
    : BoundExpression(syntax, type) {
    public ImmutableArray<BoundExpression> Elements { get; } = elements;
    public override IEnumerable<BoundNode> Children => Elements;
}

public sealed class BoundArray(
    SyntaxNode syntax,
    TypeSymbol type,
    ImmutableArray<BoundExpression> elements)
    : BoundExpression(syntax, type) {
    public ImmutableArray<BoundExpression> Elements { get; } = elements;
    public override IEnumerable<BoundNode> Children => Elements;
}

/// <summary>One element of a tuple, named by label or by position.</summary>
public sealed class BoundTupleElementAccess(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression receiver,
    int index)
    : BoundExpression(syntax, type) {
    public BoundExpression Receiver { get; } = receiver;
    public int Index { get; } = index;

    /// <summary>An element of a tuple that can be written can be written: <c>pair.0 = 9</c>.</summary>
    public override bool IsAssignable => Receiver.IsAssignable;

    public override IEnumerable<BoundNode> Children => Nodes(Receiver);
}

public sealed class BoundIndexAccess(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression receiver,
    ImmutableArray<BoundExpression> indices)
    : BoundExpression(syntax, type) {
    public BoundExpression Receiver { get; } = receiver;
    public ImmutableArray<BoundExpression> Indices { get; } = indices;

    /// <summary>The type's own <c>subscript</c> this indexing calls, or null for an array's or a dictionary's.</summary>
    public FunctionSymbol? Subscript { get; init; }

    /// <summary>What the subscript's type's parameters stand for here: <c>Row&lt;String&gt;</c>'s <c>Value</c>.</summary>
    public TypeSubstitution Substitution { get; init; } = TypeSubstitution.Empty;

    /// <summary>
    ///     An array's element or a dictionary's entry may be written; a
    ///     subscript's only when it has a setter and, as for a stored field,
    ///     the value it indexes can be written through.
    /// </summary>
    public override bool IsAssignable =>
        Subscript is null
        || (HasSetter(Subscript) && (Receiver.Type.IsReferenceType || Receiver.IsAssignable));

    /// <summary>True when a subscript declares a <c>set</c>.</summary>
    public static bool HasSetter(FunctionSymbol subscript) =>
        subscript.DeclaringSyntax.FirstOrDefault() is SubscriptDeclarationSyntax { Accessors: { } accessors }
        && accessors.Accessors.Any(static a => a.Keyword.Kind == SyntaxKind.SetKeyword);
    public override IEnumerable<BoundNode> Children => Nodes([Receiver, ..Indices]);
}

/// <summary>
///     Stands in for an expression that could not be bound, carrying whatever was
///     bound successfully so that analysis downstream still sees the shape.
/// </summary>
public sealed class BoundError(SyntaxNode syntax, params BoundExpression[] children)
    : BoundExpression(syntax, ErrorTypeSymbol.Instance) {
    readonly BoundExpression[] _children = children;
    public override IEnumerable<BoundNode> Children => _children;
}

// ---------------------------------------------------------------------------
// Statements
// ---------------------------------------------------------------------------

public sealed class BoundBlock(
    SyntaxNode syntax,
    ImmutableArray<LocalSymbol> locals,
    ImmutableArray<BoundStatement> statements)
    : BoundStatement(syntax) {
    public ImmutableArray<LocalSymbol> Locals { get; } = locals;
    public ImmutableArray<BoundStatement> Statements { get; } = statements;
    public override IEnumerable<BoundNode> Children => Statements;
}

/// <summary>
///     <c>let (a, b) = pair</c> or <c>let { name, age } = person</c>: a
///     declaration whose left side is a pattern, which must always match.
/// </summary>
public sealed class BoundPatternDeclaration(
    SyntaxNode syntax,
    BoundPattern pattern,
    BoundExpression initializer)
    : BoundStatement(syntax) {
    public BoundPattern Pattern { get; } = pattern;
    public BoundExpression Initializer { get; } = initializer;
    public override IEnumerable<BoundNode> Children => [Initializer, Pattern];
}

public sealed class BoundLocalDeclaration(
    SyntaxNode syntax,
    LocalSymbol local,
    BoundExpression? initializer)
    : BoundStatement(syntax) {
    public LocalSymbol Local { get; } = local;
    public BoundExpression? Initializer { get; } = initializer;
    public override IEnumerable<BoundNode> Children => Nodes(Initializer);
}

public sealed class BoundExpressionStatement(SyntaxNode syntax, BoundExpression expression)
    : BoundStatement(syntax) {
    public BoundExpression Expression { get; } = expression;
    public override IEnumerable<BoundNode> Children => Nodes(Expression);
}

public sealed class BoundIf(
    SyntaxNode syntax,
    BoundExpression condition,
    BoundStatement consequence,
    BoundStatement? alternative)
    : BoundStatement(syntax) {
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Consequence { get; } = consequence;
    public BoundStatement? Alternative { get; } = alternative;
    public override IEnumerable<BoundNode> Children => Nodes(Condition, Consequence, Alternative);
}

public sealed class BoundWhile(
    SyntaxNode syntax,
    BoundExpression condition,
    BoundStatement body)
    : BoundStatement(syntax) {
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Body { get; } = body;
    public override IEnumerable<BoundNode> Children => Nodes(Condition, Body);
}

public sealed class BoundRepeat(
    SyntaxNode syntax,
    BoundStatement body,
    BoundExpression condition)
    : BoundStatement(syntax) {
    public BoundStatement Body { get; } = body;
    public BoundExpression Condition { get; } = condition;
    public override IEnumerable<BoundNode> Children => Nodes(Body, Condition);
}

public sealed class BoundForEach(
    SyntaxNode syntax,
    LocalSymbol? element,
    BoundExpression sequence,
    BoundExpression? filter,
    BoundStatement body)
    : BoundStatement(syntax) {
    public LocalSymbol? Element { get; } = element;
    public BoundExpression Sequence { get; } = sequence;
    public BoundExpression? Filter { get; } = filter;
    public BoundStatement Body { get; } = body;
    public override IEnumerable<BoundNode> Children => Nodes(Sequence, Filter, Body);
}

public sealed class BoundReturn(SyntaxNode syntax, BoundExpression? value)
    : BoundStatement(syntax) {
    public BoundExpression? Value { get; } = value;
    public override IEnumerable<BoundNode> Children => Nodes(Value);
}

public sealed class BoundThrow(SyntaxNode syntax, BoundExpression value)
    : BoundStatement(syntax) {
    public BoundExpression Value { get; } = value;
    public override IEnumerable<BoundNode> Children => Nodes(Value);
}

/// <summary><c>yield value</c>: a generator's next element, given as the body pauses.</summary>
public sealed class BoundYield(SyntaxNode syntax, BoundExpression value)
    : BoundStatement(syntax) {
    public BoundExpression Value { get; } = value;
    public override IEnumerable<BoundNode> Children => Nodes(Value);
}

public sealed class BoundBreak(SyntaxNode syntax, string? label) : BoundStatement(syntax) {
    public string? Label { get; } = label;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundContinue(SyntaxNode syntax, string? label) : BoundStatement(syntax) {
    public string? Label { get; } = label;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundDefer(SyntaxNode syntax, BoundStatement body) : BoundStatement(syntax) {
    public BoundStatement Body { get; } = body;
    public override IEnumerable<BoundNode> Children => Nodes(Body);
}

/// <summary>
///     <c>try e</c>, <c>try? e</c>, <c>try! e</c>, or <c>try e catch { ... }</c>.
/// </summary>
public sealed class BoundTry(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression expression,
    TryKind tryKind,
    BoundExpression? fallback) : BoundExpression(syntax, type) {
    public BoundExpression Expression { get; } = expression;
    public TryKind TryKind { get; } = tryKind;

    /// <summary>The value an inline <c>catch</c> supplies instead.</summary>
    public BoundExpression? Fallback { get; } = fallback;

    /// <summary>
    ///     The statements of a <c>catch { ... }</c> block that come before its
    ///     value, run first and in its scope: <see cref="Fallback" /> may use
    ///     the locals they declare. Null when the catch is a lone expression.
    /// </summary>
    public BoundBlock? FallbackBody { get; init; }

    /// <summary>The <c>error</c> an inline <c>catch</c> binds implicitly.</summary>
    public LocalSymbol? ErrorLocal { get; init; }

    public override IEnumerable<BoundNode> Children => Nodes(Expression, FallbackBody, Fallback);
}

/// <summary>What a <c>try</c> does with the error, per SYNTAX.md §18.</summary>
public enum TryKind {
    /// <summary>Propagates to the caller.</summary>
    Propagate,

    /// <summary>Becomes an optional.</summary>
    Optional,

    /// <summary>Asserts it cannot happen.</summary>
    Force,

    /// <summary>Replaced by an inline fallback value.</summary>
    Fallback
}

/// <summary>
///     <c>switch</c>: C-shaped dispatch on integers and enums, with explicit
///     fallthrough. It binds no patterns, which is what separates it from
///     <c>match</c>.
/// </summary>
public sealed class BoundSwitch(
    SyntaxNode syntax,
    BoundExpression subject,
    ImmutableArray<BoundSwitchCase> cases) : BoundStatement(syntax) {
    public BoundExpression Subject { get; } = subject;
    public ImmutableArray<BoundSwitchCase> Cases { get; } = cases;

    public override IEnumerable<BoundNode> Children =>
        Nodes(Subject).Concat(Cases.SelectMany(static c => c.Statements.Cast<BoundNode>()));
}

/// <summary>One <c>case</c> or <c>default</c> of a <c>switch</c>.</summary>
public sealed class BoundSwitchCase(
    SyntaxNode syntax,
    ImmutableArray<BoundExpression> values,
    ImmutableArray<BoundStatement> statements) {
    public SyntaxNode Syntax { get; } = syntax;

    /// <summary>Empty for <c>default</c>.</summary>
    public ImmutableArray<BoundExpression> Values { get; } = values;

    public ImmutableArray<BoundStatement> Statements { get; } = statements;
}

/// <summary><c>fallthrough</c>: carry on into the next case.</summary>
public sealed class BoundFallthrough(SyntaxNode syntax) : BoundStatement(syntax) {
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>
///     A block with something said about it: <c>unsafe</c>, <c>comptime</c>, or
///     a <c>region</c> that names an allocator.
/// </summary>
public sealed class BoundScopedBlock(
    SyntaxNode syntax,
    ScopeKind scopeKind,
    LocalSymbol? name,
    BoundExpression? source,
    BoundBlock body) : BoundStatement(syntax) {
    public ScopeKind ScopeKind { get; } = scopeKind;

    /// <summary>The name a <c>region</c> introduces, if any.</summary>
    public LocalSymbol? Name { get; } = name;

    public BoundExpression? Source { get; } = source;
    public BoundBlock Body { get; } = body;

    public override IEnumerable<BoundNode> Children => Source is null ? Nodes(Body) : Nodes(Source, Body);
}

public enum ScopeKind {
    Unsafe,
    Comptime,
    Region
}

/// <summary>A <c>do</c> block with its <c>catch</c> clauses.</summary>
public sealed class BoundDo(
    SyntaxNode syntax,
    BoundBlock body,
    ImmutableArray<BoundCatch> catches) : BoundStatement(syntax) {
    public BoundBlock Body { get; } = body;
    public ImmutableArray<BoundCatch> Catches { get; } = catches;

    public override IEnumerable<BoundNode> Children =>
        Nodes(Body).Concat(Catches.Select(static c => (BoundNode)c.Body));
}

/// <summary>One <c>catch</c> clause of a <c>do</c>.</summary>
public sealed class BoundCatch(
    SyntaxNode syntax,
    BoundPattern? pattern,
    BoundExpression? guard,
    BoundBlock body,
    LocalSymbol? errorLocal) {
    public SyntaxNode Syntax { get; } = syntax;

    /// <summary>Null for a bare <c>catch</c>, which takes everything.</summary>
    public BoundPattern? Pattern { get; } = pattern;

    /// <summary>The implicit <c>error</c> a bare <c>catch</c> binds.</summary>
    public LocalSymbol? ErrorLocal { get; } = errorLocal;

    public BoundExpression? Guard { get; } = guard;
    public BoundBlock Body { get; } = body;
}

/// <summary>
///     A condition that binds a name: <c>if let x = e</c> or <c>if case p = e</c>.
/// </summary>
/// <remarks>
///     It is an expression of type Bool, so a condition list can combine several
///     with <c>&amp;&amp;</c>, but it carries what it binds — which is what lets
///     definite assignment see that the name is set when the branch is taken.
/// </remarks>
public sealed class BoundConditionBinding(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression source,
    BoundPattern? pattern,
    LocalSymbol? local) : BoundExpression(syntax, type) {
    public BoundExpression Source { get; } = source;
    public BoundPattern? Pattern { get; } = pattern;

    /// <summary>The local an optional binding introduces.</summary>
    public LocalSymbol? Local { get; } = local;

    public override IEnumerable<BoundNode> Children => Pattern is null ? Nodes(Source) : Nodes(Source, Pattern);
}

/// <summary>A function named but not called: the value is the function itself.</summary>
public sealed class BoundFunctionReference(
    SyntaxNode syntax,
    FunctionTypeSymbol type,
    BoundExpression? receiver,
    FunctionSymbol function) : BoundExpression(syntax, type) {
    public BoundExpression? Receiver { get; } = receiver;
    public FunctionSymbol Function { get; } = function;

    /// <summary>
    ///     What the function's type parameters — its own, settled by the
    ///     function type wanted, and its type's, from the receiver — stand for.
    /// </summary>
    public TypeSubstitution Substitution { get; init; } = TypeSubstitution.Empty;

    public override IEnumerable<BoundNode> Children => Nodes(Receiver);
}

/// <summary>A call through a value of function type, with no symbol to name.</summary>
public sealed class BoundIndirectCall(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression callee,
    ImmutableArray<BoundExpression> arguments) : BoundExpression(syntax, type) {
    public BoundExpression Callee { get; } = callee;
    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;

    public override IEnumerable<BoundNode> Children => Nodes(Callee).Concat(Arguments);
}

/// <summary>One name a closure captures from the scope around it.</summary>
public sealed class BoundCapture(
    SyntaxNode syntax,
    Symbol symbol,
    CaptureKind captureKind,
    BoundExpression? initializer) {
    public SyntaxNode Syntax { get; } = syntax;
    public Symbol Symbol { get; } = symbol;
    public CaptureKind CaptureKind { get; } = captureKind;

    /// <summary>The value for <c>[name = expression]</c>, when one was given.</summary>
    public BoundExpression? Initializer { get; } = initializer;
}

/// <summary>How a closure holds onto what it captured, per SYNTAX.md §17.</summary>
public enum CaptureKind {
    Default,
    Weak,
    Unowned,
    ByValue
}

/// <summary>A closure: an anonymous function, with what it captured.</summary>
public sealed class BoundClosure(
    SyntaxNode syntax,
    FunctionTypeSymbol type,
    FunctionSymbol symbol,
    ImmutableArray<BoundCapture> captures,
    BoundBlock body) : BoundExpression(syntax, type) {
    public FunctionSymbol Symbol { get; } = symbol;
    public ImmutableArray<BoundCapture> Captures { get; } = captures;
    public BoundBlock Body { get; } = body;

    /// <summary>
    ///     The closure made of an argument for an <c>@autoclosure</c>
    ///     parameter: its syntax is the expression written, and its body
    ///     returns it.
    /// </summary>
    public bool IsAutoclosure { get; init; }

    public override IEnumerable<BoundNode> Children =>
        Captures.Select(static c => c.Initializer).OfType<BoundNode>().Append(Body);
}

/// <summary>
///     A closure argument whose parameter types are not known yet.
/// </summary>
/// <remarks>
///     It stands in during overload resolution and is replaced by a
///     <see cref="BoundClosure" /> once the chosen signature says what the
///     parameters are. One should never survive into a finished bound tree.
/// </remarks>
public sealed class BoundUnboundClosure(SyntaxNode syntax)
    : BoundExpression(syntax, ErrorTypeSymbol.Instance) {
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>A function declared inside another function's body.</summary>
public sealed class BoundLocalFunction(
    SyntaxNode syntax,
    FunctionSymbol symbol,
    BoundBlock? body) : BoundStatement(syntax) {
    public FunctionSymbol Symbol { get; } = symbol;
    public BoundBlock? Body { get; } = body;
    public override IEnumerable<BoundNode> Children => Body is null ? [] : Nodes(Body);
}

/// <summary>A statement that could not be bound.</summary>
public sealed class BoundErrorStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override IEnumerable<BoundNode> Children => [];
}

/// <summary>
///     A name that resolved to one or more functions. Only ever the callee of a
///     call; overload resolution replaces it with a <see cref="BoundCall" />.
/// </summary>
public sealed class BoundFunctionGroup(
    SyntaxNode syntax,
    BoundExpression? receiver,
    ImmutableArray<FunctionSymbol> candidates)
    : BoundExpression(syntax, ErrorTypeSymbol.Instance) {
    public BoundExpression? Receiver { get; } = receiver;
    public ImmutableArray<FunctionSymbol> Candidates { get; } = candidates;

    /// <summary>What the receiver's type arguments bind, if it was constructed.</summary>
    internal TypeSubstitution? Substitution { get; init; }

    /// <summary>
    ///     True when the receiver was reached through <c>?.</c>, so a call
    ///     through this group gives an optional.
    /// </summary>
    internal bool IsOptionalChain { get; init; }

    public override IEnumerable<BoundNode> Children => Nodes(Receiver);
}

/// <summary>A union case named but not yet applied to a payload.</summary>
public sealed class BoundUnionCaseGroup(
    SyntaxNode syntax,
    TypeSymbol unionType,
    UnionCaseSymbol unionCase)
    : BoundExpression(syntax, unionType) {
    public UnionCaseSymbol UnionCase { get; } = unionCase;
    public override IEnumerable<BoundNode> Children => [];
}

// ---------------------------------------------------------------------------
// Patterns
// ---------------------------------------------------------------------------

/// <summary>A pattern, bound against the type of the value it matches.</summary>
public abstract class BoundPattern(SyntaxNode syntax, TypeSymbol inputType) : BoundNode(syntax) {
    /// <summary>The type of the value this pattern is matched against.</summary>
    public TypeSymbol InputType { get; } = inputType;

    /// <summary>True when the pattern matches every value of its input type.</summary>
    public virtual bool IsIrrefutable => false;
}

public sealed class BoundWildcardPattern(SyntaxNode syntax, TypeSymbol inputType)
    : BoundPattern(syntax, inputType) {
    public override bool IsIrrefutable => true;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundBindingPattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    LocalSymbol local)
    : BoundPattern(syntax, inputType) {
    public LocalSymbol Local { get; } = local;
    public override bool IsIrrefutable => true;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundLiteralPattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    object? value,
    bool isNegative)
    : BoundPattern(syntax, inputType) {
    public object? Value { get; } = value;
    public bool IsNegative { get; } = isNegative;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundRangePattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    BoundPattern? lower,
    BoundPattern? upper)
    : BoundPattern(syntax, inputType) {
    public BoundPattern? Lower { get; } = lower;
    public BoundPattern? Upper { get; } = upper;

    /// <summary>True for <c>..&lt;</c>, which leaves its upper bound out.</summary>
    public bool IsHalfOpen => Syntax is RangePatternSyntax { OperatorToken.Kind: SyntaxKind.DotDotLessToken };

    public override IEnumerable<BoundNode> Children => Nodes(Lower, Upper);
}

public sealed class BoundTuplePattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    ImmutableArray<BoundPattern> elements)
    : BoundPattern(syntax, inputType) {
    public ImmutableArray<BoundPattern> Elements { get; } = elements;
    public override bool IsIrrefutable => Elements.All(static e => e.IsIrrefutable);
    public override IEnumerable<BoundNode> Children => Elements;
}

public sealed class BoundUnionCasePattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    UnionCaseSymbol unionCase,
    ImmutableArray<BoundPattern> payload)
    : BoundPattern(syntax, inputType) {
    public UnionCaseSymbol UnionCase { get; } = unionCase;
    public ImmutableArray<BoundPattern> Payload { get; } = payload;
    public override IEnumerable<BoundNode> Children => Payload;
}

/// <summary>
///     <c>.Some(p)</c>, or <c>p?</c>: an optional that holds a value, which
///     <see cref="Payload" /> matches. (<c>.None</c> is the <c>none</c> literal.)
/// </summary>
public sealed class BoundSomePattern(
    SyntaxNode syntax,
    OptionalTypeSymbol inputType,
    BoundPattern payload)
    : BoundPattern(syntax, inputType) {
    public BoundPattern Payload { get; } = payload;
    public override IEnumerable<BoundNode> Children => [Payload];
}

public sealed class BoundEnumCasePattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    EnumCaseSymbol enumCase)
    : BoundPattern(syntax, inputType) {
    public EnumCaseSymbol EnumCase { get; } = enumCase;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundRecordPattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    ImmutableArray<(FieldSymbol Field, BoundPattern Pattern)> fields)
    : BoundPattern(syntax, inputType) {
    public ImmutableArray<(FieldSymbol Field, BoundPattern Pattern)> Fields { get; } = fields;

    /// <summary>True when the pattern names a narrower type than its input.</summary>
    public bool TestsType { get; init; }

    /// <summary>True when a field it named does not exist and was left out.</summary>
    public bool IsIncomplete { get; init; }

    public override bool IsIrrefutable =>
        !TestsType && !IsIncomplete && Fields.All(static f => f.Pattern.IsIrrefutable);

    public override IEnumerable<BoundNode> Children => Fields.Select(static f => f.Pattern);
}

public sealed class BoundArrayPattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    ImmutableArray<BoundPattern> elements,
    int restIndex)
    : BoundPattern(syntax, inputType) {
    public ImmutableArray<BoundPattern> Elements { get; } = elements;

    /// <summary>Index of the <c>...</c> element, or -1 when there is none.</summary>
    public int RestIndex { get; } = restIndex;

    public override IEnumerable<BoundNode> Children => Elements;
}

public sealed class BoundTypeTestPattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    TypeSymbol testedType,
    LocalSymbol? local)
    : BoundPattern(syntax, inputType) {
    public TypeSymbol TestedType { get; } = testedType;
    public LocalSymbol? Local { get; } = local;
    public override IEnumerable<BoundNode> Children => [];
}

public sealed class BoundOrPattern(
    SyntaxNode syntax,
    TypeSymbol inputType,
    BoundPattern left,
    BoundPattern right)
    : BoundPattern(syntax, inputType) {
    public BoundPattern Left { get; } = left;
    public BoundPattern Right { get; } = right;
    public override bool IsIrrefutable => Left.IsIrrefutable || Right.IsIrrefutable;
    public override IEnumerable<BoundNode> Children => Nodes(Left, Right);
}

/// <summary>
///     A pattern that did not bind. <paramref name="nested" /> are the
///     sub-patterns bound anyway, so the names they declare count as written
///     and the error is reported once.
/// </summary>
public sealed class BoundErrorPattern(SyntaxNode syntax, ImmutableArray<BoundPattern> nested = default)
    : BoundPattern(syntax, ErrorTypeSymbol.Instance) {
    public ImmutableArray<BoundPattern> Nested { get; } = nested.IsDefault ? [] : nested;
    public override IEnumerable<BoundNode> Children => Nested;
}

// ---------------------------------------------------------------------------
// match
// ---------------------------------------------------------------------------

public sealed class BoundMatchArm(
    SyntaxNode syntax,
    ImmutableArray<BoundPattern> patterns,
    BoundExpression? guard,
    BoundNode body)
    : BoundNode(syntax) {
    public ImmutableArray<BoundPattern> Patterns { get; } = patterns;
    public BoundExpression? Guard { get; } = guard;

    /// <summary>An expression or a block, depending on how the arm was written.</summary>
    public BoundNode Body { get; } = body;

    /// <summary>True for <c>default</c>, which has no patterns.</summary>
    public bool IsDefault => Patterns.IsEmpty;

    public override IEnumerable<BoundNode> Children => Nodes([..Patterns, Guard, Body]);
}

public sealed class BoundMatch(
    SyntaxNode syntax,
    TypeSymbol type,
    BoundExpression subject,
    ImmutableArray<BoundMatchArm> arms)
    : BoundExpression(syntax, type) {
    public BoundExpression Subject { get; } = subject;
    public ImmutableArray<BoundMatchArm> Arms { get; } = arms;
    public override IEnumerable<BoundNode> Children => Nodes([Subject, ..Arms]);
}
