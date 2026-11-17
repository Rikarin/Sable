namespace Sable.SIL;

/// <summary>
///     Checks the invariants every SIL function must hold, after lowering and
///     after every pass (docs/04 §2.1: "a SIL verifier runs after every pass in
///     debug builds. Non-negotiable.").
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <b>Structure.</b> Every block ends in exactly one terminator; the
///             entry block's arguments are the function's parameters.
///         </item>
///         <item>
///             <b>SSA.</b> Every use is dominated by its definition — an
///             earlier instruction in the same block, or a block that dominates.
///         </item>
///         <item>
///             <b>Types.</b> Every instruction's operands have the types it
///             takes; branches pass what the target's arguments expect; returns
///             return what the signature says; calls match the callee's type.
///         </item>
///         <item>
///             <b>Stack discipline.</b> <c>dealloc_stack</c> frees the most
///             recent <c>alloc_stack</c> still live; every path into a block
///             arrives with the same allocations live; none is live at a
///             <c>return</c>.
///         </item>
///         <item>
///             <b>Ownership.</b> Every owned value is consumed exactly once on
///             every path that leaves the function — a path that ends in
///             <c>unreachable</c> is stopping the program, and may leak — and is
///             not used after it is consumed. Only an owned value can be
///             consumed. A guaranteed value, and an address into an object, is
///             used only while the owned value it borrows from is still live.
///         </item>
///     </list>
/// </remarks>
public static class SilVerifier {
    public static IReadOnlyList<string> Verify(SilModule module) {
        var errors = new List<string>();
        foreach (var function in module.Functions) {
            errors.AddRange(Verify(function, module));
        }

        return errors;
    }

    public static IReadOnlyList<string> Verify(SilFunction function, SilModule? module = null) {
        var errors = new List<string>();
        void Error(SilBlock? block, string message) =>
            errors.Add(block is null
                ? $"@{function.Name}: {message}"
                : $"@{function.Name} bb{function.Blocks.IndexOf(block)}: {message}");

        if (function.Blocks.Count == 0) {
            Error(null, "has no blocks");
            return errors;
        }

        // ---- Structure ----------------------------------------------------
        var entry = function.Entry;
        if (entry.Arguments.Count != function.Type.Parameters.Length
            || !entry.Arguments.Select(static a => a.Type).SequenceEqual(function.Type.Parameters)) {
            Error(entry, $"the entry block's arguments must be the parameters ({string.Join(", ", function.Type.Parameters)})");
        }

        var blocks = new HashSet<SilBlock>(function.Blocks);
        if (function.Blocks.Any(block => block.Successors.Contains(entry))) {
            // The entry block's arguments are the parameters; a branch to it
            // would give them second values, which no backend can express.
            Error(entry, "the entry block cannot be branched to");
        }

        foreach (var block in function.Blocks) {
            if (block.Terminator is null) {
                Error(block, "does not end in a terminator");
            }

            for (var i = 0; i < block.Instructions.Count - 1; i++) {
                if (block.Instructions[i].IsTerminator) {
                    Error(block, $"has '{block.Instructions[i].Opcode}' before its end");
                }
            }

            foreach (var successor in block.Successors) {
                if (!blocks.Contains(successor)) {
                    Error(block, "branches to a block outside the function");
                }
            }
        }

        if (errors.Count > 0) {
            return errors;
        }

        // ---- SSA: definitions dominate uses ---------------------------------
        var dominators = Dominators(function);
        var definedIn = new Dictionary<SilValue, (SilBlock Block, int Index)>();
        foreach (var block in function.Blocks) {
            foreach (var argument in block.Arguments) {
                definedIn[argument] = (block, -1);
            }

            for (var i = 0; i < block.Instructions.Count; i++) {
                definedIn[block.Instructions[i]] = (block, i);
            }
        }

        foreach (var block in function.Blocks) {
            if (!dominators.ContainsKey(block)) {
                continue; // unreachable: nothing runs, nothing to check
            }

            for (var i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                foreach (var operand in instruction.Operands) {
                    if (!definedIn.TryGetValue(operand, out var definition)) {
                        Error(block, $"'{instruction.Opcode}' uses a value defined outside the function");
                        continue;
                    }

                    var dominates = definition.Block == block
                        ? definition.Index < i
                        : dominators[block].Contains(definition.Block);
                    if (!dominates) {
                        Error(block, $"'{instruction.Opcode}' uses a value whose definition does not dominate it");
                    }
                }
            }
        }

        // ---- Types ----------------------------------------------------------
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                if (TypeError(instruction, function, module) is { } message) {
                    Error(block, message);
                }
            }

