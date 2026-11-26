namespace Sable.SIL;

/// <summary>
///     Inlines a closure called where it is made, and takes its context
///     apart: what an <c>@autoclosure</c> argument (SYNTAX.md §5) comes to
///     once the inliner has put the callee's body in the caller.
/// </summary>
/// <remarks>
///     <para>
///         A closure whose code is <c>inline(always)</c> — an autoclosure's
///         — that the function only calls and destroys is no closure at
///         all: each call is its code, inlined with the context as its
///         argument, and each destroy the context's. So is one whose code
///         the inliner would inline there (<see cref="Inliner.InlinesClosure" />),
///         in code that is not compiled once for every type: a body given
///         to <c>withPointer</c>, or a trailing closure given to an inlined
///         function, as <c>tracer.withSpan("a") { … }</c> with nothing
///         listening. Nothing is left of the
///         <c>partial_apply</c>. A callee that never asked for the value,
///         as <c>assert</c> in a release build, leaves it only destroyed,
///         and its argument is then never worked out.
///     </para>
///     <para>
///         A closure's context that nothing but the closure's inlined code
///         reads — each field stored once, where it is made, and only
///         loaded after — is its fields: each load is the value stored, a
///         copy of it, or it borrowed, and the context's destroy is the
///         stored values'. Nothing is allocated, on the heap or the stack;
///         what the closure captured by reference, a box, may then not
///         escape either, and <see cref="PromoteToStack" /> makes it in the
///         frame.
///     </para>
/// </remarks>
public sealed class InlineClosures : ISilPass {
    public string Name => "inline-closures";

    public void Run(SilFunction function) {
        if (function.Module is not { } module || function.Blocks.Count == 0) {
            return;
        }

        for (var changed = true; changed;) {
            changed = false;
            foreach (var closure in function.Blocks.SelectMany(static b => b.Instructions).OfType<PartialApplyInst>().ToList()) {
                if (closure is { Function: FunctionRefInst code, ThickType.Signature.IsAsync: false }
                    && module.Find(code.Function) is { Blocks.Count: > 0 } callee
                    && callee != function && callee.Inline != SilInline.Never
                    && OnlyCalled(closure, new Uses(function)) is { } uses
                    && (callee.Inline == SilInline.Always || !function.IsUnspecialised && Inliner.InlinesClosure(callee, uses.Count(static u => u is ApplyInst)))) {
                    Unclose(function, closure, code, callee, uses);
                    changed = true;
                }
            }

            foreach (var context in function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocRefInst>().ToList()) {
                changed |= TakeApart(function, context);
            }
        }
    }

    /// <summary>
    ///     The calls, destroys and copies of a closure — each copy only
    ///     called and destroyed too — when that is all it is used for; null
    ///     when it is passed, stored or returned.
    /// </summary>
    static List<SilInstruction>? OnlyCalled(PartialApplyInst closure, Uses uses) {
        var found = new List<SilInstruction>();
        var pending = new Stack<SilValue>([closure]);
        while (pending.TryPop(out var value)) {
            foreach (var user in uses.Of(value)) {
                switch (user) {
                    case ApplyInst apply when ReferenceEquals(apply.Callee, value) && !apply.Arguments.Any(a => ReferenceEquals(a, value)):
                    case DestroyValueInst:
                        found.Add(user);
                        break;

                    case CopyValueInst copy:
                        found.Add(copy);
                        pending.Push(copy);
                        break;

                    default:
                        return null;
                }
            }
        }

        return found;
    }

    /// <summary>
    ///     Each call of the closure becomes its code, inlined with the
    ///     closure's context for its own; the closure's destroy is the
    ///     context's, and a copy's is nothing.
    /// </summary>
    static void Unclose(SilFunction function, PartialApplyInst closure, FunctionRefInst code, SilFunction callee, List<SilInstruction> uses) {
        var context = closure.Context;
        var calls = new List<ApplyInst>();
        foreach (var use in uses) {
            var block = use.Block;
            var at = block.Instructions.IndexOf(use);
            switch (use) {
                case ApplyInst apply: {
                    // Only a vehicle for the inliner, which reads the
                    // context off it as the code's last argument.
                    SilValue passed = context ?? Insert(block, ref at, new BuiltinInst("no_context", SilType.Object), use.Location);
                    var direct = new ApplyInst(code, apply.Type, [..apply.Arguments, passed]) { Block = block, Location = apply.Location };
                    block.Instructions[at] = direct;
                    Uses.Replace(function, apply, direct);
                    calls.Add(direct);
                    break;
                }

                case DestroyValueInst destroy when ReferenceEquals(destroy.Value, closure) && context is not null:
                    block.Instructions[at] = new DestroyValueInst(context) { Block = block, Location = destroy.Location };
                    break;

                default:
                    block.Instructions.RemoveAt(at);
                    break;
            }
        }

        closure.Block.Instructions.Remove(closure);
        foreach (var call in calls) {
            Inliner.Inline(function, call, callee);
        }
    }

