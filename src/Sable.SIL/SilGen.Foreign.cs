using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     C function pointers (SYNTAX.md §24): what Sable.Interop's
///     <c>CFunction</c> and <c>CCallback</c> are made of.
/// </summary>
/// <remarks>
///     <para>
///         A function Sable gives C is an entry of C's calling convention
///         (LlvmEmitter's <c>ExportedFunctions</c>) over a SIL function of C's
///         types, whose address the builtin <c>c_address</c> is. What captures
///         nothing is called by name; a closure is found again from the
///         context C hands back, which the runtime (runtime/foreign.c) looks
///         up and traps for if it is not one any more.
///     </para>
///     <para>
///         A function C gives Sable is a closure over its address: a thunk
///         that calls through <see cref="IndirectSymbol" />, the module's
///         declaration of a C function whose first parameter is its code.
///     </para>
/// </remarks>
public sealed partial class SilGen {
    /// <summary>In <see cref="SilModule.Foreign" />, the C symbol of a function called through its address, its first parameter.</summary>
    public const string IndirectSymbol = "*";

    /// <summary>One of the runtime's C functions, declared and named.</summary>
    FunctionRefInst RuntimeFunction(string symbol, FunctionType type) {
        var name = "c." + symbol;
        _module.Foreign[name] = (symbol, type);
        return new(name, type);
    }

    /// <summary>The SIL function of C's types whose C entry calls the closure the context — its first parameter, or its last — names.</summary>
    string? ContextEntry(ThickFunctionType thick, bool first) {
        var signature = thick.Signature;
        if (signature.Parameters.Length == 0 || PointerPath(signature.Parameters[first ? 0 : ^1]) is not { } path) {
            return null;
        }

        var name = $"c.context.{(first ? "first" : "last")}${Sanitise(signature.ToString())}";
        if (_module.Find(name) is not null) {
            return name;
        }

        var entry = new SilFunction(name, signature);
        var block = entry.AddBlock();
        var arguments = signature.Parameters.Select(p => (SilValue)block.AddArgument(p)).ToArray();
        var context = path.Aggregate(arguments[first ? 0 : ^1], (value, _) => block.Append(new StructExtractInst(value, 0)));
        var closureAt = block.Append(new ApplyInst(block.Append(RuntimeFunction("sable_callback_enter", new([SilType.Int], SilType.Int))), SilType.Int, context));
        // The table's, not counted: read as if moved out, and given back as if
        // moved to where nothing is destroyed.
        var closure = block.Append(new BuiltinInst("raw_take", thick, closureAt));
        var result = block.Append(new ApplyInst(closure, signature.Result, arguments));
        block.Append(new BuiltinInst("forget", SilType.Unit, closure));
        block.Append(new ApplyInst(block.Append(RuntimeFunction("sable_callback_leave", new([SilType.Int], SilType.Unit))), SilType.Unit, context));
        block.Append(new ReturnInst(signature.Result == SilType.Unit ? null : result));
        _module.Add(entry);
        return name;
    }

    /// <summary>A closure that captures nothing, as a function of its own type: what C's entry calls.</summary>
    string PlainEntry(string code, ThickFunctionType thick) {
        var name = code + "$c";
        if (_module.Find(name) is not null) {
            return name;
        }

        var entry = new SilFunction(name, thick.Signature);
        var block = entry.AddBlock();
        var arguments = thick.Signature.Parameters.Select(p => (SilValue)block.AddArgument(p)).ToArray();
        var closure = block.Append(new PartialApplyInst(block.Append(new FunctionRefInst(code, thick.Implementation)), null, thick));
        var result = block.Append(new ApplyInst(closure, thick.Signature.Result, arguments));
        block.Append(new DestroyValueInst(closure));
        block.Append(new ReturnInst(thick.Signature.Result == SilType.Unit ? null : result));
        _module.Add(entry);
        return name;
    }

