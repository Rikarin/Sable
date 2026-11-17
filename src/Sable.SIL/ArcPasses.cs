namespace Sable.SIL;

/// <summary>
///     Where each value is used: the instructions that take it as an operand,
///     in the order they appear.
/// </summary>
sealed class Uses {
    readonly Dictionary<SilValue, List<SilInstruction>> _uses = [];

    public Uses(SilFunction function) {
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                foreach (var operand in instruction.Operands.Distinct()) {
                    if (!_uses.TryGetValue(operand, out var list)) {
                        _uses[operand] = list = [];
                    }

                    list.Add(instruction);
                }
            }
        }
    }

    public IReadOnlyList<SilInstruction> Of(SilValue value) => _uses.GetValueOrDefault(value) ?? [];

    /// <summary>Every operand of every instruction that is <paramref name="from" /> becomes <paramref name="to" />.</summary>
    public static void Replace(SilFunction function, SilValue from, SilValue to) {
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                for (var i = 0; i < instruction.Operands.Count; i++) {
                    if (ReferenceEquals(instruction.Operands[i], from)) {
                        instruction.SetOperand(i, to);
                    }
                }
            }
        }
    }

    /// <summary>True when the user consumes the value at one of its operand positions.</summary>
    public static bool Consumes(SilInstruction user, SilValue value) {
        if (user is CondBranchInst conditional) {
            var i = 0;
            foreach (var argument in conditional.TrueArguments) {
                if (ReferenceEquals(argument, value) && !conditional.WhenTrue.Arguments[i].IsGuaranteed) {
                    return true;
                }

                i++;
            }

            i = 0;
            foreach (var argument in conditional.FalseArguments) {
                if (ReferenceEquals(argument, value) && !conditional.WhenFalse.Arguments[i].IsGuaranteed) {
                    return true;
                }

                i++;
            }

            return false;
        }

        return SilVerifier.Consumed(user).Any(i => ReferenceEquals(user.Operands[i], value));
    }
}

/// <summary>
///     Reads a value out of storage without copying it, where the copy was
///     only ever borrowed: <c>load [copy]</c> … <c>destroy_value</c> becomes
///     <c>load_borrow</c> … <c>end_borrow</c> — a retain and a release fewer.
/// </summary>
/// <remarks>
///     <para>
///         Every use must be in the load's block — or, where the block
///         branches, after it in the one block the branches meet at — the
///         only consuming one a <c>destroy_value</c>. Between the load and the destroy nothing may
///         write the storage: for a stack slot, nothing that is given its
///         address other than to read it; for any other storage — a field of
///         an object, an inout parameter — nothing that writes at all, calls
///         nothing, and destroys nothing, since a deinit can write anywhere
///         and free the object the storage is in.
///     </para>
///     <para>
///         The naive lowering reads every local, and every element of an
///         array, this way; it is the commonest copy there is.
///     </para>
/// </remarks>
public sealed class BorrowLoads : ISilPass {
    public string Name => "borrow-loads";

