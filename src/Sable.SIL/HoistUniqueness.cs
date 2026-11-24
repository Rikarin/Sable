namespace Sable.SIL;

/// <summary>
///     Copy-on-write, checked once per loop rather than once per write.
/// </summary>
/// <remarks>
///     Writing an element of an array first makes the array the only
///     reference to its buffer — a check, and a call to copy it when it is
///     shared, on every write. In a loop whose only uses of the array's
///     variable are reading it and writing its elements, nothing in the loop
///     can share the buffer again: so it is made unique once, before the
///     loop, and every write inside stores in place (<c>array_set_unique</c>),
///     with no call that could change the variable — which lets LLVM keep the
///     buffer and its count in registers. The variable is a local slot or an
///     inout parameter, which exclusivity keeps unshared for the call.
/// </remarks>
public sealed class HoistUniqueness : ISilPass {
    public string Name => "hoist-uniqueness";

    public void Run(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return;
        }

        var dominators = SilVerifier.Dominators(function);
        var predecessors = function.Blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in function.Blocks) {
            foreach (var successor in block.Successors) {
                if (predecessors.TryGetValue(successor, out var list)) {
                    list.Add(block);
                }
            }
        }

        // Natural loops: a header, and every block that reaches a back edge into it without passing it.
        var loops = new Dictionary<SilBlock, HashSet<SilBlock>>();
        foreach (var block in function.Blocks) {
            foreach (var header in block.Successors) {
                if (!dominators.TryGetValue(block, out var dominating) || !dominating.Contains(header)) {
                    continue;
                }

                if (!loops.TryGetValue(header, out var body)) {
                    loops[header] = body = [header];
                }

                var pending = new Stack<SilBlock>([block]);
                while (pending.Count > 0) {
                    var next = pending.Pop();
                    if (body.Add(next)) {
                        foreach (var predecessor in predecessors[next]) {
                            pending.Push(predecessor);
                        }
                    }
                }
            }
        }

        var uses = new Uses(function);
        UniqueViews(function, uses);

        // Outermost first: a whole nest made unique once, before all of it.
        foreach (var (header, body) in loops.OrderByDescending(static l => l.Value.Count)) {
            var addresses = body.SelectMany(static b => b.Instructions)
                .OfType<BuiltinInst>()
                .Where(static b => b.Name == "array_set")
                .Select(static b => b.Operands[0])
                .Distinct()
                .ToList();

            foreach (var address in addresses) {
                if (address is not (AllocStackInst or SilArgument { Type: AddressType }) || !OnlyReadAndWritten(address, body, uses)) {
                    continue;
                }

                foreach (var outside in predecessors[header].Where(p => !body.Contains(p))) {
                    var unique = new BuiltinInst("array_make_unique", SilType.Unit, address) { Block = outside, Location = outside.Terminator?.Location };
                    outside.Instructions.Insert(outside.Instructions.Count - 1, unique);
                }

                foreach (var write in body.SelectMany(static b => b.Instructions).OfType<BuiltinInst>()
                             .Where(b => b.Name == "array_set" && ReferenceEquals(b.Operands[0], address)).ToList()) {
                    write.Rename("array_set_unique");
                }

                uses = new Uses(function);
            }
        }
    }

    /// <summary>
    ///     An array's place viewed whole for a mutating member of
    ///     <c>Element[:]</c>, the member inlined: the array was made the only
    ///     reference to its buffer and moved into the view, so while the view
    ///     is only read and written in place — never copied, nor handed to a
    ///     call — every write through it stores in place, with no check.
    /// </summary>
    static void UniqueViews(SilFunction function, Uses uses) {
        foreach (var slot in function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>().ToList()) {
            if (slot.ElementType is not SliceType || !IsUniqueView(slot, uses)) {
                continue;
            }

            foreach (var write in uses.Of(slot).OfType<BuiltinInst>().ToList()) {
                if (write.Name.StartsWith("bytes_store_U", StringComparison.Ordinal)) {
                    write.Rename("bytes_store_unique_" + write.Name["bytes_store_".Length..]);
                } else if (write.Name == "array_set") {
                    write.Rename("array_set_unique");
                }
            }
        }
    }

    static bool IsUniqueView(AllocStackInst slot, Uses uses) {
        var made = false;
        foreach (var user in uses.Of(slot)) {
            switch (user) {
                case StoreInst { Kind: StoreKind.Init, Value: BuiltinInst { Name: "slice_of_take", Operands: [LoadInst { Kind: LoadKind.Take } taken] } } store
                    when !made && ReferenceEquals(store.Address, slot) && MadeUniqueJustBefore(taken):
                    made = true;
                    break;

                case BuiltinInst { Name: var name } write when (name.StartsWith("bytes_store_", StringComparison.Ordinal) || name is "array_set" or "array_set_unique")
                    && ReferenceEquals(write.Operands[0], slot) && !write.Operands.Skip(1).Any(o => ReferenceEquals(o, slot)):
                    break;

                case LoadBorrowInst borrowed:
                    foreach (var reader in uses.Of(borrowed)) {
                        var reads = reader is EndBorrowInst
                            || reader is BuiltinInst { Name: var read } && (read is "array_get" or "array_get_borrowed" or "array_count" || read.StartsWith("bytes_load_", StringComparison.Ordinal));
                        if (!reads) {
                            return false;
                        }
                    }

                    break;

                case LoadInst { Kind: LoadKind.Take }:
                case DestroyAddrInst:
                case DeallocStackInst:
                    break;

                case BuiltinInst marker when SharedStorage.IsMarker(marker.Name):
                    break;

                default:
                    return false;
            }
        }

        return made;
    }

    /// <summary>The array was taken from its place right after it was made the only reference to its buffer.</summary>
    static bool MadeUniqueJustBefore(LoadInst taken) {
        var instructions = taken.Block!.Instructions;
        var at = instructions.IndexOf(taken);
        return at > 0 && instructions[at - 1] is BuiltinInst { Name: "array_make_unique" } unique && ReferenceEquals(unique.Operands[0], taken.Address);
    }

    /// <summary>True when, inside the loop, the array is only borrowed to be read, and its elements written.</summary>
    static bool OnlyReadAndWritten(SilValue address, HashSet<SilBlock> body, Uses uses) {
        foreach (var user in uses.Of(address)) {
            if (!body.Contains(user.Block)) {
                continue;
            }

            switch (user) {
                case BuiltinInst { Name: "array_set" or "array_set_unique" } write when ReferenceEquals(write.Operands[0], address)
                    && !write.Operands.Skip(1).Any(o => ReferenceEquals(o, address)):
                    break;

                case LoadBorrowInst borrowed:
                    foreach (var reader in uses.Of(borrowed)) {
                        if (reader is not (EndBorrowInst or BuiltinInst { Name: "array_get" or "array_get_borrowed" or "array_count" })) {
                            return false;
                        }
                    }

                    break;

                default:
                    return false;
            }
        }

        return true;
    }
}
