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
    ///     <c>if let</c>, <c>??</c> and <c>!</c>, whose none case only traps
    ///     and need not destroy it, and of a call that can throw. Everything borrowed from the
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
        if (block.Terminator is not (SwitchEnumInst or CondBranchInst or TryApplyInst) || block.Terminator is CondBranchInst branch && branch.Operands.Skip(1).Contains(load)) {
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

        // Every way out of the block ends the borrow, in a block only it
        // leads to — or only traps, as the none case of a '!' does.
        if (successors.Any(s => !ends.ContainsKey(s) && s.Terminator is not UnreachableInst || predecessors[s].Count != 1)) {
            return null;
        }

        var slot = ExclusiveOf(load.Address);
        var owner = Owner(load.Address);
        for (var k = index + 1; k < block.Instructions.Count; k++) {
            // A branch writes nothing; a call that can throw is a call.
            if (block.Instructions[k] is { IsTerminator: false } or TryApplyInst && MayWrite(block.Instructions[k], slot, owner)) {
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
                        if (user is not (SwitchEnumInst or TryApplyInst) && !(user is CondBranchInst && ReferenceEquals(user.Operands[0], borrowed[n]))) {
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
    ///     <c>&amp;&amp;</c>, <c>||</c> or <c>??</c> after the array is read,
    ///     <c>values[c ? i : j]</c>, or a dictionary's key held while the
    ///     value it is stored with is worked out,
    ///     <c>counts[word] = (counts[word] ?? 0) + 1</c>. Uses in the load's own block come after
    ///     it; in the later block, before the destroy. Nothing on the way may
    ///     write the storage.
    /// </summary>
    static DestroyValueInst? DestroyerAfterJoin(LoadInst load, SilBlock block, int index, Uses uses) {
        if (block.Terminator is not (BranchInst or CondBranchInst or SwitchEnumInst)) {
            return null;
        }

        SilBlock? join = null;
        DestroyValueInst? destroy = null;
        var elsewhere = new List<SilInstruction>();
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

            if (user is DestroyValueInst found) {
                if (destroy is not null) {
                    return null;
                }

                destroy = found;
                join = user.Block;
                continue;
            }

            elsewhere.Add(user);
        }

        if (join is null || destroy is null) {
            return null;
        }

        // A use outside the load's block and the join is in a block between,
        // which only the region leads into — a call guessed at by
        // SpeculativeDevirtualise, with the value cast — and is checked below.
        var inJoin = elsewhere.Where(u => u.Block == join).ToList();
        elsewhere.RemoveAll(u => u.Block == join);

        var end = join.Instructions.IndexOf(destroy);
        if (inJoin.Any(u => join.Instructions.IndexOf(u) > end)) {
            return null;
        }

        var slot = ExclusiveOf(load.Address);
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

            if (next == block || between.Count >= 32 || next.Terminator is not (BranchInst or CondBranchInst or SwitchEnumInst)) {
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

        // Uses between must be where the borrow surely is: in blocks entered
        // only from the region, and only looking — what is derived from the
        // value must not reach a terminator.
        if (elsewhere.Count > 0) {
            var region = new HashSet<SilBlock>(between) { block };
            foreach (var other in block.Function.Blocks) {
                if (!region.Contains(other) && other.Successors.Any(between.Contains)) {
                    return null;
                }
            }

            if (elsewhere.Any(u => !between.Contains(u.Block) || u is not (BuiltinInst or UncheckedRefCastInst or UpcastInst or StructExtractInst or ClassMethodInst or ApplyInst))) {
                return null;
            }

            if (elsewhere.Any(u => u is UncheckedRefCastInst or UpcastInst or StructExtractInst && !u.Type.IsTrivial
                    && uses.Of(u).Any(d => d.Block != u.Block || d.IsTerminator || Uses.Consumes(d, u)))) {
                return null;
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

        var slot = ExclusiveOf(load.Address);
        var owner = Owner(load.Address);
        // A field of an object borrowed from storage, the borrow ended before
        // the copy is destroyed — 'program.ops[pc]' of a class's field each
        // turn of a loop: the borrow's end sinks to the destroy, where
        // nothing on the way writes the storage it is of.
        EndBorrowInst? sunk = null;
        for (var k = index + 1; k < end; k++) {
            var instruction = block.Instructions[k];
            if (sunk is null && owner is LoadBorrowInst && instruction is EndBorrowInst { Borrowed: var ended } && ReferenceEquals(ended, owner)) {
                sunk = (EndBorrowInst)instruction;
                continue;
            }

            if (MayWrite(instruction, slot, owner)
                || sunk is not null && owner is LoadBorrowInst outer && MayWrite(instruction, ExclusiveOf(outer.Address), Owner(outer.Address))) {
                return null;
            }
        }

        if (sunk is not null) {
            block.Instructions.Remove(sunk);
            block.Instructions.Insert(block.Instructions.IndexOf(destroy) + 1, sunk);
        }

        return destroy;
    }

    /// <summary>
    ///     The storage an address is in when only what is given its address
    ///     can write it: a stack slot, or an <c>inout</c> parameter, which
    ///     exclusivity (docs/02 §2.3) keeps from any other access while the
    ///     call lasts — a callee not handed it, or a deinit, cannot reach it.
    /// </summary>
    internal static SilValue? ExclusiveOf(SilValue address) =>
        address switch {
            AllocStackInst slot => slot,
            // A global 'let' is written as the program starts and destroyed as
            // it ends, and by nothing between: no call can change it.
            GlobalAddrInst global when global.Block?.Function.Module?.ConstantGlobals.Contains(global.Global) == true => global,
            SilArgument { Type: AddressType } parameter when parameter.Block == parameter.Block.Function.Entry => parameter,
            StructElementAddrInst element => ExclusiveOf(element.Address),
            TupleElementAddrInst element => ExclusiveOf(element.Address),
            EnumPayloadAddrInst element => ExclusiveOf(element.Address),
            _ => null,
        };

    static AllocStackInst? SlotOf(SilValue address) =>
        address switch {
            AllocStackInst slot => slot,
            StructElementAddrInst element => SlotOf(element.Address),
            TupleElementAddrInst element => SlotOf(element.Address),
            EnumPayloadAddrInst element => SlotOf(element.Address),
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
            EnumPayloadAddrInst element => Owner(element.Address),
            _ => null,
        };

    static SilValue? KeptBy(SilValue reference) =>
        reference switch {
            SilArgument argument when argument.Block == argument.Block.Function.Entry => null,
            UpcastInst upcast => KeptBy(upcast.Value),
            UncheckedRefCastInst cast => KeptBy(cast.Value),
            StructExtractInst extract => KeptBy(extract.Value),
            TupleExtractInst extract => KeptBy(extract.Value),
            BuiltinInst { Name: "array_get_borrowed" or "slice_of_borrowed" } element => KeptBy(element.Operands[0]),
            _ => reference,
        };

    /// <summary>True when an instruction might change what the storage holds, or free it.</summary>
    // The same slot, or the same global, wherever its address was taken.
    static bool SameStorage(SilValue? a, SilValue b) =>
        ReferenceEquals(a, b) || a is GlobalAddrInst x && b is GlobalAddrInst y && x.Global == y.Global;

    internal static bool MayWrite(SilInstruction instruction, SilValue? slot, SilValue? owner) {
        if (slot is not null) {
            // A stack slot, or an inout parameter, is written only by something given its address.
            foreach (var operand in instruction.Operands) {
                if (operand.Type is AddressType && SameStorage(ExclusiveOf(operand), slot)
                    && instruction is not (LoadInst { Kind: not LoadKind.Take } or LoadBorrowInst or StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst)) {
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
            IntegerLiteralInst or FloatLiteralInst or StringLiteralInst or FunctionRefInst or MetadataInst => false,
            StructInst or TupleInst or EnumInst or StructExtractInst or TupleExtractInst => false,
            StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst or RefElementAddrInst => false,
            CopyValueInst or UpcastInst or UncheckedRefCastInst or ClassMethodInst => false,
            // An exclusivity marker or check reads where an access is; it writes nothing the program sees.
            BuiltinInst builtin => !SharedStorage.IsMarker(builtin.Name)
                && (SilBuiltins.RunsWork(builtin.Name) || builtin.Operands.Any(static o => o.Type is AddressType)),
            // A call of a function that writes no field and runs no deinit, nor calls one that may: a class's
            // array lent to it, as 'same(pool, ...)', stays in its field while it runs (BorrowFields.Summarise).
            ApplyInst { Callee: FunctionRefInst { Function: var callee } } => WritesOf(module, callee),
            TryApplyInst { Callee: FunctionRefInst { Function: var thrower } } => WritesOf(module, thrower),
            _ => true,
        };
    }

    /// <summary>
    ///     Runs the pass over a module with what each of its functions may
    ///     write summarised as it is now: made again for every run, as the
    ///     passes between two runs change bodies — what a callee releases,
    ///     where it moves out of a field — and a summary kept from an earlier
    ///     run could call a function that now writes a field one that does not.
    ///     A run only turns copies into borrows, which writes less, so the
    ///     summary holds while it lasts.
    /// </summary>
    public void Run(SilModule module) {
        var (outerModule, outerWrites) = (_summarised, _writes);
        (_summarised, _writes) = (module, BorrowFields.Summarise(module));
        try {
            foreach (var function in module.Functions) {
                Run(function);
            }
        } finally {
            (_summarised, _writes) = (outerModule, outerWrites);
        }
    }

    // What the module this thread's run of the pass is over may write; outside a run, every call may write.
    [ThreadStatic] static SilModule? _summarised;
    [ThreadStatic] static Dictionary<string, bool>? _writes;

    static bool WritesOf(SilModule? module, string callee) =>
        module is null || !ReferenceEquals(module, _summarised) || _writes!.GetValueOrDefault(callee, true);
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
///         <item>
///             <b>A struct only read.</b> <c>%s = struct (%a, %b)</c> whose
///             fields are read and which is then destroyed — a view of
///             something, such as a string's <c>utf8</c>: its fields are the
///             values it was made of, each destroyed where it was, and a
///             copy made to put in it is then one of the two above.
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
                    if (block.Instructions[i] is LoadInst { Kind: LoadKind.Copy, Address: AllocStackInst } load
                        && (TryTake(function, block, i, load, uses) || TryMoveToConsumer(function, block, i, load, uses))) {
                        changed = true;
                        break;
                    }

                    if (block.Instructions[i] is BuiltinInst { Name: "array_get" } read && !read.Type.IsTrivial) {
                        if (TryBorrowedElement(block, i, read, uses) || TryBorrowedElementAcrossSwitch(function, block, i, read, uses)) {
                            changed = true;
                            break;
                        }

                        continue;
                    }

                    if (block.Instructions[i] is StructInst made && TryTakeApart(function, block, made, uses)) {
                        changed = true;
                        break;
                    }

                    if (block.Instructions[i] is not CopyValueInst copy) {
                        continue;
                    }

                    if (TryBorrowedCopy(function, block, i, copy, uses) || TryOutlivingCopy(function, block, i, copy, uses)
                        || TryOutlivingProjection(function, block, i, copy, uses) || TryHandOver(function, copy, uses)) {
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

    /// <summary>
    ///     A struct made only to be read and destroyed — a view of a string's
    ///     bytes, for one of them — is the values it was made of: its fields
    ///     are those, and each is destroyed where it was.
    /// </summary>
    static bool TryTakeApart(SilFunction function, SilBlock block, StructInst made, Uses uses) {
        if (made.Type.IsTrivial || made.StructType is { Deinit: not null } or { IsInline: true }) {
            return false;
        }

        var users = uses.Of(made);
        if (users.Any(static u => u is not (StructExtractInst or DestroyValueInst { Uncounted: false })) || !users.Any(static u => u is DestroyValueInst)) {
            return false;
        }

        foreach (var user in users) {
            if (user is StructExtractInst extract) {
                Uses.Replace(function, extract, made.Operands[extract.Field]);
                extract.Block!.Instructions.Remove(extract);
                continue;
            }

            var at = user.Block!.Instructions.IndexOf(user);
            user.Block.Instructions.RemoveAt(at);
            foreach (var field in made.Operands.Where(static f => !f.Type.IsTrivial)) {
                user.Block.Instructions.Insert(at++, new DestroyValueInst(field) { Block = user.Block, Location = user.Location });
            }
        }

        block.Instructions.Remove(made);
        return true;
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
        // what it borrows from may be consumed, or a borrow of it ended —
        // unless that end can wait for the copy's: see Sinkable.
        var source = copy.Value;
        var root = Root(source);
        SilInstruction? sunk = null;
        for (var k = index + 1; k < end; k++) {
            var instruction = block.Instructions[k];
            if (root is not null && (Uses.Consumes(instruction, root) || instruction is EndBorrowInst { Borrowed: var ended } && ReferenceEquals(ended, root))) {
                if (sunk is null && Sinkable(instruction, root, block, k, end)) {
                    sunk = instruction;
                    continue;
                }

                return false;
            }
        }

        // A value borrowed from somewhere unknown cannot be trusted to last.
        if (root is null && !IsParameterBorrow(source)) {
            return false;
        }

        Uses.Replace(function, copy, source);
        block.Instructions.Remove(copy);
        if (sunk is not null) {
            block.Instructions.Remove(sunk);
            block.Instructions[block.Instructions.IndexOf(destroy)] = sunk;
        } else {
            block.Instructions.Remove(destroy);
        }

        return true;
    }

    /// <summary>
    ///     True when the end of what a copy borrows from — its
    ///     <c>destroy_value</c>, or the <c>end_borrow</c> of a load it was
    ///     borrowed through — can move down to where the copy is destroyed,
    ///     so that the copy is not needed: the release sunk to pair with the
    ///     retain. Whatever the copy was of stayed alive to there through the
    ///     copy; the rest of the value, freed later, is freed past nothing
    ///     that could see it — every instruction between neither calls,
    ///     writes, releases, nor publishes anything to another task (a
    ///     <c>share</c> is a builtin with an effect).
    /// </summary>
    static bool Sinkable(SilInstruction end, SilValue root, SilBlock block, int at, int until) {
        if (end is not (DestroyValueInst or EndBorrowInst) || !ReferenceEquals(end.Operands[0], root)) {
            return false;
        }

        for (var k = at + 1; k < until; k++) {
            if (!Inert(block.Instructions[k], root)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>An instruction with no effect anything else could observe, and that does not end <paramref name="root" />'s borrow.</summary>
    internal static bool Inert(SilInstruction instruction, SilValue? root = null) =>
        instruction switch {
            IntegerLiteralInst or FloatLiteralInst or StringLiteralInst or FunctionRefInst or MetadataInst => true,
            StructExtractInst or TupleExtractInst or UpcastInst or UncheckedRefCastInst => true,
            StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst or RefElementAddrInst => true,
            LoadInst { Kind: LoadKind.Trivial } or LoadBorrowInst or CopyValueInst => true,
            EndBorrowInst ended => root is null || !ReferenceEquals(ended.Borrowed, root),
            StructInst or TupleInst or EnumInst => instruction.Type.IsTrivial,
            BuiltinInst builtin => Simplify.Pure(builtin.Name) || builtin.Name == "array_get_borrowed",
            _ => false,
        };

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

            if (MayChangeArray(instruction, root)) {
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
    ///     Anything that could change the array an element is read from in
    ///     place. An owned array is ours alone; one borrowed from a local slot
    ///     changes only through the slot's address — reading it again, by a
    ///     borrow, does not; one borrowed from any other storage, through
    ///     anything that calls out or is given an address. One a parameter
    ///     lends does not change: who lends it holds a reference to it, in
    ///     nothing that is written while it is lent (<see cref="BorrowLoads" />),
    ///     and a change anywhere else is to a copy; an async function's is
    ///     left to the rule, as what it holds across a suspension its frame keeps.
    /// </summary>
    static bool MayChangeArray(SilInstruction instruction, SilValue? root) {
        if (root is LoadBorrowInst { Address: AllocStackInst slot }) {
            return instruction is not LoadBorrowInst && instruction.Operands.Any(o => ReferenceEquals(o, slot));
        }

        return (root is LoadBorrowInst || root is null && instruction.Block.Function.Type.IsAsync)
            && (instruction is ApplyInst or TryApplyInst || instruction is BuiltinInst builtin && !SharedStorage.IsMarker(builtin.Name)
                && (SilBuiltins.RunsWork(builtin.Name) || builtin.Operands.Any(static o => o.Type is AddressType)));
    }

    /// <summary>
    ///     An element read out of an array to be switched on — an optional
    ///     entry of a dictionary, unwrapped — and destroyed at the top of
    ///     each case, its payload only looked at before: read in place, as
    ///     <see cref="TryBorrowedElement" /> reads one used in its block. A
    ///     case that only traps need not destroy it. A borrow of the array
    ///     ended before the switch is ended in each case instead.
    /// </summary>
    static bool TryBorrowedElementAcrossSwitch(SilFunction function, SilBlock block, int index, BuiltinInst read, Uses uses) {
        if (block.Terminator is not SwitchEnumInst { Takes: false } switchEnum || !ReferenceEquals(switchEnum.Value, read)) {
            return false;
        }

        var source = read.Operands[0];
        var root = Root(source);
        if (root is null && !IsParameterBorrow(source) || root is not null and not LoadBorrowInst) {
            return false;
        }

        var cases = block.Successors.Distinct().ToList();
        var destroys = new Dictionary<SilBlock, DestroyValueInst>();
        foreach (var user in uses.Of(read)) {
            if (user == switchEnum) {
                continue;
            }

            if (user is DestroyValueInst destroy && cases.Contains(user.Block) && destroys.TryAdd(user.Block, destroy)) {
                continue;
            }

            if (user.Block != block || user.IsTerminator || Uses.Consumes(user, read)) {
                return false;
            }
        }

        // Each case is entered from the switch alone, and destroys the
        // element or only traps.
        foreach (var target in cases) {
            if (function.Blocks.Count(b => b.Successors.Contains(target)) != 1
                || !destroys.ContainsKey(target) && target.Terminator is not UnreachableInst) {
                return false;
            }
        }

        // The borrow of the array may end in the block, after the read; it
        // is carried into each case that destroys the element.
        EndBorrowInst? early = null;
        for (var k = index + 1; k < block.Instructions.Count - 1; k++) {
            var instruction = block.Instructions[k];
            if (root is not null && instruction is EndBorrowInst { Borrowed: var ended } && ReferenceEquals(ended, root) && early is null) {
                early = (EndBorrowInst)instruction;
                continue;
            }

            if (root is not null && Uses.Consumes(instruction, root) || MayChangeArray(instruction, root)) {
                return false;
            }
        }

        // A borrow not ended in the block goes on into each case, where it
        // must last to the destroy: the element is then read in place there.
        foreach (var (target, destroy) in destroys) {
            var end = target.Instructions.IndexOf(destroy);
            for (var k = 0; k < end; k++) {
                if (MayChangeArray(target.Instructions[k], root)
                    || early is null && root is not null && target.Instructions[k] is EndBorrowInst { Borrowed: var ended } && ReferenceEquals(ended, root)) {
                    return false;
                }
            }
        }

        // The element and all that is borrowed from it: used in the block
        // after the read, or in a case before its destroy.
        var borrowed = new List<SilValue> { read };
        for (var n = 0; n < borrowed.Count; n++) {
            foreach (var user in uses.Of(borrowed[n])) {
                if (user is DestroyValueInst && ReferenceEquals(borrowed[n], read)) {
                    continue;
                }

                if (user.Block == block) {
                    if (block.Instructions.IndexOf(user) <= index) {
                        return false;
                    }
                } else if (!destroys.TryGetValue(user.Block, out var end) || user.Block.Instructions.IndexOf(user) > user.Block.Instructions.IndexOf(end)) {
                    return false;
                }

                switch (user) {
                    case StructExtractInst or TupleExtractInst or UpcastInst or UncheckedRefCastInst or RefElementAddrInst when !user.Type.IsTrivial:
                        borrowed.Add(user);
                        break;

                    case SwitchEnumInst when user == switchEnum:
                        foreach (var target in cases) {
                            if (target.Arguments is [{ IsGuaranteed: true } payload]) {
                                borrowed.Add(payload);
                            }
                        }

                        break;

                    case { IsTerminator: true }:
                        return false;
                }
            }
        }

        read.Rename("array_get_borrowed");
        if (early is not null) {
            block.Instructions.Remove(early);
        }

        foreach (var (target, destroy) in destroys) {
            var at = target.Instructions.IndexOf(destroy);
            if (early is not null) {
                target.Instructions[at] = new EndBorrowInst(early.Borrowed) { Block = target, Location = early.Location };
            } else {
                target.Instructions.RemoveAt(at);
            }
        }

        return true;
    }

    /// <summary>
    ///     A copy loaded out of a slot that is destroyed next, with only other
    ///     slots' ends between, or what puts the copy away: the value is
    ///     taken out instead — 'return result' moves. Destruction stays where
    ///     the scope puts it.
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

            // Only other slots' ends may come between, and what puts the
            // copy away whole — into an enum, into an array — without
            // destroying it: anything else might see the value destroyed
            // sooner than its scope says. 'entries[i]!.value = v' copies the
            // entry out, changes it, and stores it back.
            if (instruction.Operands.Any(o => ReferenceEquals(o, slot))
                || instruction is not (DestroyAddrInst or DeallocStackInst or EndBorrowInst) && !PutsAway(instruction)) {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    ///     A local's last use, a call's <c>consuming</c> argument: the value
    ///     is moved into the call, not copied for it, so that what the callee
    ///     is given it holds alone — a buffer it changes in place rather than
    ///     copying it first. Nothing may use the slot between the load and its
    ///     destroy, in the load's block; and the value must hold no object
    ///     but arrays' buffers and strings: one an object holds, the move
    ///     could free sooner than the scope says — under an 'unowned'
    ///     reference to it, or before its deinit was to run.
    /// </summary>
    static bool TryMoveToConsumer(SilFunction function, SilBlock block, int index, LoadInst load, Uses uses) {
        var slot = (AllocStackInst)load.Address;
        if (slot.Unmanaged || uses.Of(load) is not [{ } call] || call is not (ApplyInst or TryApplyInst) || !Uses.Consumes(call, load)
            || !HoldsNoObject(load.Type)) {
            return false;
        }

        for (var k = index + 1; k < block.Instructions.Count; k++) {
            var instruction = block.Instructions[k];
            if (instruction is DestroyAddrInst { Uncounted: false } destroy && ReferenceEquals(destroy.Address, slot)) {
                var taken = new LoadInst(slot, LoadKind.Take) { Block = block, Location = load.Location };
                block.Instructions[index] = taken;
                Uses.Replace(function, load, taken);
                block.Instructions.Remove(destroy);
                return true;
            }

            if (instruction.Operands.Any(o => o.Type is AddressType && SlotUnder(o) == slot)) {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether a value holds nothing that can be referred to but by it: trivial bits, strings, arrays of such, and aggregates of them.</summary>
    static bool HoldsNoObject(SilType type, int depth = 0) =>
        depth < 16 && type switch {
            _ when type.IsTrivial => true,
            _ when type == SilType.String => true,
            ArrayType array => HoldsNoObject(array.Element, depth + 1),
            StructType { Deinit: null, IsInline: false } structType => structType.Fields.TrueForAll(f => HoldsNoObject(f.Type, depth + 1)),
            TupleType tuple => tuple.Elements.All(e => HoldsNoObject(e, depth + 1)),
            EnumType enumType => enumType.Cases.TrueForAll(c => c.Payload is null || HoldsNoObject(c.Payload, depth + 1)),
            _ => false,
        };

    static AllocStackInst? SlotUnder(SilValue address) =>
        address switch {
            AllocStackInst slot => slot,
            StructElementAddrInst element => SlotUnder(element.Address),
            TupleElementAddrInst element => SlotUnder(element.Address),
            EnumPayloadAddrInst element => SlotUnder(element.Address),
            _ => null,
        };

    /// <summary>
    ///     An instruction that leaves every value it is given alive: one with
    ///     no effect, a value made of others, or an element stored into an
    ///     array — which releases the element it replaces, as the store it
    ///     stands for would.
    /// </summary>
    static bool PutsAway(SilInstruction instruction) =>
        Inert(instruction)
        || instruction is StructInst or TupleInst or EnumInst
        || instruction is BuiltinInst { Name: "array_set" or "array_set_unique" or "array_append" };

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
    ///     A copy, only borrowed and destroyed, of what is all an owned value
    ///     holds — an object seen as its superclass, or the one field of a
    ///     struct that is not trivial — when the value is destroyed after
    ///     the copy, in its block: the copy is the value, kept alive until
    ///     the copy would have been destroyed, and destroyed there instead.
    ///     What an inlined callee returns and its caller reads one part of:
    ///     a digest's bytes, an existential's box.
    /// </summary>
    static bool TryOutlivingProjection(SilFunction function, SilBlock block, int index, CopyValueInst copy, Uses uses) {
        var owner = copy.Value switch {
            UpcastInst upcast => upcast.Value,
            UncheckedRefCastInst cast => cast.Value,
            StructExtractInst { Value.Type: StructType { Deinit: null, IsInline: false, IsNoncopyable: false } structType } extract
                when structType.Fields.Where((_, i) => i != extract.Field).All(static f => f.Type.IsTrivial) => extract.Value,
            _ => null,
        };
        if (owner is null || SilVerifier.OwnershipOf(owner) != Ownership.Owned) {
            return false;
        }

        var consumers = uses.Of(owner).Where(u => Uses.Consumes(u, owner)).ToList();
        if (consumers is not [DestroyValueInst ownerDestroy] || ownerDestroy.Block != block) {
            return false;
        }

        var ownerEnd = block.Instructions.IndexOf(ownerDestroy);
        if (ownerEnd < index) {
            return false;
        }

        var destroys = new List<DestroyValueInst>();
        foreach (var user in uses.Of(copy)) {
            if (user is DestroyValueInst { Uncounted: false } destroy) {
                // Destroyed in its block only after the owner was, or in a block after.
                if (destroy.Block == block && block.Instructions.IndexOf(destroy) < ownerEnd) {
                    return false;
                }

                destroys.Add(destroy);
                continue;
            }

            if (user.IsTerminator || Uses.Consumes(user, copy)) {
                return false;
            }
        }

        if (destroys.Count == 0) {
            return false;
        }

        foreach (var destroy in destroys) {
            var at = destroy.Block!.Instructions.IndexOf(destroy);
            destroy.Block.Instructions[at] = new DestroyValueInst(owner) { Block = destroy.Block, Location = destroy.Location };
        }

        Uses.Replace(function, copy, copy.Value);
        block.Instructions.Remove(copy);
        block.Instructions.Remove(ownerDestroy);
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
            SilArgument { IsGuaranteed: true } payload when SwitchedFrom(payload) is { } switched => Root(switched),
            BuiltinInst { Name: "array_get_borrowed" or "slice_of_borrowed" } element => Root(element.Operands[0]),
            StructExtractInst extract => Root(extract.Value),
            TupleExtractInst extract => Root(extract.Value),
            UpcastInst upcast => Root(upcast.Value),
            UncheckedRefCastInst cast => Root(cast.Value),
            _ => null,
        };

    /// <summary>
    ///     For a case's payload, borrowed: the value the one block that leads
    ///     to it switched on.
    /// </summary>
    static SilValue? SwitchedFrom(SilArgument payload) {
        var block = payload.Block;
        if (block == block.Function.Entry || block.Arguments.Count != 1) {
            return null;
        }

        SilValue? switched = null;
        foreach (var predecessor in block.Function.Blocks) {
            if (!predecessor.Successors.Contains(block)) {
                continue;
            }

            if (switched is not null || predecessor.Terminator is not SwitchEnumInst switchEnum || switchEnum.Default == block) {
                return null;
            }

            switched = switchEnum.Value;
        }

        return switched;
    }

    /// <summary>True for a parameter, or something borrowed from one through fields and casts.</summary>
    internal static bool IsParameterBorrow(SilValue value) =>
        value switch {
            SilArgument { IsGuaranteed: true } payload when SwitchedFrom(payload) is { } switched => IsParameterBorrow(switched),
            SilArgument argument => argument.Block == argument.Block.Function.Entry,
            BuiltinInst { Name: "array_get_borrowed" or "slice_of_borrowed" } element => IsParameterBorrow(element.Operands[0]),
            StructExtractInst extract => IsParameterBorrow(extract.Value),
            TupleExtractInst extract => IsParameterBorrow(extract.Value),
            UpcastInst upcast => IsParameterBorrow(upcast.Value),
            UncheckedRefCastInst cast => IsParameterBorrow(cast.Value),
            _ => false,
        };
}
