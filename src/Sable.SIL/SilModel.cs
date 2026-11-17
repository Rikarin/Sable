using System.Collections.Immutable;

namespace Sable.SIL;

// ===========================================================================
// Types
// ===========================================================================

/// <summary>
///     A SIL type: what a value is at the level of storage and calling
///     convention, after the language's types have been lowered.
/// </summary>
/// <remarks>
///     Scalars, addresses, functions, tuples, structs, enums, classes and
///     arrays. Written with a leading <c>$</c> in SIL text: <c>$Int</c>,
///     <c>$*Int</c>, <c>$(Int, Bool) -&gt; Int</c>, <c>$[String]</c>.
/// </remarks>
public abstract class SilType : IEquatable<SilType> {
    public static readonly SilType Int = new ScalarType("Int");
    public static readonly SilType Bool = new ScalarType("Bool");
    public static readonly SilType F64 = new ScalarType("F64");

    /// <summary>One Unicode scalar value.</summary>
    public static readonly SilType Char = new ScalarType("Char");

    /// <summary>
    ///     A string: a reference to a counted heap object holding UTF-8 bytes.
    ///     A literal is an immortal object (docs/02 §2.2), whose count is never
    ///     touched; a built one is freed when its last reference goes.
    /// </summary>
    public static readonly SilType String = new ScalarType("String", trivial: false);

    /// <summary>
    ///     A reference to some counted object, of a type not known here: a
    ///     closure's context, as the closure's implementation receives it.
    ///     It may be null, for a closure that captured nothing.
    /// </summary>
    public static readonly SilType Object = new ScalarType("Object", trivial: false);

    /// <summary><c>()</c>: no value. What a function that returns nothing returns.</summary>
    public static readonly SilType Unit = new ScalarType("()");

    public static SilType Address(SilType pointee) => new AddressType(pointee);

    public static SilType Function(ImmutableArray<SilType> parameters, SilType result) =>
        new FunctionType(parameters, result);

    public static SilType Tuple(ImmutableArray<SilType> elements) =>
        elements.IsEmpty ? Unit : new TupleType(elements);

    /// <summary>True for a value with no ownership to track: copied by copying its bits.</summary>
    public virtual bool IsTrivial => true;

    public abstract override string ToString();

    public bool Equals(SilType? other) => other is not null && ToString() == other.ToString();
    public override bool Equals(object? obj) => Equals(obj as SilType);
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(SilType? a, SilType? b) => a?.Equals(b) ?? b is null;
    public static bool operator !=(SilType? a, SilType? b) => !(a == b);
}

public sealed class ScalarType(string name, bool trivial = true) : SilType {
    public string Name { get; } = name;
    public override bool IsTrivial => trivial;
    public override string ToString() => Name;
}

/// <summary>
///     <c>[T]</c>: a growable array with value semantics — a reference to a
///     counted buffer, copied on write when shared. The empty array is no
///     buffer at all.
/// </summary>
public sealed class ArrayType(SilType element) : SilType {
    public SilType Element { get; } = element;
    public override bool IsTrivial => false;
    public override string ToString() => $"[{Element}]";
}

/// <summary><c>*T</c>: the address of storage holding a <c>T</c>.</summary>
public sealed class AddressType(SilType pointee) : SilType {
    public SilType Pointee { get; } = pointee;
    public override string ToString() => $"*{Pointee}";
}

public sealed class FunctionType(ImmutableArray<SilType> parameters, SilType result, SilType? thrown = null) : SilType {
    public ImmutableArray<SilType> Parameters { get; } = parameters;
    public SilType Result { get; } = result;

    /// <summary>The error a <c>throws(E)</c> function may throw, or null when it cannot.</summary>
    public SilType? Thrown { get; } = thrown;

    public override string ToString() =>
        Thrown is null
            ? $"({string.Join(", ", Parameters)}) -> {Result}"
            : $"({string.Join(", ", Parameters)}) throws({Thrown}) -> {Result}";
}