    static SilValue Insert(SilBlock block, ref int at, SilInstruction instruction, SilLocation? location) {
        instruction.Block = block;
        instruction.Location = location;
        block.Instructions.Insert(at++, instruction);
        return instruction;
    }

    /// <summary>
    ///     Replaces a closure's context nothing else holds with what was
    ///     stored in it. False, and nothing changed, for any other object,
    ///     or a context used in any other way.
    /// </summary>
    static bool TakeApart(SilFunction function, AllocRefInst context) {
        if (context.ClassType is not { Deinit: null } type || !type.Name.EndsWith("$context", StringComparison.Ordinal)) {
            return false;
        }

        var uses = new Uses(function);
        var stores = new Dictionary<int, StoreInst>();
        var loads = new List<(SilInstruction Load, int Field)>();
        var destroys = new List<DestroyValueInst>();
        var dropped = new List<SilInstruction>();
        var pending = new Stack<SilValue>([context]);
        while (pending.TryPop(out var value)) {
            foreach (var user in uses.Of(value)) {
                switch (user) {
                    case UncheckedRefCastInst cast:
                        dropped.Add(cast);
                        pending.Push(cast);
                        break;

                    case RefElementAddrInst field when type.Fields[field.Field].Type is not (WeakStorageType or UnownedStorageType):
                        dropped.Add(field);
                        foreach (var access in uses.Of(field)) {
                            switch (access) {
                                case StoreInst store when ReferenceEquals(store.Address, field) && !stores.ContainsKey(field.Field):
                                    stores[field.Field] = store;
                                    break;
                                case LoadInst { Kind: LoadKind.Trivial or LoadKind.Copy } or LoadBorrowInst:
                                    loads.Add((access, field.Field));
                                    break;

                                // A field no one else can reach is never accessed
                                // in conflict: its dynamic exclusivity checks go with it.
                                case BuiltinInst { Name: "access_check_modify" or "access_check_read" }:
                                    dropped.Add(access);
                                    break;
                                default:
                                    return false;
                            }
                        }

                        break;

                    case BuiltinInst { Name: "share_into" } shared when ReferenceEquals(shared.Operands[0], value):
                        dropped.Add(shared);
                        break;

                    case DestroyValueInst destroy when ReferenceEquals(value, context):
                        destroys.Add(destroy);
                        break;

                    default:
                        return false;
                }
            }
        }

        if (destroys.Count == 0) {
            return false;
        }

        // Each load is of a value stored before it, on every path to it.
        Dictionary<SilBlock, HashSet<SilBlock>>? dominators = null;
        bool Before(SilInstruction first, SilInstruction then) =>
            first.Block == then.Block
                ? first.Block.Instructions.IndexOf(first) < then.Block.Instructions.IndexOf(then)
                : (dominators ??= SilVerifier.Dominators(function)).TryGetValue(then.Block, out var over) && over.Contains(first.Block);

        if (loads.Any(l => !stores.TryGetValue(l.Field, out var store) || !Before(store, l.Load))
            || stores.Values.Any(s => destroys.Any(d => !Before(s, d)))) {
            return false;
        }

        foreach (var (load, field) in loads) {
            var stored = stores[field].Value;
            var block = load.Block;
            switch (load) {
                case LoadInst { Kind: LoadKind.Copy }: {
                    var copy = new CopyValueInst(stored) { Block = block, Location = load.Location };
                    block.Instructions[block.Instructions.IndexOf(load)] = copy;
                    Uses.Replace(function, load, copy);
                    break;
                }

                case LoadBorrowInst:
                    foreach (var end in uses.Of(load).OfType<EndBorrowInst>()) {
                        end.Block.Instructions.Remove(end);
                    }

                    block.Instructions.Remove(load);
                    Uses.Replace(function, load, stored);
                    break;

                default:
                    block.Instructions.Remove(load);
                    Uses.Replace(function, load, stored);
                    break;
            }
        }

        // The context's destroy was its fields' release: now the values'.
        foreach (var destroy in destroys) {
            var block = destroy.Block;
            var at = block.Instructions.IndexOf(destroy);
            block.Instructions.RemoveAt(at);
            foreach (var store in stores.Values.Where(static s => !s.Value.Type.IsTrivial)) {
                block.Instructions.Insert(at++, new DestroyValueInst(store.Value) { Block = block, Location = destroy.Location });
            }
        }

        foreach (var instruction in stores.Values.Cast<SilInstruction>().Concat(dropped).Append(context)) {
            instruction.Block.Instructions.Remove(instruction);
        }

        return true;
    }
}
