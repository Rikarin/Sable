namespace Sable.SIL;

/// <summary>
///     A copy of something held elsewhere for as long as the copy lasts is
///     that, borrowed: no retain, no release (docs/02 §2.2).
/// </summary>
/// <remarks>
///     <para>Three are made where nothing on the way can end what is held:</para>
///     <list type="bullet">
///         <item>
///             <b>An object's <c>let</c> field</b>, loaded. It is written
///             once, before anything can read it, so nothing that runs
///             meanwhile — a call, a store, a deinit — can change or release
///             what it holds, where <see cref="BorrowLoads" /> must see that
///             nothing may write the storage. It is enough that the object
///             lives.
///         </item>
///         <item>
///             <b>A copy of a borrowed value</b>, used past its own block:
///             <see cref="ForwardCopies" /> takes those that are not.
///         </item>
///         <item>
///             <b>An array viewed whole</b> (<c>slice_of</c>), for a member of
///             <c>Element[:]</c>: <c>slice_of_borrowed</c>.
///         </item>
///     </list>
///     <para>
///         What holds it must last until the copy is destroyed: a parameter
///         the caller lends, always; a borrow, until it ends; a value the
///         function owns, until it is consumed. Either end is looked for
///         along every path from the copy to its destroys.
///     </para>
///     <para>
///         An object's own initialiser stores its <c>let</c> fields, and code
///         taking an object apart may take them: neither is stepped over. An
///         async function is left alone: what it holds across a suspension
///         its frame keeps.
///     </para>
/// </remarks>
public sealed class BorrowHeld : ISilPass {
    public string Name => "borrow-held";

    public void Run(SilFunction function) {
        if (function.Type.IsAsync || !function.Blocks.Any(static b => b.Instructions.Any(static i => SourceOf(i) is not null))) {
            return;
        }

        var uses = new Uses(function);
        var switches = new Dictionary<SilBlock, SwitchEnumInst?>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks) {
            if (block.Terminator is SwitchEnumInst switched) {
                foreach (var (_, target) in switched.Cases) {
                    // A block two switches lead to: neither's operand alone is what it was given.
                    switches[target] = switches.ContainsKey(target) ? null : switched;
                }
            }
        }

        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                var copy = block.Instructions[i];
                if (SourceOf(copy) is not { } source) {
                    continue;
                }

                var destroys = new HashSet<SilInstruction>(ReferenceEqualityComparer.Instance);
                var only = true;
                foreach (var user in uses.Of(copy)) {
                    if (user is DestroyValueInst) {
                        destroys.Add(user);
                    } else if (Uses.Consumes(user, copy)) {
                        only = false;
                        break;
                    }
                }

                var field = copy is LoadInst load ? LetField(load.Address) : null;
                if (!only || destroys.Count == 0 || Keepers(source, function, switches) is not { } keepers || !Lives(copy, i, field, destroys, keepers)) {
                    continue;
                }

                switch (copy) {
                    case LoadInst { Address: var address }:
                        var borrow = new LoadBorrowInst(address) { Block = block, Location = copy.Location };
                        block.Instructions[i] = borrow;
                        Uses.Replace(function, copy, borrow);
                        foreach (var destroy in destroys) {
                            var at = destroy.Block.Instructions.IndexOf(destroy);
                            destroy.Block.Instructions[at] = new EndBorrowInst(borrow) { Block = destroy.Block, Location = destroy.Location };
                        }

                        break;

                    case BuiltinInst view:
                        view.Rename("slice_of_borrowed");
                        foreach (var destroy in destroys) {
                            destroy.Block.Instructions.Remove(destroy);
                        }

                        break;

                    default:
                        foreach (var destroy in destroys) {
                            destroy.Block.Instructions.Remove(destroy);
                        }

                        Uses.Replace(function, copy, source);
                        block.Instructions.Remove(copy);
                        i--;
                        break;
                }