/// <summary>
///     <c>@thick (Int) -&gt; Int</c>: a function as a value of the language —
///     code and a counted context holding what it captured. Calling one calls
///     the code with the arguments and then the context, so its code has the
///     <see cref="Implementation" /> type: the same with an <c>Object</c> last.
/// </summary>
public sealed class ThickFunctionType(FunctionType signature) : SilType {
    public FunctionType Signature { get; } = signature;

    public FunctionType Implementation =>
        new([..Signature.Parameters, Object], Signature.Result, Signature.Thrown);

    public override bool IsTrivial => false;
    public override string ToString() => $"@thick {Signature}";
}

/// <summary><c>(Int, Bool)</c>: a tuple, laid out inline. The empty tuple is <see cref="SilType.Unit" />.</summary>
public sealed class TupleType(ImmutableArray<SilType> elements) : SilType {
    public ImmutableArray<SilType> Elements { get; } = elements;
    public override bool IsTrivial => Elements.All(static e => e.IsTrivial);
    public override string ToString() => $"({string.Join(", ", Elements)})";
}

/// <summary>A named type a module declares: a struct, an enum or a class.</summary>
public abstract class NominalType(string name) : SilType {
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>
///     A struct: named fields laid out inline, in order. Declared in SIL text
///     as <c>sil_struct Point { x: $Int, y: $Int }</c>.
/// </summary>
public sealed class StructType(string name) : NominalType(name) {
    public List<(string Name, SilType Type)> Fields { get; } = [];

    public int FieldIndex(string field) => Fields.FindIndex(f => f.Name == field);

    public override bool IsTrivial => Fields.All(static f => f.Type.IsTrivial);
}

/// <summary>
///     A tagged union: cases, each with at most one payload — several values
///     are one tuple. Unions, enums and optionals all lower to it. Declared as
///     <c>sil_enum Shape { Circle($F64), Empty }</c>.
/// </summary>
public sealed class EnumType(string name) : NominalType(name) {
    public List<(string Name, SilType? Payload)> Cases { get; } = [];

    public int CaseIndex(string @case) => Cases.FindIndex(c => c.Name == @case);

    public override bool IsTrivial => Cases.All(static c => c.Payload?.IsTrivial ?? true);
}

/// <summary>
///     A class: a reference to a counted heap object with identity, whose
///     fields live in the object. Declared as
///     <c>sil_class Node { value: $Int, next: $Optional$Node }</c>; a field
///     may name the class itself, since it is reached through a reference.
/// </summary>
public sealed class ClassType(string name) : NominalType(name) {
    public List<(string Name, SilType Type)> Fields { get; } = [];

    public int FieldIndex(string field) => Fields.FindIndex(f => f.Name == field);

    /// <summary>The function that runs as an object dies, before its fields are destroyed, or null.</summary>
    public string? Deinit { get; set; }

    public override bool IsTrivial => false;
}

// ===========================================================================
// Values
// ===========================================================================

/// <summary>Something an instruction can use: a block argument, or an instruction's result.</summary>
public abstract class SilValue {
    public abstract SilType Type { get; }
}

/// <summary>
///     How a value is held (docs/04 §2.1). A trivial value has nothing to
///     track. An owned one must be consumed exactly once on every path — by a
///     store, a return, a branch, an aggregate taking it in, or
///     <c>destroy_value</c>. A guaranteed one is borrowed: it may be used, and
///     copied, while whatever it was borrowed from is alive, but never consumed.
/// </summary>
public enum Ownership {
    Trivial,
    Owned,
    Guaranteed,
}

/// <summary>A value passed into a block: a function's parameters, or what a branch sends.</summary>
/// <remarks>
///     A function's parameters are guaranteed: the caller keeps them alive for
///     the call. Other arguments are owned, except where a <c>switch_enum</c>
///     hands a block the payload it borrows from the enum, marked
///     <c>@guaranteed</c> in SIL text.
/// </remarks>
public sealed class SilArgument(SilBlock block, SilType type, bool guaranteed = false) : SilValue {
    public SilBlock Block { get; } = block;
    public override SilType Type { get; } = type;