    public void Run(SilFunction function) {
        var uses = new Uses(function);
        var predecessors = function.Blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in function.Blocks) {
            foreach (var successor in block.Successors) {
                predecessors[successor].Add(block);
            }
        }

        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not LoadInst { Kind: LoadKind.Copy } load) {
                    continue;
                }

                var ends = Destroyer(load, block, i, uses) is { } end
                    ? [end]
                    : DestroyerAfterJoin(load, block, i, uses) is { } joined
                        ? [joined]
                        : DestroyersAcrossBranch(load, block, i, uses, predecessors);
                if (ends is null) {
                    continue;
                }

                var borrow = new LoadBorrowInst(load.Address) { Block = block, Location = load.Location };
                block.Instructions[i] = borrow;
                Uses.Replace(function, load, borrow);
                foreach (var destroy in ends) {
                    var at = destroy.Block.Instructions.IndexOf(destroy);
                    destroy.Block.Instructions[at] = new EndBorrowInst(borrow) { Block = destroy.Block, Location = destroy.Location };
                }
            }
        }
    }

    /// <summary>
    ///     The destroys of a load that its block branches on: one at the top
    ///     of each successor, which only that block leads to — the shape of
    ///     <c>if let</c>, <c>??</c> and <c>!</c>. Everything borrowed from the
    ///     value, a payload included, must be used before its destroy, and
    ///     nothing on the way may write the storage.
    /// </summary>
    static List<DestroyValueInst>? DestroyersAcrossBranch(
        LoadInst load,
        SilBlock block,
        int index,
        Uses uses,
        Dictionary<SilBlock, List<SilBlock>> predecessors
    ) {
        if (block.Terminator is not (SwitchEnumInst or CondBranchInst) || block.Terminator is CondBranchInst branch && branch.Operands.Skip(1).Contains(load)) {
            return null;
        }

        var successors = block.Successors.Distinct().ToList();
        var ends = new Dictionary<SilBlock, DestroyValueInst>();
        foreach (var user in uses.Of(load)) {
            if (user is DestroyValueInst destroy && successors.Contains(user.Block) && !ends.ContainsKey(user.Block)) {
                ends[user.Block] = destroy;
                continue;
            }

            if (Uses.Consumes(user, load)) {
                return null;
            }
        }

        // Every way out of the block ends the borrow, in a block only it leads to.
        if (successors.Any(s => !ends.ContainsKey(s) || predecessors[s].Count != 1)) {
            return null;
        }

        var slot = SlotOf(load.Address);
        var owner = Owner(load.Address);
        for (var k = index + 1; k < block.Instructions.Count - 1; k++) {
            if (MayWrite(block.Instructions[k], slot, owner)) {
                return null;
            }
        }

        foreach (var (successor, destroy) in ends) {
            var end = successor.Instructions.IndexOf(destroy);
            for (var k = 0; k < end; k++) {
                if (MayWrite(successor.Instructions[k], slot, owner)) {
                    return null;
                }
            }
        }

        // The value and all that is borrowed from it: used in the block after
        // the load, or in a successor before its destroy, and nowhere else.
        var borrowed = new List<SilValue> { load };
        for (var n = 0; n < borrowed.Count; n++) {
            foreach (var user in uses.Of(borrowed[n])) {
                if (user is DestroyValueInst && ReferenceEquals(borrowed[n], load)) {
                    continue;
                }

                if (user.Block == block) {
                    if (block.Instructions.IndexOf(user) <= index) {
                        return null;
                    }
                } else if (!ends.TryGetValue(user.Block, out var end) || user.Block.Instructions.IndexOf(user) > user.Block.Instructions.IndexOf(end)) {
                    return null;
                }

                switch (user) {
                    case StructExtractInst or TupleExtractInst or UpcastInst or UncheckedRefCastInst or RefElementAddrInst:
                        borrowed.Add(user);
                        break;

                    case SwitchEnumInst switchEnum when ReferenceEquals(switchEnum.Value, borrowed[n]):
                        foreach (var (_, target) in switchEnum.Cases) {
                            if (target.Arguments is [{ IsGuaranteed: true } payload]) {
                                borrowed.Add(payload);
                            }
                        }

                        break;

                    case { IsTerminator: true }:
                        if (user is not SwitchEnumInst && !(user is CondBranchInst && ReferenceEquals(user.Operands[0], borrowed[n]))) {
                            return null;
                        }

                        break;
                }
            }
        }

        return [..ends.Values];
    }

    /// <summary>
    ///     The destroy of a load used in one later block, which every way on
    ///     from the load's block reaches through blocks that only branch —
    ///     the shape of an index or argument computed by <c>?:</c>,
    ///     <c>&amp;&amp;</c> or <c>||</c> after the array is read,
    ///     <c>values[c ? i : j]</c>. Uses in the load's own block come after
    ///     it; in the later block, before the destroy. Nothing on the way may
    ///     write the storage.
    /// </summary>
    static DestroyValueInst? DestroyerAfterJoin(LoadInst load, SilBlock block, int index, Uses uses) {
        if (block.Terminator is not (BranchInst or CondBranchInst)) {
            return null;
        }

        SilBlock? join = null;
        DestroyValueInst? destroy = null;
        foreach (var user in uses.Of(load)) {
            if (user.IsTerminator || user is not DestroyValueInst && Uses.Consumes(user, load)) {
                return null;
            }

            if (user.Block == block) {
                if (user is DestroyValueInst || block.Instructions.IndexOf(user) <= index) {
                    return null;
                }

                continue;
            }

            join ??= user.Block;
            if (user.Block != join) {
                return null;
            }

            if (user is DestroyValueInst found) {
                if (destroy is not null) {
                    return null;
                }

                destroy = found;
            }
        }

        if (join is null || destroy is null) {
            return null;
        }

        var end = join.Instructions.IndexOf(destroy);
        foreach (var user in uses.Of(load)) {
            if (user.Block == join && join.Instructions.IndexOf(user) > end) {
                return null;
            }
        }

        var slot = SlotOf(load.Address);
        var owner = Owner(load.Address);
        for (var k = index + 1; k < block.Instructions.Count - 1; k++) {
            if (MayWrite(block.Instructions[k], slot, owner)) {
                return null;
            }
        }

        for (var k = 0; k < end; k++) {
            if (MayWrite(join.Instructions[k], slot, owner)) {
                return null;
            }
        }

        // The blocks between: each only branches on, towards the join, and
        // writes nothing; none leads back to the load.
        var between = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>(block.Successors);
        while (pending.TryPop(out var next)) {
            if (next == join || between.Contains(next)) {
                continue;
            }

            if (next == block || between.Count >= 32 || next.Terminator is not (BranchInst or CondBranchInst)) {
                return null;
            }

            between.Add(next);

            for (var k = 0; k < next.Instructions.Count - 1; k++) {
                if (MayWrite(next.Instructions[k], slot, owner)) {
                    return null;
                }
            }

            foreach (var successor in next.Successors) {
                pending.Push(successor);
            }
        }

        return destroy;
    }

    /// <summary>The destroy that ends the load's life, if borrowing instead is safe.</summary>
    static DestroyValueInst? Destroyer(LoadInst load, SilBlock block, int index, Uses uses) {
        DestroyValueInst? destroy = null;
        foreach (var user in uses.Of(load)) {
            if (user.Block != block) {
                return null;
            }

            if (user is DestroyValueInst found && destroy is null) {
                destroy = found;
                continue;
            }

            if (user.IsTerminator || Uses.Consumes(user, load)) {
                return null;
            }
        }

        if (destroy is null) {
            return null;
        }

        var end = block.Instructions.IndexOf(destroy);
        if (end < index) {
            return null;
        }

        // Every use is between the load and the destroy, so the borrow covers it.
        foreach (var user in uses.Of(load)) {
            if (block.Instructions.IndexOf(user) > end) {
                return null;
            }
        }

        var slot = SlotOf(load.Address);
        var owner = Owner(load.Address);
        for (var k = index + 1; k < end; k++) {
            if (MayWrite(block.Instructions[k], slot, owner)) {
                return null;
            }
        }

        return destroy;
    }

    static AllocStackInst? SlotOf(SilValue address) =>
        address switch {
            AllocStackInst slot => slot,
            StructElementAddrInst element => SlotOf(element.Address),
            TupleElementAddrInst element => SlotOf(element.Address),
            _ => null,
        };

    /// <summary>
    ///     The object storage is a field of, followed through fields and
    ///     casts to what keeps it alive; null for a parameter's, which the
    ///     caller keeps alive, or for storage that is no object's.
    /// </summary>
    static SilValue? Owner(SilValue address) =>
        address switch {
            RefElementAddrInst field => KeptBy(field.Reference),
            StructElementAddrInst element => Owner(element.Address),
            TupleElementAddrInst element => Owner(element.Address),
            _ => null,
        };

    static SilValue? KeptBy(SilValue reference) =>
        reference switch {
            SilArgument argument when argument.Block == argument.Block.Function.Entry => null,
            UpcastInst upcast => KeptBy(upcast.Value),
            UncheckedRefCastInst cast => KeptBy(cast.Value),
            StructExtractInst extract => KeptBy(extract.Value),
            TupleExtractInst extract => KeptBy(extract.Value),
            BuiltinInst { Name: "array_get_borrowed" } element => KeptBy(element.Operands[0]),
            _ => reference,
        };

    /// <summary>True when an instruction might change what the storage holds, or free it.</summary>
    static bool MayWrite(SilInstruction instruction, AllocStackInst? slot, SilValue? owner) {
        if (slot is not null) {
            // A stack slot is written only by something given its address.
            foreach (var operand in instruction.Operands) {
                if (operand.Type is AddressType && ReferenceEquals(SlotOf(operand), slot)
                    && instruction is not (LoadInst { Kind: not LoadKind.Take } or LoadBorrowInst or StructElementAddrInst or TupleElementAddrInst)) {
                    return true;
                }
            }

            return false;
        }

        // The object the storage is in must outlive the borrow.
        if (owner is not null && (Uses.Consumes(instruction, owner) || instruction is EndBorrowInst { Borrowed: var ended } && ReferenceEquals(ended, owner))) {
            return true;
        }

        var module = instruction.Block.Function.Module;
        return instruction switch {
            // Writing a stack slot cannot reach an object's field, and the
            // value it replaces, released, runs no code if no deinit can.
            StoreInst store when SlotOf(store.Address) is not null =>
                module is null || module.MayRunDeinit(store.Value.Type),
            DestroyAddrInst destroy when SlotOf(destroy.Address) is not null =>
                module is null || module.MayRunDeinit(((AddressType)destroy.Address.Type).Pointee),
            DestroyValueInst destroy => module is null || module.MayRunDeinit(destroy.Value.Type),
            DeallocStackInst or AllocStackInst => false,
            LoadInst { Kind: not LoadKind.Take } or LoadBorrowInst or EndBorrowInst => false,
            IntegerLiteralInst or FloatLiteralInst or StringLiteralInst or FunctionRefInst => false,
            StructInst or TupleInst or EnumInst or StructExtractInst or TupleExtractInst => false,
            StructElementAddrInst or TupleElementAddrInst or RefElementAddrInst => false,
            CopyValueInst or UpcastInst or UncheckedRefCastInst or ClassMethodInst => false,
            BuiltinInst builtin => builtin.Operands.Any(static o => o.Type is AddressType),
            _ => true,
        };
    }
}

