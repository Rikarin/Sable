namespace Sable.SIL;

/// <summary>
///     Specialises a function that loops over a closure it is given for the
///     closure it is given (#179): a copy of the callee whose calls of that
///     closure are calls of its code, inlined, where the call passes a
///     closure with no context — a literal one that captures nothing.
/// </summary>
/// <remarks>
///     <para>
///         <c>values.reduce(0) { $0 + $1 }</c> calls <c>reduce</c>, a loop
///         too big to inline, with a closure it calls for each element: each
///         is a call through a function value. The copy calls the closure's
///         code directly and inlines it; it is called from where the closure
///         is made, and from any other call passing the same code there.
///         The copy is given the closure as before, so nothing changes of
///         what is owned: only where its calls go.
///     </para>
///     <para>
///         A closure converted to a function type that throws, or throws
///         another error, is one of the conversion thunks of
///         <see cref="SilGen.ConversionThunk" />, whose context is a box
///         holding the closure converted. Where that is a closure with no
///         context, the thunk is specialised for it first: a copy that calls
///         its code and needs no box, so that the conversion is a closure
///         with no context too.
///     </para>
///     <para>
///         A callee is copied once for each set of closures it is given, and
///         only one with a loop (another is inlined whole) costing at most
///         <see cref="Limit" />; one that is async, generic in how it is
///         called, or calls itself is left alone. A build for size makes none.
///     </para>
/// </remarks>
public sealed class SpecialiseClosures : ISilPass {
    /// <summary>The most a looping callee may cost (<see cref="Inliner.Cost" />) to be copied.</summary>
    public const int Limit = 2 * Inliner.SingleCallerThreshold;

    public string Name => "specialise-closures";

    public void Run(SilFunction function) {
        if (function.Module is { } module) {
            Run(function, module);
        }
    }

    public void Run(SilModule module) {
        foreach (var function in module.Functions.ToList()) {
            Run(function, module);
        }
    }

    static void Run(SilFunction function, SilModule module) {
        if (function.Blocks.Count == 0 || function.IsUnspecialised) {
            return;
        }

        var changed = false;
        foreach (var closure in function.Blocks.SelectMany(static b => b.Instructions).OfType<PartialApplyInst>().ToList()) {
            changed |= UnboxConversion(function, module, closure);
        }

        foreach (var call in function.Blocks.SelectMany(static b => b.Instructions).Where(static i => i is ApplyInst or TryApplyInst).ToList()) {
            changed |= Specialise(function, module, call);
        }

        if (changed) {
            new Simplify().Run(function);
        }
    }

    /// <summary>The code of a closure with no context, not async, that the module has: what a call of it can call directly.</summary>
    static SilFunction? Code(SilValue value, SilModule module) =>
        value is PartialApplyInst { Context: null, Function: FunctionRefInst code, ThickType.Signature.IsAsync: false }
        && module.Find(code.Function) is { Blocks.Count: > 0, IsUnspecialised: false } found
            ? found
            : null;

    // ---- Conversions --------------------------------------------------------

    /// <summary>
    ///     A conversion thunk's closure, whose box holds a closure with no
    ///     context and is used for nothing else, made a closure of the thunk
    ///     specialised for that code, with no context.
    /// </summary>
    static bool UnboxConversion(SilFunction function, SilModule module, PartialApplyInst closure) {
        if (closure is not { Function: FunctionRefInst thunkRef, Context: AllocRefInst box }
            || !thunkRef.Function.StartsWith("$convert$", StringComparison.Ordinal)
            || module.Find(thunkRef.Function) is not { Blocks.Count: > 0 } thunk) {
            return false;
        }

        var uses = new Uses(function);
        var held = uses.Of(box);
        if (held.Count != 2 || !held.Contains(closure) || held.OfType<RefElementAddrInst>().SingleOrDefault() is not { Field: 0 } field
            || Accesses(uses, field) is not ([StoreInst { Kind: StoreKind.Init } store], var checks) || !ReferenceEquals(store.Address, field)
            || store.Value is not PartialApplyInst inner || Code(inner, module) is not { } code || uses.Of(inner) is not [_]) {
            return false;
        }

        var name = $"{thunk.Name}$for${code.Name}";
        if (module.Find(name) is null) {
            if (Unboxed(thunk, name, inner, code) is not { } made) {
                return false;
            }

            module.Add(made);
            new Simplify().Run(made);
        }

        var specialised = module.Find(name)!;
        var block = closure.Block;
        var at = block.Instructions.IndexOf(closure);
        var reference = new FunctionRefInst(specialised.Name, specialised.Type) { Block = block, Location = closure.Location };
        var replacement = new PartialApplyInst(reference, null, closure.ThickType) { Block = block, Location = closure.Location };
        block.Instructions[at] = replacement;
        block.Instructions.Insert(at, reference);
        Uses.Replace(function, closure, replacement);
        foreach (var gone in checks.Concat([store, field, box, inner])) {
            gone.Block.Instructions.Remove(gone);
        }

        return true;
    }