    /// <summary>True for an argument that borrows rather than owns, outside the entry block.</summary>
    public bool IsGuaranteed { get; } = guaranteed;
}

// ===========================================================================
// Instructions
// ===========================================================================

/// <summary>Where in the source an instruction came from: one-based line and column.</summary>
public readonly record struct SilLocation(int Line, int Column) {
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>
///     One SIL instruction. An instruction that produces a value is itself the
///     value, as in Swift's SIL, so a use names the instruction.
/// </summary>
public abstract class SilInstruction : SilValue {
    readonly SilValue[] _operands;

    protected SilInstruction(params SilValue[] operands) {
        _operands = operands;
    }

    public SilBlock Block { get; internal set; } = null!;

    /// <summary>The source position it was lowered from, for debug information.</summary>
    public SilLocation? Location { get; set; }

    public IReadOnlyList<SilValue> Operands => _operands;

    internal void SetOperand(int index, SilValue value) => _operands[index] = value;

    /// <summary>The text that names the instruction in SIL: <c>apply</c>, <c>cond_br</c>.</summary>
    public abstract string Opcode { get; }

    /// <summary>True when the instruction produces a value that can be named.</summary>
    public virtual bool HasResult => true;

    /// <summary>True for an instruction that ends a block.</summary>
    public virtual bool IsTerminator => false;

    public override SilType Type => SilType.Unit;
}

/// <summary><c>integer_literal $Int, 42</c>, or <c>$Bool, 1</c> for true.</summary>
public sealed class IntegerLiteralInst(SilType type, long value) : SilInstruction {
    public override SilType Type { get; } = type;
    public long Value { get; } = value;
    public override string Opcode => "integer_literal";
}

/// <summary><c>float_literal $F64, 1.5</c>.</summary>
public sealed class FloatLiteralInst(double value) : SilInstruction {
    public override SilType Type => SilType.F64;
    public double Value { get; } = value;
    public override string Opcode => "float_literal";
}

/// <summary><c>string_literal "hello"</c>: an immortal string.</summary>
public sealed class StringLiteralInst(string value) : SilInstruction {
    public override SilType Type => SilType.String;
    public string Value { get; } = value;
    public override string Opcode => "string_literal";
}

/// <summary>
///     <c>builtin "add_Int"(%0, %1) : $Int</c>: an operation the compiler
///     knows, named in <see cref="SilBuiltins" />. One that gives <c>()</c>,
///     such as <c>print_String</c>, is run for its effect and names nothing.
/// </summary>
public sealed class BuiltinInst(string name, SilType type, params SilValue[] operands) : SilInstruction(operands) {
    public string Name { get; } = name;
    public override SilType Type { get; } = type;
    public override bool HasResult => Type != SilType.Unit;
    public override string Opcode => "builtin";
}

/// <summary><c>struct $Point (%0, %1)</c>: a struct from its fields, in order.</summary>
public sealed class StructInst(StructType type, params SilValue[] fields) : SilInstruction(fields) {
    public StructType StructType { get; } = type;
    public override SilType Type => StructType;
    public override string Opcode => "struct";
}

/// <summary><c>struct_extract %0, #x</c>: one field of a struct value.</summary>
public sealed class StructExtractInst(SilValue value, int field) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public int Field { get; } = field;
    public override SilType Type => ((StructType)Value.Type).Fields[Field].Type;
    public override string Opcode => "struct_extract";
}

/// <summary><c>struct_element_addr %0, #x</c>: the address of one field of a struct in memory.</summary>
public sealed class StructElementAddrInst(SilValue address, int field) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public int Field { get; } = field;
    public override SilType Type => SilType.Address(((StructType)((AddressType)Address.Type).Pointee).Fields[Field].Type);
    public override string Opcode => "struct_element_addr";
}

/// <summary><c>tuple $(Int, Bool) (%0, %1)</c>.</summary>
public sealed class TupleInst(TupleType type, params SilValue[] elements) : SilInstruction(elements) {
    public TupleType TupleType { get; } = type;
    public override SilType Type => TupleType;
    public override string Opcode => "tuple";
}