/// <summary>
///     Removes copies that change nothing: a <c>copy_value</c> destroyed in
///     its own block while what it copied is still alive, and a copy of an
///     owned value destroyed straight after — where the copy can simply be
///     the value.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <b>A borrowed copy.</b> <c>%c = copy_value %v</c>, used only
///             without being consumed and then destroyed, all in one block,
///             while <c>%v</c> — or what it borrows from — is not consumed in
///             between: <c>%c</c>'s uses take <c>%v</c>, and the copy and the
///             destroy go.
///         </item>
///         <item>
///             <b>A copy that outlives its source.</b> <c>%c = copy_value %v</c>
///             where <c>%v</c> is owned and its only consuming use is a
///             <c>destroy_value</c> later in the block: <c>%c</c> is <c>%v</c>,
///             which now lives as long as the copy did.
///         </item>
///     </list>
/// </remarks>
public sealed class ForwardCopies : ISilPass {
    public string Name => "forward-copies";

    public void Run(SilFunction function) {
        bool changed;
        do {
            changed = false;
            var uses = new Uses(function);
            foreach (var block in function.Blocks) {
                for (var i = 0; i < block.Instructions.Count; i++) {
                    if (block.Instructions[i] is LoadInst { Kind: LoadKind.Copy, Address: AllocStackInst } load && TryTake(function, block, i, load, uses)) {
                        changed = true;
                        break;
                    }

                    if (block.Instructions[i] is BuiltinInst { Name: "array_get" } read && !read.Type.IsTrivial) {
                        if (TryBorrowedElement(block, i, read, uses)) {
                            changed = true;
                            break;
                        }

                        continue;
                    }

                    if (block.Instructions[i] is not CopyValueInst copy) {
                        continue;
                    }

                    if (TryBorrowedCopy(function, block, i, copy, uses) || TryOutlivingCopy(function, block, i, copy, uses)
                        || TryHandOver(function, copy, uses)) {
                        changed = true;
                        break;
                    }
                }

                if (changed) {
                    break;
                }
            }
        } while (changed);
    }

