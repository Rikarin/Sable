using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     The access a reference into its root's own storage holds (docs/02
///     §2.4, "References into what a value owns").
/// </summary>
/// <remarks>
///     <para>
///         A reference to an element of an array (<c>&amp;xs[0]</c>), or the one
///         a <c>mutating</c> member gives (<c>chunk.at(0)</c>), is an address in
///         storage its root owns — a buffer that moves when the array grows,
///         is copied when it is shared and written, and is freed with it. So
///         the reference holds an access to its root, as an <c>inout</c>
///         argument does for a call: from where it is made to its last use —
///         through every copy, variable, field address and call result that
///         comes to hold it — nothing else may use the root, read or write.
///         A use meanwhile is an error (S3062), as two inout arguments of one
///         variable are. The reference cannot outlive the root either: rules 2
///         and 3 hold it to the root's scope and to returning only what is
///         rooted in a parameter.
///     </para>
///     <para>
///         Rooted in an object's storage, or a global's, the access cannot be
///         proved — another reference to the object, or any code at all, may
///         reach the same array — so such a reference is used only where it
///         is made: kept in a variable, passed to a call or returned, it is an
///         error (S3067).
///     </para>
/// </remarks>
public static partial class RootChecker {
    static void CheckExclusive(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, Dictionary<string, HashSet<int>> summaries,
        SourceText source, DiagnosticBag diagnostics) {
        if (function.ExclusiveReferences.Count == 0) {
            return;
        }

        var present = new HashSet<SilValue>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks) {
            present.UnionWith(block.Arguments);
            present.UnionWith(block.Instructions);
        }