/// <summary><c>tuple_extract %0, 1</c>.</summary>
public sealed class TupleExtractInst(SilValue value, int element) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public int Element { get; } = element;
    public override SilType Type => ((TupleType)Value.Type).Elements[Element];
    public override string Opcode => "tuple_extract";
}

/// <summary><c>tuple_element_addr %0, 1</c>.</summary>
public sealed class TupleElementAddrInst(SilValue address, int element) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public int Element { get; } = element;
    public override SilType Type => SilType.Address(((TupleType)((AddressType)Address.Type).Pointee).Elements[Element]);
    public override string Opcode => "tuple_element_addr";
}

/// <summary><c>enum $Shape, #Circle, %0</c>, or without a payload for a case that has none.</summary>
public sealed class EnumInst(EnumType type, int @case, SilValue? payload) : SilInstruction(payload is null ? [] : [payload]) {
    public EnumType EnumType { get; } = type;
    public int Case { get; } = @case;
    public SilValue? Payload => Operands.Count == 0 ? null : Operands[0];
    public override SilType Type => EnumType;
    public override string Opcode => "enum";
}

/// <summary><c>cond_fail %0, "message"</c>: stops the program with the message when the condition is true.</summary>
public sealed class CondFailInst(SilValue condition, string message) : SilInstruction(condition) {
    public SilValue Condition => Operands[0];
    public string Message { get; } = message;
    public override bool HasResult => false;
    public override string Opcode => "cond_fail";
}

/// <summary><c>alloc_stack $T</c>: storage in the current frame, popped by <c>dealloc_stack</c>.</summary>
/// <remarks>
///     Storage for a non-trivial type starts <i>empty</i>, holding no value
///     and nothing to release: <c>store [assign]</c> into it destroys nothing,
///     and <c>destroy_addr</c> of it does nothing. So a slot can be assigned
///     on every path without knowing whether it was assigned before, and
///     destroyed once where the function leaves. Natively, empty is all zeros.
/// </remarks>
public sealed class AllocStackInst(SilType elementType) : SilInstruction {
    public SilType ElementType { get; } = elementType;

    /// <summary>The source variable the slot holds, for a debugger; null for a temporary.</summary>
    public string? VariableName { get; init; }
    public override SilType Type => SilType.Address(ElementType);
    public override string Opcode => "alloc_stack";
}

/// <summary><c>dealloc_stack %0</c>: frees the most recent <c>alloc_stack</c>.</summary>
public sealed class DeallocStackInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override bool HasResult => false;
    public override string Opcode => "dealloc_stack";
}

public enum LoadKind {
    /// <summary>A trivial value: its bits.</summary>
    Trivial,

    /// <summary>A copy of the value, which the storage keeps.</summary>
    Copy,

    /// <summary>The value itself, leaving the storage empty.</summary>
    Take,
}

/// <summary><c>load [trivial|copy|take] %0</c>: reads the value at an address.</summary>
public sealed class LoadInst(SilValue address, LoadKind? kind = null) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override SilType Type => (Address.Type as AddressType)?.Pointee ?? SilType.Unit;

    /// <summary>Unless given, trivial for a trivial type and a copy otherwise.</summary>
    public LoadKind Kind { get; } = kind ?? ((address.Type as AddressType)?.Pointee.IsTrivial ?? true ? LoadKind.Trivial : LoadKind.Copy);

    public override string Opcode => "load";
}

public enum StoreKind {
    /// <summary>The storage held nothing before.</summary>
    Init,

    /// <summary>The storage may hold a value, which this destroys and replaces.</summary>
    Assign,
}

/// <summary><c>store %1 to [init] %0</c>.</summary>
public sealed class StoreInst(SilValue value, SilValue address, StoreKind kind) : SilInstruction(value, address) {
    public SilValue Value => Operands[0];
    public SilValue Address => Operands[1];
    public StoreKind Kind { get; } = kind;
    public override bool HasResult => false;
    public override string Opcode => "store";
}

/// <summary><c>copy_value %0</c>: a new owned copy of a value — a retain, for a reference.</summary>
public sealed class CopyValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type => Value.Type;
    public override string Opcode => "copy_value";
}