    static bool TryBorrowedCopy(SilFunction function, SilBlock block, int index, CopyValueInst copy, Uses uses) {
        DestroyValueInst? destroy = null;
        foreach (var user in uses.Of(copy)) {
            if (user.Block != block) {
                return false;
            }

            if (user is DestroyValueInst found && destroy is null) {
                destroy = found;
                continue;
            }

            if (user.IsTerminator || Uses.Consumes(user, copy)) {
                return false;
            }
        }

        if (destroy is null) {
            return false;
        }

        var end = block.Instructions.IndexOf(destroy);
        if (uses.Of(copy).Any(u => block.Instructions.IndexOf(u) > end)) {
            return false;
        }

        // What the copy is of must stay alive to the destroy: neither it nor
        // what it borrows from may be consumed, or a borrow of it ended.
        var source = copy.Value;
        var root = Root(source);
        for (var k = index + 1; k < end; k++) {
            var instruction = block.Instructions[k];
            if (root is not null && (Uses.Consumes(instruction, root) || instruction is EndBorrowInst { Borrowed: var ended } && ReferenceEquals(ended, root))) {
                return false;
            }
        }

        // A value borrowed from somewhere unknown cannot be trusted to last.
        if (root is null && !IsParameterBorrow(source)) {
            return false;
        }

        Uses.Replace(function, copy, source);
        block.Instructions.Remove(copy);
        block.Instructions.Remove(destroy);
        return true;
    }