    /// <summary>The thunk, copied, its call of the closure in its box a call of the closure's code.</summary>
    static SilFunction? Unboxed(SilFunction thunk, string name, PartialApplyInst inner, SilFunction code) {
        var copy = Copy(thunk, name);
        var context = copy.Entry.Arguments[^1];
        var uses = new Uses(copy);
        if (uses.Of(context) is not [UncheckedRefCastInst cast] || uses.Of(cast) is not [RefElementAddrInst field]
            || Accesses(uses, field) is not ([LoadBorrowInst callee], var checks)) {
            return null;
        }

        var calls = uses.Of(callee).Where(u => u is ApplyInst or TryApplyInst && ReferenceEquals(u.Operands[0], callee)).ToList();
        if (calls is not [var call] || uses.Of(callee).Any(u => u is not EndBorrowInst && u != call) || call.Operands.Skip(1).Contains(callee)) {
            return null;
        }

        var direct = CallDirectly(copy, call, (FunctionRefInst)inner.Function, inner.ThickType);
        foreach (var end in uses.Of(callee).OfType<EndBorrowInst>()) {
            end.Block.Instructions.Remove(end);
        }

        foreach (var gone in checks.Concat([callee, field, cast])) {
            gone.Block.Instructions.Remove(gone);
        }

        if (Inliner.WouldInline(code)) {
            Inliner.Inline(copy, direct, code);
            copy.RemoveUnreachableBlocks();
        }

        return copy;
    }

    /// <summary>
    ///     The uses of a box's field, apart from its dynamic exclusivity
    ///     checks: a field no one else can reach is never accessed in
    ///     conflict, and its checks go with it.
    /// </summary>
    static (List<SilInstruction> Uses, List<SilInstruction> Checks) Accesses(Uses uses, SilValue field) {
        var all = uses.Of(field);
        var checks = all.Where(static u => u is BuiltinInst { Name: "access_check_modify" or "access_check_read" }).ToList();
        return ([..all.Except(checks)], checks);
    }

    // ---- Looping callees ----------------------------------------------------

    /// <summary>A call of a looping function given closures with no context it calls: a call of its copy for them.</summary>
    static bool Specialise(SilFunction function, SilModule module, SilInstruction call) {
        if (call.Operands[0] is not FunctionRefInst reference || !call.Operands.Skip(1).Any(a => Code(a, module) is not null)
            || module.Find(reference.Function) is not { } callee || callee == function || !Copyable(callee)) {
            return false;
        }

        var parameters = callee.Entry.Arguments;
        var known = new List<(int Index, PartialApplyInst Closure)>();
        for (var i = 0; i < parameters.Count && i + 1 < call.Operands.Count; i++) {
            if (call.Operands[i + 1] is PartialApplyInst closure && Code(closure, module) is not null && Called(callee, parameters[i]).Count > 0) {
                known.Add((i, closure));
            }
        }

        if (known.Count == 0) {
            return false;
        }

        var name = $"{callee.Name}$closures${string.Join("$", known.Select(static k => $"{k.Index}${((FunctionRefInst)k.Closure.Function).Function}"))}";
        if (module.Find(name) is null) {
            var copy = Copy(callee, name);
            var direct = new List<(SilInstruction Call, SilFunction Code)>();
            foreach (var (index, closure) in known) {
                var code = Code(closure, module)!;
                foreach (var through in Called(copy, copy.Entry.Arguments[index])) {
                    var made = CallDirectly(copy, through, (FunctionRefInst)closure.Function, closure.ThickType);
                    if (Inliner.WouldInline(code)) {
                        direct.Add((made, code));
                    }
                }
            }

            foreach (var (made, code) in direct) {
                Inliner.Inline(copy, made, code);
            }

            // The chain of blocks each inlined call leaves, merged, as the
            // passes after look at a block at a time: an element read for
            // the closure is then read in place (BorrowLoads' kin).
            module.Add(copy);
            new Simplify().Run(copy);
        }

        var specialised = module.Find(name)!;
        var target = new FunctionRefInst(specialised.Name, specialised.Type) { Block = call.Block, Location = call.Location };
        call.Block.Instructions.Insert(call.Block.Instructions.IndexOf(call), target);
        call.SetOperand(0, target);
        return true;
    }

