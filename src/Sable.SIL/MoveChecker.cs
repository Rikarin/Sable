using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Move checking (docs/04 §3.2, docs/02 §2.3): a value of a
///     <c>@noncopy</c> type is moved, never copied, and a variable is not
///     used once its value has been moved out.
/// </summary>
/// <remarks>
///     <para>
///         Runs on each function as it is lowered, before any pass. The
///         lowering reads a move-only variable to borrow it with
///         <c>load_borrow</c>, and to consume it with <c>load [copy]</c>;
///         here every such copy becomes <c>load [take]</c>, which leaves the
///         slot empty — and an empty slot's destruction does nothing, so a
///         moved-from variable needs no other change.
///     </para>
///     <para>
///         Then a forward dataflow over each variable that is move-only, or
///         that an explicit <c>move</c> takes from: on every path it is
///         initialised, moved, or maybe moved — initialised on one path into
///         a block and moved on another. A read or a move where it may have
///         been moved is an error that says where the move was. A store
///         initialises it again.
///     </para>
///     <para>
///         Last, any copy of a move-only value still in the function is an
///         error: a borrowed parameter consumed, a part of a value moved out
///         of something else.
///     </para>
/// </remarks>
public static class MoveChecker {
    public static void Check(SilFunction function, SourceText source, DiagnosticBag diagnostics) {
        if (function.Blocks.Count == 0) {
            return;
        }

        int PositionOf(SilInstruction at) =>
            at.Location is { } location ? source.GetPosition(new LinePosition(location.Line, location.Column)) : 0;

        void Report(ErrorCode code, SilInstruction at, string message, DiagnosticLocation? related = null) =>
            diagnostics.Add(Diagnostic.Error(code, new TextSpan(PositionOf(at), 0), message).WithRelated(related));

        string Where(SilInstruction at) => at.Location is { } location ? $"line {location.Line}" : "earlier";

        // Every consuming read of a move-only variable is a move.
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is LoadInst { Kind: LoadKind.Copy, Address: AllocStackInst { VariableName: not null } slot } load
                    && IsMoveOnly(slot.ElementType)) {
                    var take = new LoadInst(slot, LoadKind.Take) { Block = block, Location = load.Location };
                    block.Instructions[i] = take;
                    Uses.Replace(function, load, take);
                }
            }
        }

        var variables = function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>()
            .Where(static s => s.VariableName is not null && !s.ElementType.IsTrivial)
            .Where(s => IsMoveOnly(s.ElementType) || function.Blocks.Any(b => b.Instructions.Any(i => i is LoadInst { Kind: LoadKind.Take } take && ReferenceEquals(take.Address, s))))
            .ToList();

        foreach (var variable in variables) {
            if (UseAfterMove(function, variable) is var (use, move)) {
                Report(
                    ErrorCode.ERR_UsedAfterMove,
                    use,
                    $"'{variable.VariableName}' is used after its value was moved out of it, at {Where(move)}"
                    + (IsMoveOnly(variable.ElementType) ? $"; '{variable.ElementType}' is @noncopy, so using it moves it" : ""),
                    move.Location is null ? null : new DiagnosticLocation(null, new TextSpan(PositionOf(move), 0), $"'{variable.VariableName}' is moved here")
                );
            }
        }

        // What is left that copies a move-only value cannot be made a move.
        var reported = new HashSet<SilLocation?>();
        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions).ToList()) {
            switch (instruction) {
                case CopyValueInst copy when IsMoveOnly(copy.Type) && reported.Add(copy.Location):
                    Report(
                        ErrorCode.ERR_CopyOfNoncopyable,
                        copy,
                        copy.Value is SilArgument { Block: var b } && b == function.Entry
                            ? $"a parameter of @noncopy type '{copy.Type}' is borrowed, so it cannot be moved; take it as 'consuming'"
                            : $"a value of @noncopy type '{copy.Type}' cannot be copied, and it cannot be moved out of what holds it"
                    );
                    break;

                // A captured variable lives in a box the closure shares: moved
                // out, the closure would find it empty, or move it again.
                case LoadInst { Kind: LoadKind.Copy } load when IsMoveOnly(load.Type) && CapturedName(load.Address) is { } captured && reported.Add(load.Location):
                    Report(
                        ErrorCode.ERR_CopyOfNoncopyable,
                        load,
                        $"'{captured}' is captured by a closure, so its @noncopy value '{load.Type}' can be borrowed but not moved, in the closure or out of it"
                    );
                    break;

                case LoadInst { Kind: LoadKind.Copy } load when IsMoveOnly(load.Type) && reported.Add(load.Location):
                    Report(
                        ErrorCode.ERR_PartialMove,
                        load,
                        $"a value of @noncopy type '{load.Type}' cannot be moved out of a field or an element; move the whole value"
                    );
                    break;
            }
        }
    }

    static bool IsMoveOnly(SilType type) => type is NominalType { IsNoncopyable: true };

    /// <summary>
    ///     The variable whose box an address is the value of — a local a
    ///     closure captures, reached through its slot or through the
    ///     closure's context — or null.
    /// </summary>
    static string? CapturedName(SilValue address) {
        // A box is the one-field class lowering keeps a captured variable in.
        if (address is not RefElementAddrInst { Field: 0, Reference: { Type: ClassType boxType } box }
            || !boxType.Name.StartsWith("Box$of$", StringComparison.Ordinal)) {
            return null;
        }

        var held = box switch {
            LoadInst load => load.Address,
            LoadBorrowInst load => load.Address,
            _ => null,
        };

        return held switch {
            AllocStackInst { VariableName: { } name } => name,
            RefElementAddrInst { Reference.Type: ClassType context, Field: var field } when field < context.Fields.Count => context.Fields[field].Name,
            _ => null,
        };
    }

    enum State {
        Unknown,
        Initialized,
        Moved,
        MaybeMoved,
    }

    /// <summary>The first read or move of the variable where it may already have been moved, with that move.</summary>
    static (SilInstruction Use, SilInstruction Move)? UseAfterMove(SilFunction function, AllocStackInst variable) {
        var blocks = SilVerifier.ReversePostorder(function);
        var predecessors = blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in blocks) {
            foreach (var successor in block.Successors) {
                if (predecessors.TryGetValue(successor, out var list)) {
                    list.Add(block);
                }
            }
        }

        var exit = new Dictionary<SilBlock, (State State, SilInstruction? Move)>();
        (SilInstruction, SilInstruction)? found = null;

        // To a fixed point: a loop's back edge can carry a move round.
        for (var round = 0; round < 2 * blocks.Count + 2; round++) {
            var changed = false;
            foreach (var block in blocks) {
                var (state, move) = Merge(predecessors[block].Where(exit.ContainsKey).Select(p => exit[p]));
                foreach (var instruction in block.Instructions) {
                    switch (Effect(instruction, variable)) {
                        case Access.Write:
                            (state, move) = (State.Initialized, null);
                            break;

                        case Access.Read or Access.Take when state is State.Moved or State.MaybeMoved:
                            found ??= (instruction, move!);
                            break;

                        case Access.Take:
                            (state, move) = (State.Moved, instruction);
                            break;

                        case Access.Destroy:
                            (state, move) = (State.Unknown, null);
                            break;
                    }
                }

                if (!exit.TryGetValue(block, out var old) || old != (state, move)) {
                    exit[block] = (state, move);
                    changed = true;
                }
            }

            if (!changed) {
                break;
            }
        }

        return found;
    }

    static (State, SilInstruction?) Merge(IEnumerable<(State State, SilInstruction? Move)> incoming) {
        var state = State.Unknown;
        SilInstruction? move = null;
        foreach (var (s, m) in incoming) {
            move ??= m;
            state = (state, s) switch {
                (State.Unknown, _) => s,
                (_, State.Unknown) => state,
                _ when state == s => state,
                _ => State.MaybeMoved,
            };
        }

        return (state, move);
    }

    enum Access {
        None,
        Read,
        Take,
        Write,
        Destroy,
    }

    /// <summary>What an instruction does to the variable: reads it, moves out of it, writes it, or ends it.</summary>
    static Access Effect(SilInstruction instruction, AllocStackInst variable) {
        static bool Reaches(SilValue address, AllocStackInst slot) =>
            address switch {
                AllocStackInst s => ReferenceEquals(s, slot),
                StructElementAddrInst element => Reaches(element.Address, slot),
                TupleElementAddrInst element => Reaches(element.Address, slot),
                _ => false,
            };

        return instruction switch {
            StoreInst store when ReferenceEquals(store.Address, variable) => Access.Write,
            LoadInst { Kind: LoadKind.Take } take when ReferenceEquals(take.Address, variable) => Access.Take,
            DestroyAddrInst destroy when ReferenceEquals(destroy.Address, variable) => Access.Destroy,
            DeallocStackInst => Access.None,
            StructElementAddrInst or TupleElementAddrInst => Access.None,
            _ when instruction.Operands.Any(o => o.Type is AddressType && Reaches(o, variable)) => Access.Read,
            _ => Access.None,
        };
    }
}