        var uses = new Uses(function);
        var reported = new HashSet<SilInstruction>(ReferenceEqualityComparer.Instance);
        foreach (var reference in function.ExclusiveReferences) {
            if (!present.Contains(reference) || Made(reference) is not { } made) {
                continue;
            }

            var roots = anchors.GetValueOrDefault(reference) ?? [];
            var (holders, used, kept) = Holders(reference, uses, summaries, anchors);
            var owned = roots.Where(static a => a is LocalAnchor or ParameterAnchor).ToHashSet();
            if (owned.Count == 0 || roots.Any(static a => a is HeapAnchor)) {
                if (kept is { } keeper && reported.Add(keeper)) {
                    diagnostics.Error(
                        ErrorCode.ERR_ElementReferenceKept,
                        SpanOf(keeper, source),
                        "a reference into an element of an object's or a global's array is used only where it is made: "
                        + "another reference to the object, or any code, may change the array while it is kept. "
                        + "To fix it: change the element where it is ('obj.items[i].v = 1'), or copy it out and store it back"
                    );
                }

                continue;
            }

            foreach (var conflict in Conflicts(function, made, used, holders, owned, anchors)) {
                if (!reported.Add(conflict)) {
                    continue;
                }

                var root = owned.First() switch {
                    LocalAnchor local => Describe(local.Slot, function) is var described && described.StartsWith("local ", StringComparison.Ordinal)
                        ? described["local ".Length..]
                        : described,
                    ParameterAnchor parameter => $"'{(parameter.Index < function.ParameterNames.Count ? function.ParameterNames[parameter.Index] : "?")}'",
                    _ => "its root",
                };
                var at = made.Location is { } location ? $"line {location.Line}" : "earlier";
                diagnostics.Error(
                    ErrorCode.ERR_OverlappingAccess,
                    SpanOf(conflict, source),
                    $"{root} is used while a reference into it, made at {at}, is still in use: the reference holds an access to {root} until its last use. "
                    + $"To fix it: use {root} after the reference's last use, or take the reference again after this"
                );
                break;
            }
        }
    }

    /// <summary>The instruction that makes a reference, or that the block argument holding it follows.</summary>
    static SilInstruction? Made(SilValue reference) =>
        reference switch {
            SilInstruction instruction => instruction,
            SilArgument { Block: var block } => block.Instructions.FirstOrDefault(),
            _ => null,
        };

    /// <summary>
    ///     What comes to hold a reference — copies, the variables it is kept
    ///     in and what is read out of them, the addresses of its parts, what a
    ///     call that may return it gives — and every instruction that uses one
    ///     of them; with the first that keeps it past where it is made.
    /// </summary>
    static (HashSet<SilValue> Holders, HashSet<SilInstruction> Used, SilInstruction? Kept) Holders(
        SilValue reference, Uses uses, Dictionary<string, HashSet<int>> summaries, Dictionary<SilValue, HashSet<Anchor>> anchors) {
        var holders = new HashSet<SilValue>(ReferenceEqualityComparer.Instance) { reference };
        var used = new HashSet<SilInstruction>(ReferenceEqualityComparer.Instance);
        SilInstruction? kept = null;
        var pending = new Stack<SilValue>([reference]);
        void Hold(SilValue value) {
            if (holders.Add(value)) {
                pending.Push(value);
            }
        }

        while (pending.TryPop(out var holder)) {
            foreach (var user in uses.Of(holder)) {
                // The end of a holder's storage, or of a copy, reaches nothing through it.
                if (user is DeallocStackInst or DestroyAddrInst or DestroyValueInst or EndBorrowInst) {
                    continue;
                }

                used.Add(user);
                switch (user) {
                    case StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst or CopyValueInst:
                        Hold(user);
                        break;

                    case BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique" } element when ReferenceEquals(element.Operands[0], holder):
                        Hold(element);
                        break;

                    case LoadInst or LoadBorrowInst when holder is AllocStackInst && HoldsReference(user.Type):
                        Hold(user);
                        break;

                    // Kept in a variable: what is read out of it holds it too.
                    case StoreInst store when ReferenceEquals(store.Value, holder):
                        kept ??= store;
                        if (store.Address is AllocStackInst slot) {
                            Hold(slot);
                        }

                        break;

                    case StructInst or TupleInst or EnumInst when HoldsReference(user.Type):
                        kept ??= user;
                        Hold(user);
                        break;

                    case StructExtractInst or TupleExtractInst when HoldsReference(user.Type):
                        Hold(user);
                        break;

                    case BranchInst branch:
                        kept ??= branch;
                        for (var i = 0; i < branch.Operands.Count; i++) {
                            if (ReferenceEquals(branch.Operands[i], holder)) {
                                Hold(branch.Target.Arguments[i]);
                            }
                        }

                        break;

                    case CondBranchInst conditional: {
                        kept ??= conditional;
                        var i = 0;
                        foreach (var argument in conditional.TrueArguments) {
                            if (ReferenceEquals(argument, holder)) {
                                Hold(conditional.WhenTrue.Arguments[i]);
                            }

                            i++;
                        }

                        i = 0;
                        foreach (var argument in conditional.FalseArguments) {
                            if (ReferenceEquals(argument, holder)) {
                                Hold(conditional.WhenFalse.Arguments[i]);
                            }

                            i++;
                        }

                        break;
                    }

                    // Given to a call: what it gives back may be the reference again.
                    case ApplyInst apply:
                        kept ??= apply;
                        if (HoldsReference(apply.Type) && Returns(apply.Callee, [..apply.Arguments], holder, summaries)) {
                            Hold(apply);
                        }

                        break;

                    case TryApplyInst tryApply:
                        kept ??= tryApply;
                        if (tryApply.Normal.Arguments is [var result] && HoldsReference(result.Type)
                            && Returns(tryApply.Callee, [..tryApply.Arguments], holder, summaries)) {
                            Hold(result);
                        }

                        break;

                    case ReturnInst:
                        kept ??= user;
                        break;
                }
            }
        }

        return (holders, used, kept);
    }

    /// <summary>Whether a call's reference result may be the argument: its callee's summary says so, or it is not known here.</summary>
    static bool Returns(SilValue callee, List<SilValue> arguments, SilValue argument, Dictionary<string, HashSet<int>> summaries) =>
        callee is FunctionRefInst reference && summaries.TryGetValue(reference.Function, out var summary)
            ? summary.Any(i => i < arguments.Count && ReferenceEquals(arguments[i], argument))
            : arguments.Contains(argument);

    /// <summary>
    ///     The instructions that use the reference's root while it is in use:
    ///     after it is made, on a path to a use of what holds it that does not
    ///     pass where it is made again, and not themselves such a use.
    /// </summary>
    static IEnumerable<SilInstruction> Conflicts(SilFunction function, SilInstruction made, HashSet<SilInstruction> used, HashSet<SilValue> holders,
        HashSet<Anchor> roots, Dictionary<SilValue, HashSet<Anchor>> anchors) {
        // Backward: whether a use of a holder is ahead, with no remaking between.
        var liveIn = new Dictionary<SilBlock, bool>();
        bool LiveOut(SilBlock block) => block.Successors.Any(s => liveIn.GetValueOrDefault(s));
        for (var changed = true; changed;) {
            changed = false;
            foreach (var block in function.Blocks) {
                var live = LiveOut(block);
                for (var i = block.Instructions.Count - 1; i >= 0; i--) {
                    var instruction = block.Instructions[i];
                    if (ReferenceEquals(instruction, made)) {
                        live = false;
                    } else if (used.Contains(instruction)) {
                        live = true;
                    }
                }

                if (liveIn.GetValueOrDefault(block) != live) {
                    liveIn[block] = live;
                    changed = true;
                }
            }
        }

        // Forward from where it is made, to where it is made again.
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<(SilBlock Block, int Start)>();
        pending.Push((made.Block!, made.Block!.Instructions.IndexOf(made) + 1));
        while (pending.TryPop(out var next)) {
            var block = next.Block;

            // Whether a use is ahead of each instruction, within the block and after it.
            var ahead = new bool[block.Instructions.Count + 1];
            ahead[block.Instructions.Count] = LiveOut(block);
            for (var i = block.Instructions.Count - 1; i >= 0; i--) {
                var instruction = block.Instructions[i];
                ahead[i] = !ReferenceEquals(instruction, made) && (used.Contains(instruction) || ahead[i + 1]);
            }

            var through = true;
            for (var i = next.Start; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                if (ReferenceEquals(instruction, made)) {
                    through = false;
                    break;
                }

                if (!ahead[i + 1] || used.Contains(instruction) || instruction is DeallocStackInst or DestroyAddrInst) {
                    continue;
                }

                if (instruction.Operands.Any(o => !holders.Contains(o) && (anchors.GetValueOrDefault(o) ?? []).Overlaps(roots))) {
                    yield return instruction;
                }
            }

            if (!through) {
                continue;
            }

            foreach (var successor in block.Successors) {
                if (seen.Add(successor)) {
                    pending.Push((successor, 0));
                }
            }
        }
    }
}