            for (var i = 0; i < block.Arguments.Count; i++) {
                if (block.Arguments[i].IsGuaranteed && (block == entry || block.Arguments[i].Type.IsTrivial)) {
                    Error(block, "only a non-trivial argument outside the entry block can be @guaranteed");
                }
            }
        }

        if (errors.Count > 0) {
            return errors;
        }

        // ---- Stack discipline ------------------------------------------------
        VerifyStack(function, Error);
        if (errors.Count > 0) {
            return errors;
        }

        // ---- Ownership -------------------------------------------------------
        VerifyOwnership(function, Error);

        return errors;
    }

    static string? TypeError(SilInstruction instruction, SilFunction function, SilModule? module) {
        static string Types(IEnumerable<SilValue> values) => string.Join(", ", values.Select(static v => v.Type));

        switch (instruction) {
            case IntegerLiteralInst literal when !SilType.IsInteger(literal.Type) && literal.Type != SilType.Bool && literal.Type != SilType.Char:
                return $"integer_literal of type {literal.Type}; only an integer, Bool and Char";

            case FloatLiteralInst literal when !SilType.IsFloat(literal.Type):
                return $"float_literal of type {literal.Type}; only F64 and F32";

            case IntegerLiteralInst { Value: < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) } literal when literal.Type == SilType.Char:
                return $"integer_literal $Char must be a Unicode scalar value, not {literal.Value}";

            case IntegerLiteralInst { Value: not (0 or 1) } literal when literal.Type == SilType.Bool:
                return $"integer_literal $Bool must be 0 or 1, not {literal.Value}";

            case BuiltinInst builtin: {
                if (SilBuiltins.Resolve(builtin.Name, [..builtin.Operands.Select(static o => o.Type)], builtin.Type) is not { } signature) {
                    return SilBuiltins.Find(builtin.Name) is null && !SilBuiltins.ArrayOperations.Contains(builtin.Name)
                        ? $"no builtin named \"{builtin.Name}\""
                        : $"builtin \"{builtin.Name}\" cannot take ({Types(builtin.Operands)})";
                }

                if (!builtin.Operands.Select(static o => o.Type).SequenceEqual(signature.Operands)) {
                    return $"builtin \"{builtin.Name}\" takes ({string.Join(", ", signature.Operands)}), "
                        + $"not ({Types(builtin.Operands)})";
                }

                return builtin.Type == signature.Result
                    ? null
                    : $"builtin \"{builtin.Name}\" gives {signature.Result}, not {builtin.Type}";
            }

            case DeallocStackInst dealloc when dealloc.Address is not AllocStackInst:
                return "dealloc_stack must free an alloc_stack";

            case LoadInst load when load.Address.Type is not AddressType:
                return $"load needs an address, not {load.Address.Type}";

            case LoadWeakInst weak when weak.Address.Type is not AddressType { Pointee: WeakStorageType }:
                return "load_weak needs the address of weak storage";

            case LoadUnownedInst unowned when unowned.Address.Type is not AddressType { Pointee: UnownedStorageType }:
                return "load_unowned needs the address of unowned storage";

            case StoreWeakInst weak when weak.Address.Type is not AddressType { Pointee: WeakStorageType storage } || storage.Optional != weak.Value.Type:
                return $"store_weak of {weak.Value.Type} to {weak.Address.Type}";

            case StoreUnownedInst unowned when unowned.Address.Type is not AddressType { Pointee: UnownedStorageType storage } || storage.Referent != unowned.Value.Type:
                return $"store_unowned of {unowned.Value.Type} to {unowned.Address.Type}";

            case LoadInst { Type: WeakStorageType or UnownedStorageType }:
                return "weak and unowned storage is read with load_weak and load_unowned";

            case LoadBorrowInst borrow when borrow.Address.Type is not AddressType || borrow.Type.IsTrivial:
                return "load_borrow needs the address of a non-trivial value; a trivial one is loaded [trivial]";

            case EndBorrowInst end when end.Borrowed is not LoadBorrowInst:
                return "end_borrow ends a load_borrow";

            case LoadInst load when (load.Kind == LoadKind.Trivial) != load.Type.IsTrivial:
                return load.Kind == LoadKind.Trivial
                    ? $"load [trivial] of {load.Type}, which is not trivial; it needs [copy] or [take]"
                    : $"load [{load.Kind.ToString().ToLowerInvariant()}] of {load.Type}, which is trivial";

            case CopyValueInst copy when copy.Value.Type.IsTrivial:
            case DestroyValueInst destroy when destroy.Value.Type.IsTrivial:
                return $"{instruction.Opcode} of a trivial value, which has nothing to copy or destroy";

            case DestroyAddrInst destroy when destroy.Address.Type is not AddressType:
                return $"destroy_addr needs an address, not {destroy.Address.Type}";

            case RefElementAddrInst element when element.Reference.Type is not ClassType:
                return $"ref_element_addr needs a class reference, not {element.Reference.Type}";

            case StoreInst store when store.Address.Type is not AddressType address || address.Pointee != store.Value.Type:
                return $"store of {store.Value.Type} to {store.Address.Type}";

            case GlobalAddrInst global when module is not null:
                return module.Globals.Any(g => g.Name == global.Global && g.Type == global.ValueType)
                    ? null
                    : $"global_addr names @{global.Global} : ${global.ValueType}, which the module does not declare";

            case FunctionRefInst reference when module is not null: {
                // One unit of several may name what another unit defines.
                var calleeType = module.Find(reference.Function)?.Type
                    ?? (module.Unit?.External.TryGetValue(reference.Function, out var external) == true ? external : null);
                if (calleeType is null) {
                    return $"function_ref names @{reference.Function}, which the module does not define";
                }

                return calleeType == reference.Type
                    ? null
                    : $"function_ref @{reference.Function} is {calleeType}, not {reference.Type}";
            }

            case StructInst structInst:
                return structInst.Operands.Select(static o => o.Type).SequenceEqual(structInst.StructType.Fields.Select(static f => f.Type))
                    ? null
                    : $"struct {structInst.StructType.Name} takes ({string.Join(", ", structInst.StructType.Fields.Select(static f => f.Type))}), "
                    + $"not ({Types(structInst.Operands)})";

            case StructExtractInst extract when extract.Value.Type is not StructType:
                return $"struct_extract needs a struct, not {extract.Value.Type}";

            case StructElementAddrInst element when element.Address.Type is not AddressType { Pointee: StructType }:
                return $"struct_element_addr needs a struct's address, not {element.Address.Type}";

            case TupleInst tuple:
                return tuple.Operands.Select(static o => o.Type).SequenceEqual(tuple.TupleType.Elements)
                    ? null
                    : $"tuple {tuple.TupleType} is given ({Types(tuple.Operands)})";

            case TupleExtractInst extract when extract.Value.Type is not TupleType { } tupleType
                                               || extract.Element >= tupleType.Elements.Length:
                return $"tuple_extract {extract.Element} of {extract.Value.Type}";

            case TupleElementAddrInst element when element.Address.Type is not AddressType { Pointee: TupleType { } tupleType }
                                                   || element.Element >= tupleType.Elements.Length:
                return $"tuple_element_addr {element.Element} of {element.Address.Type}";

            case EnumInst enumInst: {
                var (name, payload) = enumInst.EnumType.Cases[enumInst.Case];
                if (payload is null) {
                    return enumInst.Payload is null ? null : $"enum case #{name} carries nothing";
                }

                return enumInst.Payload?.Type == payload ? null : $"enum case #{name} carries {payload}";
            }

            case SwitchEnumInst switchEnum: {
                if (switchEnum.Value.Type is not EnumType enumType) {
                    return $"switch_enum needs an enum, not {switchEnum.Value.Type}";
                }

                var seen = new HashSet<int>();
                foreach (var (caseIndex, target) in switchEnum.Cases) {
                    if (!seen.Add(caseIndex)) {
                        return $"switch_enum names #{enumType.Cases[caseIndex].Name} twice";
                    }

                    var payload = enumType.Cases[caseIndex].Payload;
                    var expected = payload is null ? Array.Empty<SilType>() : [payload];
                    if (!target.Arguments.Select(static a => a.Type).SequenceEqual(expected)) {
                        return $"switch_enum's block for #{enumType.Cases[caseIndex].Name} must take ({string.Join(", ", expected.Select(static t => t.ToString()))})";
                    }

                    // The payload is borrowed from the enum, which the switch does not consume.
                    if (payload is { IsTrivial: false } && !target.Arguments[0].IsGuaranteed) {
                        return $"switch_enum's block for #{enumType.Cases[caseIndex].Name} must take its payload @guaranteed";
                    }
                }

                if (switchEnum.Default is { } fallback && fallback.Arguments.Count > 0) {
                    return "switch_enum's default block takes nothing";
                }

                return switchEnum.Default is null && seen.Count < enumType.Cases.Count
                    ? $"switch_enum on {enumType.Name} covers {seen.Count} of {enumType.Cases.Count} cases and has no default"
                    : null;
            }

            case CondFailInst condFail when condFail.Condition.Type != SilType.Bool:
                return $"cond_fail needs a Bool, not {condFail.Condition.Type}";

            case ThrowInst throwInst:
                if (function.Type.Thrown is not { } thrown) {
                    return "throw in a function that does not throw";
                }

                return throwInst.Error.Type == thrown ? null : $"throws {throwInst.Error.Type} from a function throwing {thrown}";

            case PartialApplyInst partial: {
                if (partial.Function.Type is not FunctionType code || code != partial.ThickType.Implementation) {
                    return $"partial_apply needs code of type {partial.ThickType.Implementation}, not {partial.Function.Type}";
                }

                return partial.Context is null or { Type: ClassType } ? null : $"a closure's context is an object, not {partial.Context.Type}";
            }

            case UpcastInst upcast when upcast.Value.Type is not ClassType from || !from.IsSubclassOf((ClassType)upcast.Type):
                return $"upcast of {upcast.Value.Type} to {upcast.Type}, which it does not inherit from";

            case ClassMethodInst method:
                if (method.Reference.Type is not ClassType owner) {
                    return $"class_method needs an object, not {method.Reference.Type}";
                }

                return owner.MethodIndex(method.Method) < 0 ? $"{owner.Name} has no method #{method.Method} in its vtable" : null;

            // From an Object, or down from a class to a subclass of it (after a 'class_is' test).
            case UncheckedRefCastInst cast when cast.Value.Type != SilType.Object
                && !(cast.Value.Type is ClassType from && cast.Type is ClassType to && to.IsSubclassOf(from)):
                return $"unchecked_ref_cast is from an Object, or from a class to its subclass, not {cast.Value.Type}";

            case TryApplyInst tryApply: {
                if (Signature(tryApply.Callee.Type) is not { Thrown: { } calleeThrows } callee) {
                    return "try_apply needs a throwing function; call others with apply";
                }

                if (!tryApply.Arguments.Select(static a => a.Type).SequenceEqual(callee.Parameters)) {
                    return $"try_apply passes ({Types(tryApply.Arguments)}) to {callee}";
                }

                if (tryApply.ResultType != callee.Result) {
                    return $"try_apply of {callee} gives {callee.Result}, not {tryApply.ResultType}";
                }

                var normal = callee.Result == SilType.Unit ? Array.Empty<SilType>() : [callee.Result];
                if (!tryApply.Normal.Arguments.Select(static a => a.Type).SequenceEqual(normal)) {
                    return "try_apply's normal block must take the result";
                }

                if (tryApply.Normal.Arguments.Concat(tryApply.Error.Arguments).Any(static a => a.IsGuaranteed)) {
                    return "try_apply gives its blocks owned values, not @guaranteed ones";
                }

                return tryApply.Error.Arguments.Select(static a => a.Type).SequenceEqual([calleeThrows])
                    ? null
                    : $"try_apply's error block must take the {calleeThrows}";
            }

            case ApplyInst apply: {
                if (Signature(apply.Callee.Type) is not { } callee) {
                    return $"apply needs a function, not {apply.Callee.Type}";
                }

                if (callee.Thrown is not null) {
                    return "apply of a throwing function; it needs try_apply";
                }

                if (!apply.Arguments.Select(static a => a.Type).SequenceEqual(callee.Parameters)) {
                    return $"apply passes ({Types(apply.Arguments)}) to {callee}";
                }

                return apply.Type == callee.Result ? null : $"apply of {callee} gives {callee.Result}, not {apply.Type}";
            }

            case BranchInst branch:
                return Arguments(branch.Target, branch.Operands);

            case CondBranchInst conditional:
                if (conditional.Condition.Type != SilType.Bool) {
                    return $"cond_br needs a Bool, not {conditional.Condition.Type}";
                }

                return Arguments(conditional.WhenTrue, conditional.TrueArguments)
                    ?? Arguments(conditional.WhenFalse, conditional.FalseArguments);

            case ReturnInst returnInst: {
                var returned = returnInst.Value?.Type ?? SilType.Unit;
                return returned == function.Type.Result
                    ? null
                    : $"returns {returned} from a function returning {function.Type.Result}";
            }

            default:
                return null;
        }
    }

    /// <summary>What calling a value of this type takes and gives: a function's own type, or a closure's.</summary>
    static FunctionType? Signature(SilType type) =>
        type switch {
            FunctionType function => function,
            ThickFunctionType thick => thick.Signature,
            _ => null,
        };

    static string? Arguments(SilBlock target, IEnumerable<SilValue> arguments) {
        var passed = arguments.Select(static a => a.Type).ToList();
        var expected = target.Arguments.Select(static a => a.Type).ToList();
        return passed.SequenceEqual(expected)
            ? null
            : $"branch passes ({string.Join(", ", passed)}) to a block taking ({string.Join(", ", expected)})";
    }

    /// <summary>
    ///     Walks the blocks from the entry carrying the live stack allocations,
    ///     which every path into a block must agree on.
    /// </summary>
    static void VerifyStack(SilFunction function, Action<SilBlock?, string> error) {
        var onEntry = new Dictionary<SilBlock, List<AllocStackInst>> { [function.Entry] = [] };
        var pending = new Queue<SilBlock>();
        pending.Enqueue(function.Entry);
        var done = new HashSet<SilBlock>();

        while (pending.Count > 0) {
            var block = pending.Dequeue();
            if (!done.Add(block)) {
                continue;
            }

            var stack = new List<AllocStackInst>(onEntry[block]);
            foreach (var instruction in block.Instructions) {
                switch (instruction) {
                    case AllocStackInst alloc:
                        stack.Add(alloc);
                        break;

                    case DeallocStackInst dealloc:
                        if (stack.Count == 0 || !ReferenceEquals(stack[^1], dealloc.Address)) {
                            error(block, "dealloc_stack does not free the most recent live alloc_stack");
                            return;
                        }

                        stack.RemoveAt(stack.Count - 1);
                        break;

                    case LoadInst { Address: AllocStackInst slot } when !stack.Contains(slot):
                    case StoreInst { Address: AllocStackInst storeSlot } when !stack.Contains(storeSlot):
                    case DestroyAddrInst { Address: AllocStackInst destroySlot } when !stack.Contains(destroySlot):
                        error(block, $"'{instruction.Opcode}' uses stack storage that is not live");
                        return;

                    case ReturnInst or ThrowInst when stack.Count > 0:
                        error(block, $"leaves the function with {stack.Count} stack allocation(s) still live");
                        return;
                }
            }

            foreach (var successor in block.Successors) {
                if (onEntry.TryGetValue(successor, out var existing)) {
                    if (!existing.SequenceEqual(stack)) {
                        error(successor, "is entered with different stack allocations live on different paths");
                        return;
                    }

                    continue;
                }

                onEntry[successor] = stack;
                pending.Enqueue(successor);
            }
        }
    }

    // =======================================================================
    // Ownership
    // =======================================================================

    /// <summary>How a value is held, by what defines it.</summary>
    public static Ownership OwnershipOf(SilValue value) =>
        value switch {
            _ when value.Type.IsTrivial => Ownership.Trivial,
            // A parameter is borrowed for the call, unless the function owns it.
            SilArgument argument when argument.Block == argument.Block.Function.Entry =>
                argument.Block.Function.Type.IsOwned(argument.Block.Arguments.IndexOf(argument)) ? Ownership.Owned : Ownership.Guaranteed,
            SilArgument { IsGuaranteed: true } => Ownership.Guaranteed,
            StructExtractInst or TupleExtractInst or UncheckedRefCastInst or UpcastInst or LoadBorrowInst => Ownership.Guaranteed,
            BuiltinInst { Name: "array_get_borrowed" } => Ownership.Guaranteed,
            _ => Ownership.Owned,
        };

    /// <summary>The operands an instruction consumes; it borrows the rest.</summary>
    /// <remarks>A <c>cond_br</c>'s depend on the edge taken, and are worked out per edge.</remarks>
    public static IEnumerable<int> Consumed(SilInstruction instruction) {
        switch (instruction) {
            // A slot that holds without owning takes nothing.
            case StoreInst { Address: AllocStackInst { Unmanaged: true } }:
                return [];

            case StoreInst:
                return [0];

            case StructInst or TupleInst or EnumInst or ReturnInst or ThrowInst or DestroyValueInst:
                return Enumerable.Range(0, instruction.Operands.Count);

            case PartialApplyInst { Context: not null }:
                return [1];

            case BranchInst branch:
                return Enumerable.Range(0, branch.Operands.Count).Where(i => !branch.Target.Arguments[i].IsGuaranteed);

            case BuiltinInst builtin:
                return SilBuiltins.Resolve(builtin.Name, [..builtin.Operands.Select(static o => o.Type)], builtin.Type)?.Consumed ?? [];

            // An argument for an owned parameter is handed over.
            case ApplyInst or TryApplyInst when Signature(instruction.Operands[0].Type) is { Owned.IsEmpty: false } callee:
                return Enumerable.Range(0, callee.Parameters.Length).Where(callee.IsOwned).Select(static i => i + 1);

            default:
                return [];
        }
    }

    /// <summary>
    ///     A forward walk carrying the owned values live at each point. Every
    ///     path into a block must arrive with the same ones live: a value live
    ///     on one path and not another was leaked on the first, or consumed
    ///     twice on the second.
    /// </summary>
    static void VerifyOwnership(SilFunction function, Action<SilBlock?, string> error) {
        var names = ValueNames(function);
        string Describe(SilValue value) =>
            value is SilInstruction instruction ? $"{names[value]} ({instruction.Opcode})" : $"{names[value]} (argument)";

        // What each borrowed value, and each address into an object, borrows
        // from: an owned value, or null for a parameter, which outlives the call.
        var roots = new Dictionary<SilValue, SilValue?>();
        // A load_borrow is its own root, and is tracked like an owned value:
        // live until its end_borrow, which every path must reach.
        SilValue? RootOf(SilValue value) =>
            OwnershipOf(value) switch {
                Ownership.Owned => value,
                _ when value is LoadBorrowInst => value,
                _ => roots.GetValueOrDefault(value),
            };

        bool Tracked(SilValue value) => OwnershipOf(value) != Ownership.Trivial || roots.ContainsKey(value);

        var order = ReversePostorder(function);
        var liveIn = new Dictionary<SilBlock, HashSet<SilValue>> { [function.Entry] = [] };
        var reported = false;

        void Enter(SilBlock from, SilBlock target, HashSet<SilValue> live, IReadOnlyList<SilValue> passed) {
            // A borrowed argument borrows from what the value passed to it did.
            for (var i = 0; i < target.Arguments.Count && i < passed.Count; i++) {
                if (!target.Arguments[i].IsGuaranteed) {
                    continue;
                }

                var root = RootOf(passed[i]);
                if (roots.TryGetValue(target.Arguments[i], out var existing)) {
                    if (!ReferenceEquals(existing, root)) {
                        error(target, "a @guaranteed argument borrows from different values on different paths");
                    }
                } else {
                    roots[target.Arguments[i]] = root;
                }
            }

            if (!liveIn.TryGetValue(target, out var expected)) {
                liveIn[target] = live;
                return;
            }

            if (!reported && !expected.SetEquals(live)) {
                reported = true;
                var difference = expected.Union(live).Except(expected.Intersect(live)).Select(Describe);
                error(target, $"is entered with different owned values live on different paths: {string.Join(", ", difference)}"
                    + $" — consumed on some paths into it and not others (from bb{function.Blocks.IndexOf(from)})");
            }
        }

        foreach (var block in order) {
            if (!liveIn.TryGetValue(block, out var incoming)) {
                continue;
            }

            var live = new HashSet<SilValue>(incoming);
            foreach (var argument in block.Arguments) {
                if (OwnershipOf(argument) == Ownership.Owned) {
                    live.Add(argument);
                }
            }

            foreach (var instruction in block.Instructions) {
                // Every use needs what it uses alive.
                foreach (var operand in instruction.Operands) {
                    if (!Tracked(operand)) {
                        continue;
                    }

                    var root = RootOf(operand);
                    if (root is not null && !live.Contains(root)) {
                        error(block, OwnershipOf(operand) == Ownership.Owned
                            ? $"'{instruction.Opcode}' uses {Describe(operand)}, which is not live: it was consumed, or not on this path"
                            : $"'{instruction.Opcode}' uses {Describe(operand)}, borrowed from {Describe(root)}, which is no longer live");
                        return;
                    }
                }

                // What a use derives borrows from the same place.
                switch (instruction) {
                    case StructExtractInst or TupleExtractInst when !instruction.Type.IsTrivial:
                    case UncheckedRefCastInst:
                    case UpcastInst:
                    case RefElementAddrInst or StructElementAddrInst or TupleElementAddrInst when Tracked(instruction.Operands[0]):
                    case BuiltinInst { Name: "array_get_borrowed" } when !instruction.Type.IsTrivial && Tracked(instruction.Operands[0]):
                        roots[instruction] = RootOf(instruction.Operands[0]);
                        break;

                    case SwitchEnumInst switchEnum:
                        foreach (var (_, target) in switchEnum.Cases) {
                            if (target.Arguments is [{ IsGuaranteed: true } payload]) {
                                roots[payload] = RootOf(switchEnum.Value);
                            }
                        }

                        break;
                }

                if (instruction is CondBranchInst conditional) {
                    var trueLive = Consume(live, conditional.TrueArguments, conditional.WhenTrue, block, error, Describe);
                    var falseLive = Consume(live, conditional.FalseArguments, conditional.WhenFalse, block, error, Describe);
                    if (trueLive is null || falseLive is null) {
                        return;
                    }

                    Enter(block, conditional.WhenTrue, trueLive, [..conditional.TrueArguments]);
                    Enter(block, conditional.WhenFalse, falseLive, [..conditional.FalseArguments]);
                    continue;
                }

                // Nothing writes storage while a borrow of it is live.
                if (WrittenSlot(instruction) is { } written
                    && live.OfType<LoadBorrowInst>().FirstOrDefault(b => ReferenceEquals(SlotOf(b.Address), written)) is { } borrowed) {
                    error(block, $"'{instruction.Opcode}' writes storage that {Describe(borrowed)} borrows");
                    return;
                }

                if (instruction is EndBorrowInst { Borrowed: var ended }) {
                    live.Remove(ended);
                    continue;
                }

                foreach (var index in Consumed(instruction).Distinct()) {
                    var operand = instruction.Operands[index];
                    if (operand.Type.IsTrivial) {
                        continue;
                    }

                    if (OwnershipOf(operand) != Ownership.Owned) {
                        error(block, $"'{instruction.Opcode}' consumes {Describe(operand)}, which is borrowed; it needs a copy_value");
                        return;
                    }

                    live.Remove(operand);
                }

                if (instruction.Operands.Where(o => OwnershipOf(o) == Ownership.Owned).GroupBy(static o => o).FirstOrDefault(static g => g.Count() > 1) is { } twice
                    && Consumed(instruction).Count(i => ReferenceEquals(instruction.Operands[i], twice.Key)) > 1) {
                    error(block, $"'{instruction.Opcode}' consumes {Describe(twice.Key)} twice");
                    return;
                }

                if (instruction.HasResult && (OwnershipOf(instruction) == Ownership.Owned || instruction is LoadBorrowInst)) {
                    live.Add(instruction);
                }

                switch (instruction) {
                    case ReturnInst or ThrowInst when live.Count > 0:
                        error(block, $"'{instruction.Opcode}' leaves the function with owned values never consumed: "
                            + string.Join(", ", live.Select(Describe)));
                        return;

                    case BranchInst branch:
                        Enter(block, branch.Target, live, branch.Operands);
                        break;

                    case SwitchEnumInst switchEnum:
                        foreach (var (_, target) in switchEnum.Cases) {
                            Enter(block, target, live, []);
                        }

                        if (switchEnum.Default is { } fallback) {
                            Enter(block, fallback, live, []);
                        }

                        break;

                    case TryApplyInst tryApply:
                        Enter(block, tryApply.Normal, live, []);
                        Enter(block, tryApply.Error, live, []);
                        break;
                }
            }
        }
    }

    /// <summary>The stack slot an address is in, through the fields and elements it reaches; null for any other storage.</summary>
    static AllocStackInst? SlotOf(SilValue address) =>
        address switch {
            AllocStackInst slot => slot,
            StructElementAddrInst element => SlotOf(element.Address),
            TupleElementAddrInst element => SlotOf(element.Address),
            _ => null,
        };

    /// <summary>The stack slot an instruction may write, if it writes one.</summary>
    static AllocStackInst? WrittenSlot(SilInstruction instruction) =>
        instruction switch {
            StoreInst store => SlotOf(store.Address),
            StoreWeakInst weak => SlotOf(weak.Address),
            StoreUnownedInst unowned => SlotOf(unowned.Address),
            DestroyAddrInst destroy => SlotOf(destroy.Address),
            LoadInst { Kind: LoadKind.Take } take => SlotOf(take.Address),
            BuiltinInst or ApplyInst or TryApplyInst => instruction.Operands
                .Where(static o => o.Type is AddressType)
                .Select(SlotOf)
                .FirstOrDefault(static s => s is not null),
            _ => null,
        };

    static HashSet<SilValue>? Consume(
        HashSet<SilValue> live,
        IEnumerable<SilValue> passed,
        SilBlock target,
        SilBlock block,
        Action<SilBlock?, string> error,
        Func<SilValue, string> describe
    ) {
        var after = new HashSet<SilValue>(live);
        var i = 0;
        foreach (var value in passed) {
            if (!value.Type.IsTrivial && !target.Arguments[i].IsGuaranteed) {
                if (OwnershipOf(value) != Ownership.Owned) {
                    error(block, $"'cond_br' passes {describe(value)}, which is borrowed, to an owned argument; it needs a copy_value");
                    return null;
                }

                after.Remove(value);
            }

            i++;
        }

        return after;
    }

    /// <summary>The names <see cref="SilPrinter" /> gives values, for messages.</summary>
    static Dictionary<SilValue, string> ValueNames(SilFunction function) {
        var names = new Dictionary<SilValue, string>();
        foreach (var block in function.Blocks) {
            foreach (var argument in block.Arguments) {
                names[argument] = $"%{names.Count}";
            }

            foreach (var instruction in block.Instructions) {
                if (instruction.HasResult) {
                    names[instruction] = $"%{names.Count}";
                }
            }
        }

        return names;
    }

    /// <summary>The reachable blocks, each after every block that reaches it other than by a back edge.</summary>
    public static List<SilBlock> ReversePostorder(SilFunction function) {
        var visited = new HashSet<SilBlock> { function.Entry };
        var postorder = new List<SilBlock>();
        var stack = new Stack<(SilBlock Block, IEnumerator<SilBlock> Successors)>();
        stack.Push((function.Entry, function.Entry.Successors.GetEnumerator()));
        while (stack.Count > 0) {
            var (block, successors) = stack.Peek();
            if (successors.MoveNext()) {
                if (visited.Add(successors.Current)) {
                    stack.Push((successors.Current, successors.Current.Successors.GetEnumerator()));
                }

                continue;
            }

            stack.Pop();
            postorder.Add(block);
        }

        postorder.Reverse();
        return postorder;
    }

    /// <summary>
    ///     For each block reachable from the entry, the blocks that dominate it,
    ///     itself included. The simple iterative algorithm: fine for the sizes a
    ///     function has.
    /// </summary>
    public static Dictionary<SilBlock, HashSet<SilBlock>> Dominators(SilFunction function) {
        var reachable = new List<SilBlock>();
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>();
        pending.Push(function.Entry);
        while (pending.Count > 0) {
            var block = pending.Pop();
            if (!seen.Add(block)) {
                continue;
            }

            reachable.Add(block);
            foreach (var successor in block.Successors) {
                pending.Push(successor);
            }
        }

        var predecessors = reachable.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in reachable) {
            foreach (var successor in block.Successors) {
                predecessors[successor].Add(block);
            }
        }

        var dominators = reachable.ToDictionary(static b => b, _ => new HashSet<SilBlock>(reachable));
        dominators[function.Entry] = [function.Entry];

        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in reachable) {
                if (block == function.Entry) {
                    continue;
                }

                HashSet<SilBlock>? meet = null;
                foreach (var predecessor in predecessors[block]) {
                    if (meet is null) {
                        meet = [..dominators[predecessor]];
                    } else {
                        meet.IntersectWith(dominators[predecessor]);
                    }
                }

                meet ??= [];
                meet.Add(block);

                if (!meet.SetEquals(dominators[block])) {
                    dominators[block] = meet;
                    changed = true;
                }
            }
        }

        return dominators;
    }
}