                uses = new Uses(function);
            }
        }
    }

    /// <summary>What must last for the instruction's copy to be borrowed: the object a field is of, what is copied, the array viewed; null for any other.</summary>
    static SilValue? SourceOf(SilInstruction instruction) =>
        instruction switch {
            _ when instruction.Type.IsTrivial || SilType.IsIndirect(instruction.Type) => null,
            LoadInst { Kind: LoadKind.Copy } load => LetField(load.Address)?.Reference,
            CopyValueInst copy => copy.Value,
            BuiltinInst { Name: "slice_of" } view => view.Operands[0],
            _ => null,
        };

    /// <summary>The <c>let</c> field an address is, or is a part of.</summary>
    internal static RefElementAddrInst? LetField(SilValue address) =>
        address switch {
            RefElementAddrInst { Reference.Type: ClassType { IsCoroutineFrame: false } type } field when type.LetFields.Contains(field.Field) => field,
            StructElementAddrInst element => LetField(element.Address),
            TupleElementAddrInst element => LetField(element.Address),
            _ => null,
        };

    /// <summary>
    ///     The values whose end may be the value's: none for what a parameter
    ///     the caller lends holds; null when what keeps it alive is not known.
    /// </summary>
    static List<SilValue>? Keepers(SilValue value, SilFunction function, Dictionary<SilBlock, SwitchEnumInst?> switches) {
        for (var depth = 0; depth < 16; depth++) {
            switch (value) {
                case SilArgument argument when argument.Block == function.Entry:
                    return argument.Type is AddressType ? null : SilVerifier.OwnershipOf(argument) == Ownership.Guaranteed ? [] : [argument];

                // A payload, borrowed from what was switched on.
                case SilArgument { IsGuaranteed: true } payload when switches.GetValueOrDefault(payload.Block) is { } switched:
                    value = switched.Value;
                    break;

                case UpcastInst upcast:
                    value = upcast.Value;
                    break;
                case UncheckedRefCastInst cast:
                    value = cast.Value;
                    break;
                case StructExtractInst extract:
                    value = extract.Value;
                    break;
                case TupleExtractInst extract:
                    value = extract.Value;
                    break;
                case BuiltinInst { Name: "array_get_borrowed" or "slice_of_borrowed" } part:
                    value = part.Operands[0];
                    break;

                case LoadBorrowInst:
                    return [value];

                case LoadInst or CopyValueInst or ApplyInst or AllocRefInst when SilVerifier.OwnershipOf(value) == Ownership.Owned:
                    return [value];

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    ///     Along every path from the copy to a destroy of it, what it is of
    ///     stays, and no instruction writes the field or takes it.
    /// </summary>
    static bool Lives(SilInstruction copy, int index, RefElementAddrInst? field, HashSet<SilInstruction> destroys, List<SilValue> keepers) {
        bool Ends(SilInstruction instruction) {
            foreach (var keeper in keepers) {
                if (instruction is EndBorrowInst end ? ReferenceEquals(end.Borrowed, keeper) : instruction.Operands.Contains(keeper) && Uses.Consumes(instruction, keeper)) {
                    return true;
                }
            }

            return field is not null && instruction switch {
                LoadInst { Kind: not LoadKind.Take } or LoadBorrowInst or RefElementAddrInst or StructElementAddrInst or TupleElementAddrInst => false,
                _ => instruction.Operands.Any(o => o.Type is AddressType && LetField(o) is { } other && other.Field == field.Field),
            };
        }

        var seen = new HashSet<SilBlock>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(SilBlock Block, int Start)>();
        pending.Push((copy.Block, index + 1));
        while (pending.TryPop(out var next)) {
            var alive = true;
            for (var i = next.Start; i < next.Block.Instructions.Count; i++) {
                var instruction = next.Block.Instructions[i];
                if (destroys.Contains(instruction)) {
                    alive = false;
                    break;
                }

                // Back round to the copy with the one before still alive: not one copy.
                if (ReferenceEquals(instruction, copy) || Ends(instruction)) {
                    return false;
                }
            }

            if (alive) {
                foreach (var successor in next.Block.Successors) {
                    if (seen.Add(successor)) {
                        pending.Push((successor, 0));
                    }
                }
            }
        }

        return true;
    }
}