/// <summary><c>destroy_value %0</c>: consumes an owned value — a release, for a reference.</summary>
public sealed class DestroyValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override bool HasResult => false;
    public override string Opcode => "destroy_value";
}

/// <summary><c>destroy_addr %0</c>: destroys the value in storage, leaving it empty.</summary>
public sealed class DestroyAddrInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override bool HasResult => false;
    public override string Opcode => "destroy_addr";
}

/// <summary><c>alloc_ref $Node</c>: a new object, its count one and every field empty.</summary>
public sealed class AllocRefInst(ClassType type) : SilInstruction {
    public ClassType ClassType { get; } = type;
    public override SilType Type => ClassType;
    public override string Opcode => "alloc_ref";
}

/// <summary><c>ref_element_addr %0, #next</c>: the address of a field inside an object.</summary>
public sealed class RefElementAddrInst(SilValue reference, int field) : SilInstruction(reference) {
    public SilValue Reference => Operands[0];
    public int Field { get; } = field;
    public override SilType Type => SilType.Address(((ClassType)Reference.Type).Fields[Field].Type);
    public override string Opcode => "ref_element_addr";
}

/// <summary>
///     <c>partial_apply %f(%context) : $@thick (Int) -&gt; Int</c>: a closure —
///     the code, whose last parameter takes the context, with the context
///     object, which it consumes. With no context, the closure captured
///     nothing and its context is null.
/// </summary>
public sealed class PartialApplyInst(SilValue function, SilValue? context, ThickFunctionType type)
    : SilInstruction(context is null ? [function] : [function, context]) {
    public SilValue Function => Operands[0];
    public SilValue? Context => Operands.Count > 1 ? Operands[1] : null;
    public override SilType Type { get; } = type;
    public ThickFunctionType ThickType => (ThickFunctionType)Type;
    public override string Opcode => "partial_apply";
}

/// <summary>
///     <c>unchecked_ref_cast %0 : $Context</c>: an <c>Object</c> seen as the
///     class it is known to be — a closure's context, inside its code. The
///     result is borrowed from the operand.
/// </summary>
public sealed class UncheckedRefCastInst(SilValue value, ClassType type) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type { get; } = type;
    public override string Opcode => "unchecked_ref_cast";
}

/// <summary><c>function_ref @f : $(Int) -&gt; Int</c>: a function as a value.</summary>
public sealed class FunctionRefInst(string function, FunctionType type) : SilInstruction {
    public string Function { get; } = function;
    public override SilType Type { get; } = type;
    public override string Opcode => "function_ref";
}

/// <summary><c>apply %f(%0, %1) : $Int</c>: calls a function value.</summary>
public sealed class ApplyInst(SilValue callee, SilType resultType, params SilValue[] arguments)
    : SilInstruction([callee, ..arguments]) {
    public SilValue Callee => Operands[0];
    public IEnumerable<SilValue> Arguments => Operands.Skip(1);
    public override SilType Type { get; } = resultType;
    public override bool HasResult => Type != SilType.Unit;
    public override string Opcode => "apply";
}

/// <summary><c>br bb2(%0)</c>: jumps, passing values to the target's arguments.</summary>
public sealed class BranchInst(SilBlock target, params SilValue[] arguments) : SilInstruction(arguments) {
    public SilBlock Target { get; internal set; } = target;
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "br";
}

/// <summary><c>cond_br %c, bb1(%a), bb2</c>.</summary>
public sealed class CondBranchInst(
    SilValue condition,
    SilBlock whenTrue,
    SilValue[] trueArguments,
    SilBlock whenFalse,
    SilValue[] falseArguments) : SilInstruction([condition, ..trueArguments, ..falseArguments]) {
    public SilValue Condition => Operands[0];
    public SilBlock WhenTrue { get; internal set; } = whenTrue;
    public SilBlock WhenFalse { get; internal set; } = whenFalse;
    public int TrueArgumentCount { get; } = trueArguments.Length;
    public IEnumerable<SilValue> TrueArguments => Operands.Skip(1).Take(TrueArgumentCount);
    public IEnumerable<SilValue> FalseArguments => Operands.Skip(1 + TrueArgumentCount);
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "cond_br";
}