    /// <summary>
    ///     An element read out of an array only to be looked at, then
    ///     destroyed in the same block, is read in place instead: no retain,
    ///     no release. The array it is in must stay alive to the destroy, as
    ///     a copy's source must.
    /// </summary>
    static bool TryBorrowedElement(SilBlock block, int index, BuiltinInst read, Uses uses) {
        DestroyValueInst? destroy = null;
        foreach (var user in uses.Of(read)) {
            if (user.Block != block) {
                return false;
            }

            if (user is DestroyValueInst found && destroy is null) {
                destroy = found;
                continue;
            }

            if (user.IsTerminator || Uses.Consumes(user, read)) {
                return false;
            }
        }

        if (destroy is null) {
            return false;
        }

        var end = block.Instructions.IndexOf(destroy);
        if (uses.Of(read).Any(u => block.Instructions.IndexOf(u) > end)) {
            return false;
        }

        var source = read.Operands[0];
        var root = Root(source);
        if (root is null && !IsParameterBorrow(source)) {
            return false;
        }

        // A borrow that ends before the destroy is carried on past it —
        // 'before(a[i], a[j])' borrows the array for each read and ends it at
        // once — as long as nothing between could change what it borrows:
        // for a local slot, nothing writes the slot; for anything else, the
        // rule below, nothing calls out.
        EndBorrowInst? early = null;
        for (var k = index + 1; k < end; k++) {
            var instruction = block.Instructions[k];
            if (instruction is EndBorrowInst { Borrowed: var endedHere } endsRoot && ReferenceEquals(endedHere, root)
                && root is LoadBorrowInst && early is null) {
                early = endsRoot;
                continue;
            }

            if (root is not null && (Uses.Consumes(instruction, root) || instruction is EndBorrowInst { Borrowed: var ended } && ReferenceEquals(ended, root))) {
                return false;
            }

            // Anything that could change the array in place. An owned array is
            // ours alone; one borrowed from a local slot changes only through
            // the slot's address — reading it again, by a borrow, does not;
            // one borrowed from a parameter, through anything that calls out
            // or is given an address.
            if (root is LoadBorrowInst { Address: AllocStackInst slot }) {
                if (instruction is not LoadBorrowInst && instruction.Operands.Any(o => ReferenceEquals(o, slot))) {
                    return false;
                }
            } else if ((root is null || root is LoadBorrowInst)
                && (instruction is ApplyInst or TryApplyInst || instruction is BuiltinInst builtin && builtin.Operands.Any(static o => o.Type is AddressType))) {
                return false;
            }
        }

        read.Rename("array_get_borrowed");
        if (early is not null) {
            block.Instructions.Remove(early);
            block.Instructions.Insert(block.Instructions.IndexOf(destroy) + 1, early);
        }

        block.Instructions.Remove(destroy);
        return true;
    }

