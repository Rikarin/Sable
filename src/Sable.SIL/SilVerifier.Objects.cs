namespace Sable.SIL;

/// <summary>
///     The verifier's checks of dynamic accesses, of new objects' fields, and
///     of what each type's vtable and deinit name.
/// </summary>
public static partial class SilVerifier {
    // =======================================================================
    // Accesses
    // =======================================================================

    /// <summary>
    ///     Every <c>access_begin</c> is ended by an <c>access_end</c> of the same
    ///     address on every path that leaves the function: one left open makes
    ///     every later access to that storage trap as a conflict, and an end
    ///     with nothing to end corrupts the runtime's record of what is open.
    ///     A path that ends in <c>unreachable</c> is stopping the program.
    /// </summary>
    static void VerifyAccesses(SilFunction function, Action<SilBlock?, string> error) {
        var onEntry = new Dictionary<SilBlock, List<SilValue>> { [function.Entry] = [] };
        var pending = new Queue<SilBlock>();
        pending.Enqueue(function.Entry);
        var done = new HashSet<SilBlock>();

        while (pending.Count > 0) {
            var block = pending.Dequeue();
            if (!done.Add(block)) {
                continue;
            }

            var open = new List<SilValue>(onEntry[block]);
            foreach (var instruction in block.Instructions) {
                switch (instruction) {
                    case BuiltinInst { Name: "access_begin_read" or "access_begin_modify" } begin:
                        open.Add(begin.Operands[0]);
                        break;

                    case BuiltinInst { Name: "access_end" } end:
                        var index = open.LastIndexOf(end.Operands[0]);
                        if (index < 0) {
                            error(block, "access_end ends an access that is not in progress");
                            return;
                        }

                        open.RemoveAt(index);
                        break;

                    case ReturnInst or ThrowInst when open.Count > 0:
                        error(block, $"leaves the function with {open.Count} access(es) still in progress");
                        return;
                }
            }

            foreach (var successor in block.Successors) {
                if (onEntry.TryGetValue(successor, out var existing)) {
                    if (!existing.SequenceEqual(open)) {
                        error(successor, "is entered with different accesses in progress on different paths");
                        return;
                    }

                    continue;
                }

                onEntry[successor] = open;
                pending.Enqueue(successor);
            }
        }
    }

    // =======================================================================
    // Objects
    // =======================================================================