/// <summary><c>return %0</c>, or a bare <c>return</c> from a function returning <c>()</c>.</summary>
public sealed class ReturnInst(SilValue? value) : SilInstruction(value is null ? [] : [value]) {
    public SilValue? Value => Operands.Count == 0 ? null : Operands[0];
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "return";
}

/// <summary>
///     <c>switch_enum %0, case #A: bb1, case #B: bb2, default bb3</c>: branches
///     on an enum's case. A case block takes the payload as its one argument
///     when the case has one; the default block takes nothing.
/// </summary>
public sealed class SwitchEnumInst(SilValue value, List<(int Case, SilBlock Target)> cases, SilBlock? defaultTarget)
    : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public List<(int Case, SilBlock Target)> Cases { get; } = cases;
    public SilBlock? Default { get; internal set; } = defaultTarget;
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "switch_enum";
}

/// <summary><c>throw %0</c>: leaves a <c>throws</c> function with an error.</summary>
public sealed class ThrowInst(SilValue error) : SilInstruction(error) {
    public SilValue Error => Operands[0];
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "throw";
}

/// <summary>
///     <c>try_apply %f(%0) : $Int, normal bb1, error bb2</c>: calls a throwing
///     function. The normal block takes the result (nothing for <c>()</c>);
///     the error block takes the error.
/// </summary>
public sealed class TryApplyInst(SilValue callee, SilType resultType, SilValue[] arguments, SilBlock normal, SilBlock error)
    : SilInstruction([callee, ..arguments]) {
    public SilValue Callee => Operands[0];
    public IEnumerable<SilValue> Arguments => Operands.Skip(1);
    public SilType ResultType { get; } = resultType;
    public SilBlock Normal { get; internal set; } = normal;
    public SilBlock Error { get; internal set; } = error;
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "try_apply";
}

/// <summary><c>unreachable</c>: control cannot get here.</summary>
public sealed class UnreachableInst() : SilInstruction {
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "unreachable";
}

// ===========================================================================
// Blocks, functions, modules
// ===========================================================================

public sealed class SilBlock(SilFunction function) {
    public SilFunction Function { get; } = function;
    public List<SilArgument> Arguments { get; } = [];
    public List<SilInstruction> Instructions { get; } = [];

    public SilInstruction? Terminator =>
        Instructions.Count > 0 && Instructions[^1].IsTerminator ? Instructions[^1] : null;

    public IEnumerable<SilBlock> Successors =>
        Terminator switch {
            BranchInst branch => [branch.Target],
            CondBranchInst conditional => [conditional.WhenTrue, conditional.WhenFalse],
            SwitchEnumInst switchEnum => [..switchEnum.Cases.Select(static c => c.Target), ..(switchEnum.Default is { } d ? [d] : Array.Empty<SilBlock>())],
            TryApplyInst tryApply => [tryApply.Normal, tryApply.Error],
            _ => [],
        };

    public SilArgument AddArgument(SilType type, bool guaranteed = false) {
        var argument = new SilArgument(this, type, guaranteed);
        Arguments.Add(argument);
        return argument;
    }

    public T Append<T>(T instruction) where T : SilInstruction {
        instruction.Block = this;
        Instructions.Add(instruction);
        return instruction;
    }
}

/// <summary>A SIL function: a signature and blocks, the first of which is the entry.</summary>
public sealed class SilFunction(string name, FunctionType type) {
    public string Name { get; } = name;
    public FunctionType Type { get; } = type;
    public List<SilBlock> Blocks { get; } = [];

    /// <summary>The source file and line it was declared at, for debug information.</summary>
    public string? SourceFile { get; set; }

    public int Line { get; set; }

    /// <summary>The name of each parameter, <c>self</c> included, for a debugger.</summary>
    public List<string> ParameterNames { get; } = [];

    public SilBlock Entry => Blocks[0];

    public SilBlock AddBlock() {
        var block = new SilBlock(this);
        Blocks.Add(block);
        return block;
    }

