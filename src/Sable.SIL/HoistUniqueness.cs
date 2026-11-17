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