    /// <summary>Whether a callee is one to copy: a loop, not too big, nothing that makes it unlike any other function.</summary>
    static bool Copyable(SilFunction callee) =>
        callee.Blocks.Count > 0 && callee.Inline != SilInline.Never && !callee.Vectorised && !callee.IsUnspecialised
        && !callee.Type.IsAsync && callee.HiddenFrame is null && callee.TrampolineTarget is null
        && !callee.Blocks.SelectMany(static b => b.Instructions).Any(i => i is FunctionRefInst r && r.Function == callee.Name)
        && Inliner.Loops(callee) && Inliner.Cost(callee, Limit) <= Limit;

    /// <summary>The calls of a parameter, or of a copy of it, as the callee.</summary>
    static List<SilInstruction> Called(SilFunction function, SilValue parameter) {
        var uses = new Uses(function);
        var calls = new List<SilInstruction>();
        var pending = new Stack<SilValue>([parameter]);
        var seen = new HashSet<SilValue>();
        while (pending.TryPop(out var value)) {
            if (!seen.Add(value)) {
                continue;
            }

            foreach (var user in uses.Of(value)) {
                switch (user) {
                    case ApplyInst or TryApplyInst when ReferenceEquals(user.Operands[0], value) && !user.Operands.Skip(1).Contains(value):
                        calls.Add(user);
                        break;
                    case CopyValueInst copy:
                        pending.Push(copy);
                        break;
                }
            }
        }

        return calls;
    }

    /// <summary>A call through a closure made a call of its code, with no context, in its place.</summary>
    static SilInstruction CallDirectly(SilFunction function, SilInstruction call, FunctionRefInst code, ThickFunctionType type) {
        var block = call.Block;
        var at = block.Instructions.IndexOf(call);
        var target = new FunctionRefInst(code.Function, type.Implementation) { Block = block, Location = call.Location };
        var none = new BuiltinInst("no_context", SilType.Object, []) { Block = block, Location = call.Location };
        SilValue[] arguments = [..call.Operands.Skip(1), none];
        SilInstruction direct = call switch {
            ApplyInst apply => new ApplyInst(target, apply.Type, arguments),
            TryApplyInst tryApply => new TryApplyInst(target, tryApply.ResultType, arguments, tryApply.Normal, tryApply.Error),
            _ => throw new InvalidOperationException(),
        };

        direct.Block = block;
        direct.Location = call.Location;
        direct.InlinedSite = call.InlinedSite;
        block.Instructions[at] = direct;
        block.Instructions.Insert(at, none);
        block.Instructions.Insert(at, target);
        Uses.Replace(function, call, direct);
        return direct;
    }

    /// <summary>A copy of a function, under another name: its blocks, instructions and what a debugger is told of it.</summary>
    internal static SilFunction Copy(SilFunction original, string name) {
        var copy = new SilFunction(name, original.Type) {
            DisplayName = original.DisplayName,
            Inline = original.Inline,
            SourceFile = original.SourceFile,
            Line = original.Line,
            ResultRoots = original.ResultRoots,
        };
        copy.ParameterNames.AddRange(original.ParameterNames);
        copy.Scopes.AddRange(original.Scopes);

        var blocks = new Dictionary<SilBlock, SilBlock>();
        var values = new Dictionary<SilValue, SilValue>();
        foreach (var block in original.Blocks) {
            var into = copy.AddBlock();
            blocks[block] = into;
            foreach (var argument in block.Arguments) {
                values[argument] = into.AddArgument(argument.Type, argument.IsGuaranteed);
            }
        }

        var copied = new List<SilInstruction>();
        foreach (var block in original.Blocks) {
            foreach (var instruction in block.Instructions) {
                var made = instruction.Clone(static v => v, b => blocks[b]);
                made.InlinedSite = instruction.InlinedSite;
                values[instruction] = made;
                blocks[block].Append(made);
                copied.Add(made);
            }
        }

        foreach (var instruction in copied) {
            for (var i = 0; i < instruction.Operands.Count; i++) {
                if (values.TryGetValue(instruction.Operands[i], out var replacement)) {
                    instruction.SetOperand(i, replacement);
                }
            }
        }

        return copy;
    }
}