    /// <summary>Drops blocks control cannot reach from the entry.</summary>
    public void RemoveUnreachableBlocks() {
        if (Blocks.Count == 0) {
            return;
        }

        var seen = new HashSet<SilBlock> { Entry };
        var pending = new Stack<SilBlock>();
        pending.Push(Entry);
        while (pending.Count > 0) {
            foreach (var next in pending.Pop().Successors) {
                if (seen.Add(next)) {
                    pending.Push(next);
                }
            }
        }

        Blocks.RemoveAll(block => !seen.Contains(block));
    }
}

public sealed class SilModule {
    readonly Dictionary<string, SilFunction> _byName = new(StringComparer.Ordinal);

    public List<SilFunction> Functions { get; } = [];

    /// <summary>The function a program starts in: the one marked <c>@main</c>, or else <c>main</c>.</summary>
    public string? EntryPoint { get; set; }

    readonly Dictionary<string, NominalType> _types = new(StringComparer.Ordinal);

    /// <summary>The structs and enums the module declares, in declaration order.</summary>
    public List<NominalType> Types { get; } = [];

    public NominalType? FindType(string name) => _types.GetValueOrDefault(name);

    public T AddType<T>(T type) where T : NominalType {
        Types.Add(type);
        _types[type.Name] = type;
        return type;
    }

    public void RemoveType(NominalType type) {
        Types.Remove(type);
        _types.Remove(type.Name);
    }

    public SilFunction? Find(string name) => _byName.GetValueOrDefault(name);

    public SilFunction Add(SilFunction function) {
        Functions.Add(function);
        _byName[function.Name] = function;
        return function;
    }

    public override string ToString() => SilPrinter.Print(this);
}

// ===========================================================================
// Builtins
// ===========================================================================

/// <summary>
///     The operations <c>builtin</c> may name: arithmetic, comparison and
///     logic on the scalar types, the string and character operations the
///     runtime provides, and the array operations. Integer arithmetic traps on
///     overflow, as the language's safety promises; floating-point does not.
/// </summary>
/// <remarks>
///     A builtin borrows its operands, except those its signature says it
///     consumes — the element <c>array_append</c> stores — and gives an owned
///     result. The array operations work for any element type, so their
///     signatures are worked out from the operands.
/// </remarks>
public static class SilBuiltins {
    public sealed record Signature(ImmutableArray<SilType> Operands, SilType Result) {
        /// <summary>The operands the builtin takes ownership of.</summary>
        public ImmutableArray<int> Consumed { get; init; } = [];
    }

    static readonly Dictionary<string, Signature> Table = Build();

    /// <summary>The names whose signature depends on the element type of the array they work on.</summary>
    public static readonly ImmutableArray<string> ArrayOperations =
        ["array_empty", "array_count", "array_get", "array_append", "array_set", "array_remove_last", "array_clear"];

    /// <summary>A builtin with fixed operand types, by name.</summary>
    public static Signature? Find(string name) => Table.GetValueOrDefault(name);

    /// <summary>
    ///     The signature of a builtin applied to operands of these types, or
    ///     null when it does not apply. <c>array_empty</c> takes nothing, so
    ///     its element type comes from the result it is declared to give.
    /// </summary>
    public static Signature? Resolve(string name, IReadOnlyList<SilType> operands, SilType? result = null) {
        if (Table.TryGetValue(name, out var fixedSignature)) {
            return fixedSignature;
        }

        if (name == "array_empty") {
            return operands.Count == 0 && result is ArrayType ? new([], result) : null;
        }

        SilType? Element(int index, bool address) {
            if (index >= operands.Count) {
                return null;
            }

            var type = address ? (operands[index] as AddressType)?.Pointee : operands[index];
            return (type as ArrayType)?.Element;
        }

        switch (name) {
            case "array_count" when Element(0, false) is not null:
                return new([operands[0]], SilType.Int);

            case "array_get" when Element(0, false) is { } element:
                return new([operands[0], SilType.Int], element);

            case "array_append" when Element(0, true) is { } element:
                return new([operands[0], element], SilType.Unit) { Consumed = [1] };

            case "array_set" when Element(0, true) is { } element:
                return new([operands[0], SilType.Int, element], SilType.Unit) { Consumed = [2] };

            case "array_remove_last" when Element(0, true) is { } element:
                return new([operands[0]], element);

            case "array_clear" when Element(0, true) is not null:
                return new([operands[0]], SilType.Unit);

            default:
                return null;
        }
    }