    /// <summary>
    ///     A copy loaded out of a slot that is destroyed next, with only other
    ///     slots' ends between: the value is taken out instead — 'return
    ///     result' moves. Destruction stays where the scope puts it.
    /// </summary>
    static bool TryTake(SilFunction function, SilBlock block, int index, LoadInst load, Uses uses) {
        var slot = (AllocStackInst)load.Address;
        if (slot.Unmanaged) {
            return false;
        }

        for (var k = index + 1; k < block.Instructions.Count; k++) {
            var instruction = block.Instructions[k];
            if (instruction is DestroyAddrInst destroy && ReferenceEquals(destroy.Address, slot)) {
                var taken = new LoadInst(slot, LoadKind.Take) { Block = block, Location = load.Location };
                block.Instructions[index] = taken;
                Uses.Replace(function, load, taken);
                block.Instructions.Remove(destroy);
                return true;
            }

            // Only other slots' ends may come between: anything else would see
            // the value destroyed sooner than its scope says.
            if (instruction is not (DestroyAddrInst or DeallocStackInst or EndBorrowInst) || instruction.Operands.Any(o => ReferenceEquals(o, slot))) {
                return false;
            }
        }

        return false;
    }

    static bool TryOutlivingCopy(SilFunction function, SilBlock block, int index, CopyValueInst copy, Uses uses) {
        var source = copy.Value;
        if (SilVerifier.OwnershipOf(source) != Ownership.Owned) {
            return false;
        }

        var consumers = uses.Of(source).Where(u => Uses.Consumes(u, source)).ToList();
        if (consumers is not [DestroyValueInst destroy] || destroy.Block != block || block.Instructions.IndexOf(destroy) < index) {
            return false;
        }

        // Nothing may use the source after its destroy: the verifier holds
        // that, so the copy simply carries the value on — provided the copy
        // is not consumed while the source is still in use. Once the two are
        // one value, a use of the source, or of anything borrowed from it,
        // after the copy is consumed would use a value already given away:
        // 'task_spawn(copy)' followed by a store into the task's field.
        var destroyAt = block.Instructions.IndexOf(destroy);
        foreach (var consumer in uses.Of(copy).Where(u => Uses.Consumes(u, copy) && u.Block == block)) {
            var consumedAt = block.Instructions.IndexOf(consumer);
            for (var k = consumedAt + 1; k < destroyAt; k++) {
                if (block.Instructions[k].Operands.Any(o => DependsOn(o, source))) {
                    return false;
                }
            }
        }

        Uses.Replace(function, copy, source);
        block.Instructions.Remove(copy);
        block.Instructions.Remove(destroy);
        return true;
    }