    /// <summary>The code of a closure over a C function's address: its context is the object whose 'address' is it.</summary>
    string IndirectThunk(ThickFunctionType thick, ClassType target) {
        var signature = thick.Signature;
        var name = "c.call$" + Sanitise(signature.ToString());
        if (_module.Find(name) is not null) {
            return name;
        }

        var foreign = new FunctionType([SilType.Int, ..signature.Parameters], signature.Result);
        _module.Foreign[name + "$c"] = (IndirectSymbol, foreign);
        var thunk = new SilFunction(name, thick.Implementation);
        var block = thunk.AddBlock();
        var arguments = signature.Parameters.Select(p => (SilValue)block.AddArgument(p)).ToArray();
        var context = block.Append(new UncheckedRefCastInst(block.AddArgument(SilType.Object), target));
        var address = block.Append(new LoadInst(block.Append(new RefElementAddrInst(context, target.FieldIndex("address")))));
        var result = block.Append(new ApplyInst(block.Append(new FunctionRefInst(name + "$c", foreign)), signature.Result, [address, ..arguments]));
        block.Append(new ReturnInst(signature.Result == SilType.Unit ? null : result));
        _module.Add(thunk);
        return name;
    }

    /// <summary>The fields to go through, a struct of one field at a time, to a pointer's address; null for what is not a pointer.</summary>
    static List<StructType>? PointerPath(SilType type) {
        var path = new List<StructType>();
        while (type is StructType { IsInline: false, Fields: [var only] } structure) {
            path.Add(structure);
            if (IsRawPointer(structure)) {
                return path;
            }

            type = only.Type;
        }

        return null;
    }

    /// <summary>An instance of Sable.Core's <c>RawPtr</c>.</summary>
    public static bool IsRawPointer(StructType type) =>
        type.Name == "Sable.Core.RawPtr" || type.Name.StartsWith("Sable.Core.RawPtr$", StringComparison.Ordinal);

    sealed partial class FunctionLowering {
        /// <summary>
        ///     Sable.Interop's <c>@intrinsic</c>s: <c>cFunction(function)</c>, the C function pointer of a function that
        ///     captures nothing; and on <c>CFunction&lt;F&gt;</c>, <c>contextFirst()</c> and <c>contextLast()</c>,
        ///     that of the entry a closure is called through by its context;
        ///     <c>closure(target)</c>, the function value that calls the
        ///     address the target holds. Each is compiled for the F it is of.
        /// </summary>
        SilValue? ForeignIntrinsic(BoundCall call) {
            if (call.Function.Name == "closure") {
                if (LowerType(call.Type) is not ThickFunctionType called || call.Arguments is not [var given]) {
                    return Missing(call, $"a C function of type '{call.Type.ToDisplayString()}'");
                }

                if (Expression(given) is not { Type: ClassType target } context) {
                    return null;
                }

                var held = Hold(Owned(context));
                var thunk = generator.IndirectThunk(called, target);
                return Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, called.Implementation)), Unhold(held), called));
            }

            if (Sub(call.Type) is not NamedTypeSymbol { TypeArguments: [var functionType] }
                || LowerType(functionType) is not ThickFunctionType thick || LowerType(call.Type) is not StructType result || PointerPath(result) is not { } path) {
                return Missing(call, $"the C function pointer '{call.Type.ToDisplayString()}'");
            }

            SilValue address;
            if (call.Function.Name == "cFunction") {
                if (call.Arguments is not [var argument] || Expression(argument) is not { } value) {
                    return null;
                }

                if (value is not PartialApplyInst { Context: null, Function: FunctionRefInst code }) {
                    generator._diagnostics.Error(ErrorCode.ERR_CFunctionCaptures, argument.Syntax.Span,
                        "a C function pointer is of a function named here, or of a closure written here that captures nothing: "
                        + "what a closure captures goes to C as a 'CCallback' and its context");
                    _failed = true;
                    return null;
                }

                var name = generator.PlainEntry(code.Function, thick);
                Emit(new DestroyValueInst(value));
                Emit(new ApplyInst(Emit(generator.RuntimeFunction("sable_threads_begin", new([], SilType.Unit))), SilType.Unit));
                address = Emit(new BuiltinInst("c_address", SilType.Int, Emit(new FunctionRefInst(name, thick.Signature))));
            } else if (generator.ContextEntry(thick, call.Function.Name == "contextFirst") is { } entry) {
                address = Emit(new BuiltinInst("c_address", SilType.Int, Emit(new FunctionRefInst(entry, thick.Signature))));
            } else {
                // No context there: the null pointer, which the library says no to.
                address = Emit(new IntegerLiteralInst(SilType.Int, 0));
            }

            path.Reverse();
            return path.Aggregate(address, (inner, type) => Emit(new StructInst(type, inner)));
        }
    }
}