    /// <summary>
    ///     A new object starts zeroed, and a zeroed reference is not a value:
    ///     followed, it is a crash. So every field holding a reference — every
    ///     non-trivial field but a weak one, whose empty is <c>none</c> — is
    ///     stored before the object is used as a whole: returned, passed on,
    ///     put in an enum, called through. Storing a field, reading one
    ///     already stored, and passing the object as the <c>self</c> of a
    ///     function that stores fields (an initialiser) are not uses as a
    ///     whole; what such a function stores on every path to its
    ///     <c>return</c> counts as stored.
    /// </summary>
    static void VerifyObjects(
        SilFunction function,
        SilModule? module,
        Dictionary<SilFunction, HashSet<int>?> initialised,
        Action<SilBlock?, string> error
    ) {
        var names = (Dictionary<SilValue, string>?)null;
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                if (instruction is not AllocRefInst alloc) {
                    continue;
                }

                var required = Required(alloc.ClassType);
                if (required.Count == 0) {
                    continue;
                }

                if (FirstEarlyUse(function, alloc, required, module, initialised) is not { } early) {
                    continue;
                }

                names ??= ValueNames(function);
                var field = alloc.ClassType.Fields[early.Field].Name;
                error(early.Instruction.Block, $"'{early.Instruction.Opcode}' uses {names[alloc]} (alloc_ref ${alloc.ClassType.Name}) before its field #{field} is stored");
            }
        }
    }

    /// <summary>The fields of a class that must be stored before its object is used.</summary>
    static HashSet<int> Required(ClassType type) {
        var required = new HashSet<int>();
        for (var i = 0; i < (type.EmptyFrom ?? type.Fields.Count); i++) {
            if (type.Fields[i].Type is { IsTrivial: false } and not WeakStorageType) {
                required.Add(i);
            }
        }

        return required;
    }

    /// <summary>
    ///     A forward walk from the allocation: the first instruction that uses
    ///     the object, or reads a field, before a field it needs is stored on
    ///     every path to it; null when there is none.
    /// </summary>
    static (SilInstruction Instruction, int Field)? FirstEarlyUse(
        SilFunction function,
        AllocRefInst alloc,
        HashSet<int> required,
        SilModule? module,
        Dictionary<SilFunction, HashSet<int>?> initialised
    ) {
        (SilInstruction, int)? found = null;
        Walk(function, alloc, alloc.ClassType.Fields.Count, module, initialised, (instruction, stored, needed) => {
            if (found is not null) {
                return;
            }

            foreach (var field in needed ?? required) {
                if (required.Contains(field) && !stored.Contains(field)) {
                    found = (instruction, field);
                    return;
                }
            }
        });

        return found;
    }

    /// <summary>
    ///     The fields of its <c>self</c>, an object, that a function stores on
    ///     every path to its <c>return</c>; null when that cannot be known —
    ///     self is not an object — or when no path returns.
    /// </summary>
    static HashSet<int>? Initialised(SilFunction function, SilModule? module, Dictionary<SilFunction, HashSet<int>?> initialised) {
        if (initialised.TryGetValue(function, out var known)) {
            return known;
        }

        if (function.Blocks.Count == 0 || function.Entry.Arguments.Count == 0 || function.Entry.Arguments[0].Type is not ClassType self) {
            return initialised[function] = null;
        }

        // Assumed to store nothing while it is worked out, so that an
        // initialiser that calls itself ends.
        initialised[function] = [];
        HashSet<int>? atReturn = null;
        Walk(function, function.Entry.Arguments[0], self.Fields.Count, module, initialised, (instruction, stored, _) => {
            if (instruction is ReturnInst) {
                if (atReturn is null) {
                    atReturn = [..stored];
                } else {
                    atReturn.IntersectWith(stored);
                }
            }
        });

        return initialised[function] = atReturn;
    }

    /// <summary>
    ///     Follows an object through a function: the fields stored on every
    ///     path to each instruction, which <paramref name="visit" /> is given
    ///     with the fields that instruction needs — null for all of them, as
    ///     when it uses the object as a whole. A return is visited too.
    /// </summary>
    static void Walk(
        SilFunction function,
        SilValue root,
        int fields,
        SilModule? module,
        Dictionary<SilFunction, HashSet<int>?> initialised,
        Action<SilInstruction, HashSet<int>, IEnumerable<int>?> visit
    ) {
        var everything = Enumerable.Range(0, fields).ToHashSet();
        var order = ReversePostorder(function);

        // The object under other names: upcast, copied, cast back. And the
        // address of each of its fields, and what is inside them.
        var aliases = new HashSet<SilValue> { root };
        var fieldOf = new Dictionary<SilValue, int>();
        foreach (var block in order) {
            foreach (var instruction in block.Instructions) {
                switch (instruction) {
                    case UpcastInst or CopyValueInst or UncheckedRefCastInst or BuiltinInst { Name: "opaque_borrow_as" } when aliases.Contains(instruction.Operands[0]):
                        aliases.Add(instruction);
                        break;
                    case RefElementAddrInst element when aliases.Contains(element.Reference):
                        fieldOf[element] = element.Field;
                        break;
                    case StructElementAddrInst or TupleElementAddrInst when fieldOf.TryGetValue(instruction.Operands[0], out var inside):
                        fieldOf[instruction] = inside;
                        break;
                }
            }
        }

        // Stored on every path in: everything, until the object is made.
        var storedOut = new Dictionary<SilBlock, HashSet<int>>();
        var predecessors = order.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in order) {
            foreach (var successor in block.Successors) {
                predecessors[successor].Add(block);
            }
        }

        HashSet<int> StoredIn(SilBlock block) {
            if (block == function.Entry) {
                return root is SilArgument ? [] : [..everything];
            }

            var stored = new HashSet<int>(everything);
            foreach (var predecessor in predecessors[block]) {
                if (storedOut.TryGetValue(predecessor, out var from)) {
                    stored.IntersectWith(from);
                }
            }

            return stored;
        }

        // To a fixed point — the sets only shrink, so it is reached — and
        // then once more, visiting.
        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in order) {
                var stored = StoredIn(block);
                foreach (var instruction in block.Instructions) {
                    Step(instruction, stored, visiting: false);
                }

                if (!storedOut.TryGetValue(block, out var previous) || !previous.SetEquals(stored)) {
                    storedOut[block] = stored;
                    changed = true;
                }
            }
        }

        foreach (var block in order) {
            var stored = StoredIn(block);
            foreach (var instruction in block.Instructions) {
                Step(instruction, stored, visiting: true);
            }
        }

        void Step(SilInstruction instruction, HashSet<int> stored, bool visiting) {
            if (instruction == root) {
                stored.Clear(); // a new object: nothing stored yet
                return;
            }

            if (instruction is ReturnInst && visiting) {
                visit(instruction, stored, []);
            }

            switch (instruction) {
                // Storing a field whole — strong, weak or unowned — stores it;
                // storing a part of one does not.
                case StoreInst or StoreWeakInst or StoreUnownedInst when fieldOf.TryGetValue(instruction.Operands[1], out var field):
                    if (aliases.Contains(instruction.Operands[0]) && visiting) {
                        visit(instruction, stored, null);
                    }

                    if (instruction.Operands[1] is RefElementAddrInst) {
                        stored.Add(field);
                    }

                    return;

                // Naming the object, or a field's address, uses neither.
                case UpcastInst or CopyValueInst or UncheckedRefCastInst or BuiltinInst { Name: "opaque_borrow_as" } when aliases.Contains(instruction):
                case RefElementAddrInst or StructElementAddrInst or TupleElementAddrInst when fieldOf.ContainsKey(instruction):
                case BuiltinInst { Name: "access_begin_read" or "access_begin_modify" or "access_end" or "access_check_read" or "access_check_modify" }:
                // Destroying it destroys what is stored; an empty field is
                // nothing to destroy. One whose initialiser threw is marked
                // unmade first, with only some of its fields stored.
                case DestroyValueInst:
                case BuiltinInst { Name: "object_unmade" }:
                    return;

                // The object as an initialiser's self.
                case ApplyInst or TryApplyInst when instruction.Operands.Count > 1 && aliases.Contains(instruction.Operands[1]):
                    if (visiting && instruction.Operands.Skip(2).Any(aliases.Contains)) {
                        visit(instruction, stored, null);
                    }

                    var callee = instruction.Operands[0] is FunctionRefInst reference ? module?.Find(reference.Function) : null;
                    if (callee is null && instruction.Operands[0] is FunctionRefInst && module is not null) {
                        // Another unit's, or C's: what it stores cannot be seen.
                        stored.UnionWith(everything);
                        return;
                    }

                    if (callee is null) {
                        if (visiting) {
                            visit(instruction, stored, null);
                        }

                        return;
                    }

                    stored.UnionWith(Initialised(callee, module, initialised) ?? everything);
                    return;
            }

            if (!visiting) {
                return;
            }

            foreach (var operand in instruction.Operands) {
                if (aliases.Contains(operand)) {
                    visit(instruction, stored, null);
                    return;
                }

                if (fieldOf.TryGetValue(operand, out var read)) {
                    visit(instruction, stored, [read]);
                }
            }
        }
    }

    // =======================================================================
    // Types
    // =======================================================================

    /// <summary>
    ///     Each class's vtable keeps its superclass's slots, in order, and
    ///     names, in each, a function the module defines that takes the class
    ///     (or one it inherits from) as self and otherwise has the slot's
    ///     signature; each deinit is a function that takes the type and
    ///     returns nothing. The code generator puts these in the metadata
    ///     unchecked, and a mismatch is a call with the wrong arguments.
    /// </summary>
    static void VerifyTypes(SilModule module, List<string> errors) {
        FunctionType? Signature(string name) =>
            module.Find(name)?.Type ?? (module.Unit?.External.TryGetValue(name, out var external) == true ? external : null);

        void Deinit(NominalType type, string deinit) {
            if (Signature(deinit) is not { } signature) {
                errors.Add($"{type.Name}: its deinit @{deinit} is not a function the module defines");
            } else if (signature.Parameters.Length != 1 || signature.Parameters[0] != type && !SilType.SameErasingOpaque(signature.Parameters[0], type, opaqueOnly: true) || signature.Result != SilType.Unit || signature.Thrown is not null) {
                errors.Add($"{type.Name}: its deinit @{deinit} is {signature}, not ({type.Name}) -> ()");
            }
        }

        foreach (var type in module.Types) {
            if (type is StructType { Deinit: { } structDeinit }) {
                Deinit(type, structDeinit);
            }

            if (type is not ClassType classType) {
                continue;
            }

            if (classType.Deinit is { } deinit) {
                Deinit(classType, deinit);
            }

            var inherited = classType.Superclass?.VTable ?? [];
            for (var i = 0; i < inherited.Count; i++) {
                if (i >= classType.VTable.Count || classType.VTable[i].Method != inherited[i].Method) {
                    errors.Add($"{classType.Name}: vtable slot {i} must be #{inherited[i].Method}, as in {classType.Superclass!.Name}");
                }
            }

            for (var i = 0; i < classType.VTable.Count; i++) {
                var (method, function) = classType.VTable[i];
                if (Signature(function) is not { } signature) {
                    errors.Add($"{classType.Name}: vtable slot #{method} names @{function}, which the module does not define");
                    continue;
                }

                if (signature.Parameters.IsEmpty || signature.Parameters[0] is not ClassType self || !classType.IsSubclassOf(self) && !InheritsErasing(classType, self)) {
                    errors.Add($"{classType.Name}: vtable slot #{method} names @{function} : {signature}, which does not take a {classType.Name} as self");
                    continue;
                }

                if (i < inherited.Count && inherited[i].Method == method && Signature(inherited[i].Function) is { } slot && !Overrides(signature, slot)) {
                    errors.Add($"{classType.Name}: vtable slot #{method} names @{function} : {signature}, which does not match {slot}");
                }
            }
        }
    }

    /// <summary>
    ///     True when a method can sit in its base's slot: the same parameters
    ///     after self, and a result the base's callers can take.
    /// </summary>
    static bool Overrides(FunctionType method, FunctionType slot) =>
        method.Parameters.Length == slot.Parameters.Length
        && method.Parameters.Skip(1).SequenceEqual(slot.Parameters.Skip(1))
        && method.Thrown == slot.Thrown
        && (method.Result == slot.Result || method.Result is ClassType result && slot.Result is ClassType expected && result.IsSubclassOf(expected));

    /// <summary>Whether a class inherits from an instance of the generic class a method compiled once takes as self (#297).</summary>
    static bool InheritsErasing(ClassType type, ClassType self) {
        for (var at = type; at is not null; at = at.Superclass) {
            if (SilType.SameErasingOpaque(at, self, opaqueOnly: true)) {
                return true;
            }
        }

        return false;
    }
}