    /// <summary>
    ///     An owned value used once, by a copy, and otherwise only destroyed
    ///     — an owned parameter destroyed where the function leaves — is
    ///     handed over: the copy becomes the value, and the destroys go. The
    ///     copy must run on every path to the destroys, and once: its block
    ///     dominates theirs and is in no loop.
    /// </summary>
    static bool TryHandOver(SilFunction function, CopyValueInst copy, Uses uses) {
        var source = copy.Value;
        if (SilVerifier.OwnershipOf(source) != Ownership.Owned) {
            return false;
        }

        var destroys = new List<DestroyValueInst>();
        foreach (var user in uses.Of(source)) {
            switch (user) {
                case DestroyValueInst destroy:
                    destroys.Add(destroy);
                    break;
                case CopyValueInst other when ReferenceEquals(other, copy):
                    break;
                default:
                    return false;
            }
        }

        if (destroys.Count == 0 || InLoop(copy.Block)) {
            return false;
        }

        var dominators = SilVerifier.Dominators(function);
        if (destroys.Any(d => !dominators.TryGetValue(d.Block, out var set) || !set.Contains(copy.Block)
                || d.Block == copy.Block && d.Block.Instructions.IndexOf(d) < d.Block.Instructions.IndexOf(copy))) {
            return false;
        }

        Uses.Replace(function, copy, source);
        copy.Block.Instructions.Remove(copy);
        foreach (var destroy in destroys) {
            destroy.Block.Instructions.Remove(destroy);
        }

        return true;
    }

    /// <summary>
    ///     True when the value is the source, or reaches it through what is
    ///     not owned on its own — a field's address, a borrow, a cast — and so
    ///     is only good while the source is.
    /// </summary>
    static bool DependsOn(SilValue value, SilValue source) {
        for (var depth = 0; depth < 32; depth++) {
            if (ReferenceEquals(value, source)) {
                return true;
            }

            if (value is not SilInstruction { Operands.Count: > 0 } instruction || SilVerifier.OwnershipOf(value) == Ownership.Owned) {
                return false;
            }

            value = instruction.Operands[0];
        }

        return true;
    }

    /// <summary>True when a block can be reached again from itself.</summary>
    static bool InLoop(SilBlock block) {
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>(block.Successors);
        while (pending.Count > 0) {
            var next = pending.Pop();
            if (next == block) {
                return true;
            }

            if (seen.Add(next)) {
                foreach (var successor in next.Successors) {
                    pending.Push(successor);
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     What keeps a value alive: an owned value itself, the load_borrow a
    ///     value is borrowed through, or null for a borrow of a parameter,
    ///     which outlives the function.
    /// </summary>
    static SilValue? Root(SilValue value) =>
        value switch {
            _ when SilVerifier.OwnershipOf(value) == Ownership.Owned => value,
            LoadBorrowInst => value,
            BuiltinInst { Name: "array_get_borrowed" } element => Root(element.Operands[0]),
            StructExtractInst extract => Root(extract.Value),
            TupleExtractInst extract => Root(extract.Value),
            UpcastInst upcast => Root(upcast.Value),
            UncheckedRefCastInst cast => Root(cast.Value),
            _ => null,
        };

    /// <summary>True for a parameter, or something borrowed from one through fields and casts.</summary>
    static bool IsParameterBorrow(SilValue value) =>
        value switch {
            SilArgument argument => argument.Block == argument.Block.Function.Entry,
            BuiltinInst { Name: "array_get_borrowed" } element => IsParameterBorrow(element.Operands[0]),
            StructExtractInst extract => IsParameterBorrow(extract.Value),
            TupleExtractInst extract => IsParameterBorrow(extract.Value),
            UpcastInst upcast => IsParameterBorrow(upcast.Value),
            UncheckedRefCastInst cast => IsParameterBorrow(cast.Value),
            _ => false,
        };
}