    public static IEnumerable<string> Names => Table.Keys.Concat(ArrayOperations);

    static Dictionary<string, Signature> Build() {
        var table = new Dictionary<string, Signature>(StringComparer.Ordinal);

        void Binary(string op, SilType operand, SilType result) =>
            table[$"{op}_{operand}"] = new([operand, operand], result);

        void Unary(string op, SilType operand) => table[$"{op}_{operand}"] = new([operand], operand);

        void Add(string name, SilType result, params SilType[] operands) => table[name] = new([..operands], result);

        foreach (var op in new[] { "add", "sub", "mul", "div", "rem", "and", "or", "xor", "shl", "shr", "min", "max" }) {
            Binary(op, SilType.Int, SilType.Int);
        }

        foreach (var op in new[] { "add", "sub", "mul", "div", "min", "max" }) {
            Binary(op, SilType.F64, SilType.F64);
        }

        foreach (var op in new[] { "and", "or", "xor" }) {
            Binary(op, SilType.Bool, SilType.Bool);
        }

        foreach (var type in new[] { SilType.Int, SilType.F64, SilType.Bool, SilType.Char, SilType.String }) {
            Binary("cmp_eq", type, SilType.Bool);
            Binary("cmp_ne", type, SilType.Bool);
        }

        foreach (var type in new[] { SilType.Int, SilType.F64, SilType.Char }) {
            foreach (var op in new[] { "cmp_lt", "cmp_le", "cmp_gt", "cmp_ge" }) {
                Binary(op, type, SilType.Bool);
            }
        }

        foreach (var printable in new[] { SilType.String, SilType.Int, SilType.F64, SilType.Bool }) {
            table[$"print_{printable}"] = new([printable], SilType.Unit);
        }

        table["convert_Int_F64"] = new([SilType.Int], SilType.F64);
        table["convert_F64_Int"] = new([SilType.F64], SilType.Int);

        Unary("neg", SilType.Int);
        Unary("neg", SilType.F64);
        Unary("not", SilType.Int);
        Unary("not", SilType.Bool);
        Unary("abs", SilType.Int);
        Unary("abs", SilType.F64);

        // Strings. A string is indexed by Unicode scalar: 'count' scalars,
        // 'at' one of them, 'slice' a run of them.
        Add("string_concat", SilType.String, SilType.String, SilType.String);
        Add("string_count", SilType.Int, SilType.String);
        Add("string_at", SilType.Char, SilType.String, SilType.Int);
        Add("string_slice", SilType.String, SilType.String, SilType.Int, SilType.Int);
        Add("string_contains", SilType.Bool, SilType.String, SilType.Char);
        Add("string_index_of", SilType.Int, SilType.String, SilType.Char);
        Add("string_trimmed", SilType.String, SilType.String);
        Add("string_split", new ArrayType(SilType.String), SilType.String, SilType.Char);
        Add("string_from_Char", SilType.String, SilType.Char);
        Add("string_from_Int", SilType.String, SilType.Int);
        Add("string_from_F64", SilType.String, SilType.F64);
        Add("string_from_Bool", SilType.String, SilType.Bool);

        // Parsing gives whether it worked, and the value when it did.
        Add("string_to_Int", SilType.Tuple([SilType.Bool, SilType.Int]), SilType.String);
        Add("string_to_F64", SilType.Tuple([SilType.Bool, SilType.F64]), SilType.String);

        Add("char_to_Int", SilType.Int, SilType.Char);
        Add("char_from_Int", SilType.Tuple([SilType.Bool, SilType.Char]), SilType.Int);
        Add("char_is_digit", SilType.Bool, SilType.Char);
        Add("char_is_letter", SilType.Bool, SilType.Char);
        Add("char_is_whitespace", SilType.Bool, SilType.Char);

        return table;
    }
}
