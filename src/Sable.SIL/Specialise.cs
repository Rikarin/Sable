using System.Collections.Immutable;

namespace Sable.SIL;

/// <summary>
///     Specialises unspecialised generic code where its types are known
///     (#297 stage 5, docs/06 §6.2): a call of a <c>$generic</c> function
///     whose metadata and witness tables are constants calls a copy of it
///     made for those types instead, in which nothing is opaque.
/// </summary>
/// <remarks>
///     <para>
///         The copy (<see cref="Specialiser" />) is the generic body with each
///         <c>@opaque(T)</c> — and each array, optional, tuple and generic
///         type of one — replaced by the type <c>T</c> is: the views
///         between the two (<c>opaque_cast</c>, <c>opaque_borrow_as</c>,
///         <c>opaque_take_as</c>) are the value itself; a borrowed value's
///         storage (<c>opaque_address</c>) read is the value; the metadata
///         parameters are constants; a witness table's requirement
///         (<c>witness_generic:i</c>) is the conformance's witness, called
///         directly and itself specialised for its <c>Self</c>; associated
///         types are the types the conformances bind; a value witness is
///         the type's own copy, move or destroy; a value written out or
///         boxed as an error, the describer's or eraser's copy for the type;
///         and a result written where the caller said is returned. A closure
///         the caller reabstracted to the generic convention is given with
///         its reabstraction copied for the types, and a slot the caller
///         copied a value into for the call is the value, borrowed. What is
///         left is code as the
///         specialised lowering writes it, which the inliner and the passes
///         after it treat as any other.
///     </para>
///     <para>
///         The calls of the copy are specialised as it is made, so a chain of
///         generic calls becomes a chain of specialised ones. A function
///         that cannot be copied — it uses something the copy cannot
///         express for its types — stays generic, and its callers call it as
///         before; so do the calls of a generic function whose types are
///         known only at run time.
///     </para>
///     <para>
///         The cost: one copy for each set of types a function is called
///         with, at most <see cref="MaxCopies" />. An optimised build makes
///         every one, as Swift's does; a build for size (<c>-Os</c>) makes
///         only those of functions small enough to inline
///         (<see cref="Inliner.SingleCallerThreshold" />), or called in a loop,
///         where the hidden parameters and the indirection cost the most.
///     </para>
///     <para>
///         <c>SABLE_SPECIALISE=0</c> turns the pass off, to compare the
///         unspecialised code with and without it.
///     </para>
/// </remarks>
public sealed class Specialise(bool forSize = false) : ISilPass {
    /// <summary>The most copies made of one generic function.</summary>
    public const int MaxCopies = 64;

    public string Name => "specialise";

    /// <summary>Whether the pass runs: unless <c>SABLE_SPECIALISE</c> is <c>0</c>, <c>off</c> or <c>false</c>.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("SABLE_SPECIALISE") is not ("0" or "off" or "false" or "no");

    public void Run(SilFunction function) {
        if (function.Module is { } module) {
            new Specialiser(module, forSize).Rewrite(function);
        }
    }

    public void Run(SilModule module) {
        if (!Enabled || !module.Functions.Exists(static f => f.IsUnspecialised)) {
            return;
        }

        var specialiser = new Specialiser(module, forSize);
        foreach (var function in module.Functions.Where(static f => !f.IsUnspecialised && f.Blocks.Count > 0).ToList()) {
            specialiser.Rewrite(function);
        }

        // What no longer has a caller goes: the generic functions every
        // call of which was specialised. Only those, and only from a whole
        // program, whose callers are all here.
        // One a function that stays still names — a witness of a type
        // nothing makes any more, which the passes after drop — stays too.
        if (module.Unit is null && SilPassManager.Reachable(module) is { } reached) {
            // Whatever is kept keeps what it names: the pass leaves every
            // other function, a witness thunk the walk did not reach among
            // them, and such a thunk may call a generic function.
            var kept = new HashSet<string>(module.Functions.Where(f => !f.IsUnspecialised || reached.Contains(f.Name)).Select(static f => f.Name), StringComparer.Ordinal);
            var pending = new Stack<string>(kept);
            while (pending.TryPop(out var name)) {
                if (module.Find(name) is not { } function) {
                    continue;
                }

                foreach (var reference in function.Blocks.SelectMany(static b => b.Instructions).OfType<FunctionRefInst>()) {
                    if (kept.Add(reference.Function)) {
                        pending.Push(reference.Function);
                    }
                }
            }

            module.RemoveAll(f => !kept.Contains(f.Name));
        }
    }
}

/// <summary>Makes and keeps the specialised copies of a module's generic functions (<see cref="Specialise" />).</summary>
sealed class Specialiser(SilModule module, bool forSize) {
    /// <summary>Each copy by its generic function and types; null for one that could not be made.</summary>
    readonly Dictionary<string, SilFunction?> _copies = new(StringComparer.Ordinal);

    readonly Dictionary<string, int> _copiesOf = new(StringComparer.Ordinal);

    /// <summary>The copies made, in order: what is taken back with one that fails.</summary>
    readonly List<(string Key, SilFunction Copy, string Generic)> _made = [];

    internal SilModule Module => module;

    /// <summary>What a parameter of a generic function is.</summary>
    internal enum Role {
        Value,

        /// <summary>The address its result is written to, which the copy returns instead.</summary>
        Out,

        Metadata,
        Table,
    }

    /// <summary>A call of a generic function, with its types: the copy it calls, by <see cref="Key" />.</summary>
    internal sealed record Plan(SilFunction Generic, Role[] Roles, Dictionary<string, SilType> Types, List<SilWitnessTable> Tables, string Key) {
        /// <summary>
        ///     The closure parameters the copy takes as the caller made them,
        ///     before their reabstraction to the generic convention — a result
        ///     returned rather than written where the caller says — by index.
        /// </summary>
        public Dictionary<int, ThickFunctionType> Natural { get; init; } = [];
    }

    /// <summary>Each parameter's role, or null for a function the copy cannot be made of.</summary>
    internal static Role[]? Roles(SilFunction function) {
        var parameters = function.Type.Parameters;
        if (function.Blocks.Count == 0 || function.Entry.Arguments.Count != parameters.Length) {
            return null;
        }

        var roles = new Role[parameters.Length];
        for (var i = 0; i < parameters.Length; i++) {
            var name = i < function.ParameterNames.Count ? function.ParameterNames[i] : "";
            if (name.StartsWith("$metadata.", StringComparison.Ordinal)) {
                roles[i] = Role.Metadata;
            } else if (name.StartsWith("$witnesses.", StringComparison.Ordinal)) {
                roles[i] = Role.Table;
            } else if (name == "$out" && function.Type is { Result: var result, Thrown: null } && result == SilType.Unit && parameters[i] is AddressType) {
                roles[i] = Role.Out;
            } else {
                roles[i] = Role.Value;
            }
        }

        return roles;
    }

    /// <summary>What a call of the generic function with these arguments specialises it for; null when its types are not known here.</summary>
    internal Plan? PlanCall(SilFunction generic, IReadOnlyList<SilValue> arguments) {
        if (Roles(generic) is not { } roles || roles.Length != arguments.Count) {
            return null;
        }

        var types = new Dictionary<string, SilType>(StringComparer.Ordinal);
        var tables = new List<SilWitnessTable>();
        var key = new System.Text.StringBuilder(generic.Name).Append('<');
        for (var i = 0; i < roles.Length; i++) {
            switch (roles[i]) {
                case Role.Metadata:
                    if (KnownMetadata(arguments[i]) is not { } type) {
                        return null;
                    }

                    types[generic.ParameterNames[i]["$metadata.".Length..]] = type;
                    key.Append(type).Append(',');
                    break;
                case Role.Table:
                    if (KnownTable(arguments[i]) is not { } table) {
                        return null;
                    }

                    tables.Add(table);
                    key.Append(table.Conformer).Append(':').Append(table.Protocol).Append(',');
                    break;
            }
        }

        return new(generic, roles, types, tables, key.Append('>').ToString());
    }

    /// <summary>The type a metadata value is known to be, here: a constant, or one made from constants.</summary>
    internal SilType? KnownMetadata(SilValue value) =>
        value switch {
            MetadataInst { Described: var described } when !SilType.MentionsOpaque(described) => described,
            BuiltinInst { Name: "metadata_array", Operands: [var element] } => KnownMetadata(element) is { } known ? new ArrayType(known) : null,
            BuiltinInst { Name: var name, Operands: [var table] } when name.StartsWith("witness_associated:", StringComparison.Ordinal)
                => KnownTable(table) is { } known && int.Parse(name["witness_associated:".Length..], System.Globalization.CultureInfo.InvariantCulture) is var index
                                                  && index < known.AssociatedTypes.Count
                    ? known.AssociatedTypes[index].Type
                    : null,
            _ => null,
        };

    /// <summary>The witness table a value is known to be, here: the table of a known type's conformance this module made.</summary>
    internal SilWitnessTable? KnownTable(SilValue value) {
        if (value is not BuiltinInst { Name: var name, Operands: [var operand] }) {
            return null;
        }

        if (name.StartsWith("witness_table_known:", StringComparison.Ordinal) || name.StartsWith("witness_table:", StringComparison.Ordinal)) {
            return KnownMetadata(operand) is { } type ? module.FindWitnessTable(type, name[(name.IndexOf(':') + 1)..]) : null;
        }

        if (name.StartsWith("witness_base:", StringComparison.Ordinal) && KnownTable(operand) is { } refining) {
            var index = int.Parse(name["witness_base:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return index < refining.Bases.Count ? module.FindWitnessTable(refining.Conformer, refining.Bases[index]) : null;
        }

        return null;
    }

    /// <summary>The copy a call specialises its callee to; null when it is not made (refused, too many, or not worth it in a build for size).</summary>
    internal SilFunction? CopyFor(Plan plan, bool hot) {
        if (_copies.TryGetValue(plan.Key, out var made)) {
            return made;
        }

        if (_copiesOf.GetValueOrDefault(plan.Generic.Name) >= Specialise.MaxCopies
            || forSize && !hot && Inliner.Cost(plan.Generic, Inliner.SingleCallerThreshold) > Inliner.SingleCallerThreshold) {
            return null;
        }

        var start = _made.Count;
        var cloner = new SpecialisedCopy(this, plan);
        if (cloner.Shell() is not { } copy) {
            _copies[plan.Key] = null;
            return null;
        }

        _copies[plan.Key] = copy;
        _made.Add((plan.Key, copy, plan.Generic.Name));
        _copiesOf[plan.Generic.Name] = _copiesOf.GetValueOrDefault(plan.Generic.Name) + 1;
        module.Add(copy);
        if (!cloner.Fill()) {
            // Taken back with every copy made for it, which may call it.
            foreach (var (key, taken, generic) in _made.Skip(start)) {
                _copies.Remove(key);
                module.RemoveAll(f => ReferenceEquals(f, taken));
                _copiesOf[generic]--;
            }

            _made.RemoveRange(start, _made.Count - start);
            _copies[plan.Key] = null;
            return null;
        }

        return copy;
    }

    /// <summary>A name for a new function, not yet taken.</summary>
    internal string FreshName(string name) {
        if (module.Find(name) is null) {
            return name;
        }

        for (var i = 2;; i++) {
            if (module.Find($"{name}${i}") is null) {
                return $"{name}${i}";
            }
        }
    }

    /// <summary>
    ///     A copy of a function that is not generic but takes values of
    ///     opaque types — a closure's reabstraction to the generic convention,
    ///     made where the types are known — for the closure type the copy
    ///     that is given it takes: its opaque types the types they are there.
    /// </summary>
    internal SilFunction? ThunkFor(string name, ThickFunctionType wanted) {
        if (module.Find(name) is not { IsUnspecialised: false } thunk || Roles(thunk) is not { } roles || roles.Any(static r => r != Role.Value)) {
            return null;
        }

        var types = new Dictionary<string, SilType>(StringComparer.Ordinal);
        if (!Unify(thunk.Type, wanted.Implementation, types)) {
            return null;
        }

        var key = $"{name}<{string.Join(",", types.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => $"{p.Key}={p.Value}"))}>";
        var copy = CopyFor(new(thunk, roles, types, [], key), hot: true);
        return copy?.Type.ToString() == wanted.Implementation.ToString() ? copy : null;
    }

    /// <summary>Binds each opaque type in a type to what is in its place in another; false where the two differ otherwise.</summary>
    static bool Unify(SilType generic, SilType known, Dictionary<string, SilType> types) {
        if (!SilType.MentionsOpaque(generic)) {
            return generic == known;
        }

        switch (generic, known) {
            case (OpaqueType opaque, _):
                if (types.TryGetValue(opaque.Name, out var bound)) {
                    return bound == known;
                }

                types[opaque.Name] = known;
                return !SilType.MentionsOpaque(known);
            case (ArrayType a, ArrayType b):
                return Unify(a.Element, b.Element, types);
            case (SliceType a, SliceType b):
                return Unify(a.Element, b.Element, types);
            case (AddressType a, AddressType b):
                return Unify(a.Pointee, b.Pointee, types);
            case (TupleType a, TupleType b):
                return a.Elements.Length == b.Elements.Length && a.Elements.Zip(b.Elements).All(p => Unify(p.First, p.Second, types));
            case (ThickFunctionType a, ThickFunctionType b):
                return Unify(a.Signature, b.Signature, types);
            case (FunctionType a, FunctionType b):
                return a.Parameters.Length == b.Parameters.Length && a.IsAsync == b.IsAsync && a.Owned.SequenceEqual(b.Owned)
                       && a.Parameters.Zip(b.Parameters).All(p => Unify(p.First, p.Second, types)) && Unify(a.Result, b.Result, types)
                       && (a.Thrown is null ? b.Thrown is null : b.Thrown is not null && Unify(a.Thrown, b.Thrown, types));
            default:
                // A generic type of opaque types, by name: the copy checks the name it has once they are bound.
                return generic is NominalType && known is NominalType;
        }
    }

    // ---- Calls from code that knows the types --------------------------------

    /// <summary>Makes each call of a generic function in the function whose types it knows a call of the copy for them.</summary>
    public void Rewrite(SilFunction function) {
        var changed = false;
        foreach (var block in function.Blocks.ToList()) {
            foreach (var call in block.Instructions.Where(static i => i is ApplyInst or TryApplyInst).ToList()) {
                if (call.Operands[0] is not FunctionRefInst reference || module.Find(reference.Function) is not { IsUnspecialised: true } generic
                    || PlanCall(generic, [..call.Operands.Skip(1)]) is not { } plan) {
                    continue;
                }

                if (call is TryApplyInst && plan.Roles.Contains(Role.Out)) {
                    continue;
                }

                var hot = InLoop(call.Block);
                var natural = Naturals(function, call, plan);
                var reabstracted = natural.Keys.Select(i => (PartialApplyInst)call.Operands[i + 1]).ToList();
                if (natural.Count > 0 && CopyFor(plan with { Key = $"{plan.Key}|{string.Join(",", natural.Select(static n => $"{n.Key}={n.Value}"))}", Natural = natural }, hot) is { } direct
                    && CallCopy(function, call, plan with { Natural = natural }, direct)) {
                    foreach (var closure in reabstracted) {
                        DropReabstraction(function, closure);
                    }

                    changed = true;
                    continue;
                }

                if (CopyFor(plan, hot) is not { } copy || !CallCopy(function, call, plan, copy)) {
                    continue;
                }

                changed = true;
            }
        }

        if (changed) {
            RemoveDeadViews(function);
            ForwardCopiedSlots(function);
        }
    }

    /// <summary>
    ///     A slot a call of generic code was given a copy of a value in —
    ///     <c>store (copy_value x) to [init] s</c>, or a <c>load [copy]</c> of
    ///     a local's slot stored there — borrowed from within the block, then
    ///     destroyed, which the copy it now calls borrows as it is: each
    ///     borrow from the slot is a borrow of the value, which outlives them,
    ///     or of the local, which nothing changes meanwhile; and the copy and
    ///     the slot go.
    /// </summary>
    internal static void ForwardCopiedSlots(SilFunction function) {
        ForwardUnmanagedSlots(function);
        var uses = new Uses(function);
        foreach (var block in function.Blocks) {
            foreach (var slot in block.Instructions.OfType<AllocStackInst>().Where(static s => !s.Unmanaged && !s.ElementType.IsTrivial).ToList()) {
                var users = uses.Of(slot);
                if (users.Count(static u => u is StoreInst) != 1 || users.First(static u => u is StoreInst) is not StoreInst { Kind: StoreKind.Init } store
                    || !ReferenceEquals(store.Address, slot) || store.Value is not SilInstruction || uses.Of(store.Value).Count != 1
                    || users.Any(static u => u is not (StoreInst or LoadBorrowInst or DestroyAddrInst or DeallocStackInst))) {
                    continue;
                }

                var at = store.Block;
                var borrows = users.OfType<LoadBorrowInst>().ToList();
                var ends = borrows.SelectMany(b => uses.Of(b)).OfType<EndBorrowInst>().ToList<SilInstruction>();
                int Position(SilInstruction i) => at.Instructions.IndexOf(i);
                if (borrows.Count == 0 || ends.Count == 0 || borrows.Any(b => b.Block != at || Position(b) < Position(store)) || ends.Any(e => e.Block != at)
                    || borrows.SelectMany(b => uses.Of(b)).Any(u => u.Block != at || Position(u) > ends.Max(Position))) {
                    continue;
                }

                var last = ends.Max(Position);
                var copied = (SilInstruction)store.Value;
                SilValue value;
                var keep = false;
                if (copied is CopyValueInst { Value: var original }) {
                    // The value must be borrowed for longer: a parameter, borrowed for the whole call,
                    // a borrow in this block ended after them, or a copy destroyed here after them and taken by nothing else.
                    var outlives = original switch {
                        SilArgument argument => argument.Block == function.Entry,
                        LoadBorrowInst outer => uses.Of(outer).OfType<EndBorrowInst>().ToList() is { Count: > 0 } outerEnds && outerEnds.All(e => e.Block == at && Position(e) > last),
                        LoadInst { Kind: LoadKind.Copy } owned => uses.Of(owned).Where(u => Uses.Consumes(u, owned)).ToList() is [DestroyValueInst destroy] && destroy.Block == at && Position(destroy) > last,
                        _ => false,
                    };
                    if (!outlives) {
                        continue;
                    }

                    value = original;
                } else if (copied is not LoadInst { Kind: LoadKind.Copy, Address: AllocStackInst }) {
                    // A value made to be stored there, destroyed with the slot in this block: the value, destroyed there.
                    var destroys = users.OfType<DestroyAddrInst>().Where(d => d.Block == at).ToList();
                    if (copied.Block != at || destroys is not [var destroy] || Position(destroy) < last) {
                        continue;
                    }

                    at.Instructions.Insert(Position(destroy), new DestroyValueInst(copied) { Block = at, Location = destroy.Location });
                    value = copied;
                    keep = true;
                } else {
                    // A local's slot nothing names from the copy until the last borrow ends: borrowed there instead.
                    var local = ((LoadInst)copied).Address;
                    if (copied.Block != at || at.Instructions.Skip(Position(copied) + 1).Take(last - Position(copied)).Any(i => i.Operands.Contains(local))) {
                        continue;
                    }

                    var borrowed = new LoadBorrowInst(local) { Block = at, Location = copied.Location };
                    at.Instructions.Insert(Position(copied), borrowed);
                    at.Instructions.Insert(last + 2, new EndBorrowInst(borrowed) { Block = at, Location = at.Instructions[last + 1].Location });
                    value = borrowed;
                }

                foreach (var borrow in borrows) {
                    Uses.Replace(function, borrow, value);
                }

                var gone = new HashSet<SilInstruction>([..borrows, ..ends, store, slot, ..users.Where(static u => u is DestroyAddrInst or DeallocStackInst)]);
                if (!keep) {
                    gone.Add(copied);
                }

                foreach (var each in function.Blocks) {
                    each.Instructions.RemoveAll(gone.Contains);
                }

                uses = new Uses(function);
            }
        }
    }

    /// <summary>
    ///     A slot that holds, unmanaged, the one value stored in it — a
    ///     witness's <c>self</c>, which its caller keeps alive — and is only
    ///     borrowed from: each borrow is the value, and the slot goes.
    /// </summary>
    static void ForwardUnmanagedSlots(SilFunction function) {
        var uses = new Uses(function);
        var gone = new HashSet<SilInstruction>();
        foreach (var slot in function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>().Where(static s => s.Unmanaged).ToList()) {
            var users = uses.Of(slot);
            if (users.OfType<StoreInst>().ToList() is not [{ Kind: StoreKind.Init } store] || !ReferenceEquals(store.Address, slot)
                || users.Any(static u => u is not (StoreInst or LoadBorrowInst or DeallocStackInst))) {
                continue;
            }

            // The value is the one stored where the slot is made, before anything borrows it.
            var borrows = users.OfType<LoadBorrowInst>().ToList();
            if (store.Block != slot.Block || borrows.Any(b => b.Block == store.Block && b.Block.Instructions.IndexOf(b) < b.Block.Instructions.IndexOf(store))
                || !(store.Value is SilArgument { Block: var given } && given == function.Entry || store.Value is SilInstruction { Block: var defined } && defined == function.Entry)
                   && borrows.Any(b => b.Block != store.Block)) {
                continue;
            }

            foreach (var borrow in borrows) {
                gone.UnionWith(uses.Of(borrow).OfType<EndBorrowInst>());
                Uses.Replace(function, borrow, store.Value);
                gone.Add(borrow);
            }

            gone.UnionWith([store, slot, ..users.OfType<DeallocStackInst>()]);
        }

        if (gone.Count > 0) {
            foreach (var block in function.Blocks) {
                block.Instructions.RemoveAll(gone.Contains);
            }
        }
    }

    /// <summary>
    ///     The closure arguments of a call that the caller reabstracted to the
    ///     generic convention, writing their result where they are told, from
    ///     closures that return it (<c>$reabstract$</c>, its context's
    ///     <c>function</c> a copy of the closure): by index, the closure's own type.
    /// </summary>
    static Dictionary<int, ThickFunctionType> Naturals(SilFunction function, SilInstruction call, Plan plan) {
        var natural = new Dictionary<int, ThickFunctionType>();
        for (var i = 0; i < plan.Roles.Length; i++) {
            if (plan.Roles[i] == Role.Value && call.Operands[i + 1] is PartialApplyInst { Context: AllocRefInst context } closure
                && closure.ThickType.Signature is { Result: var result, Parameters: [.., AddressType { Pointee: var written }] } generic && result == SilType.Unit && SilType.IsIndirect(written)
                && Reabstracted(function, context) is { Type: ThickFunctionType { Signature: var own } original }
                && own.Result != SilType.Unit && own.Parameters.Length == generic.Parameters.Length - 1) {
                natural[i] = original;
            }
        }

        return natural;
    }

    /// <summary>
    ///     A reabstraction nothing calls any more, only destroys: gone, with
    ///     its context and the copy of the closure it held, so that what
    ///     calls the closure sees the closure itself.
    /// </summary>
    static void DropReabstraction(SilFunction function, PartialApplyInst closure) {
        var all = function.Blocks.SelectMany(static b => b.Instructions).ToList();
        List<SilInstruction> UsersOf(SilValue value) => [..all.Where(i => i.Operands.Any(o => ReferenceEquals(o, value)))];
        if (closure.Context is not AllocRefInst context || UsersOf(closure).Any(static u => u is not DestroyValueInst)) {
            return;
        }

        var contextUsers = UsersOf(context);
        var field = contextUsers.OfType<RefElementAddrInst>().ToList();
        if (field is not [var address] || contextUsers.Any(u => u != closure && u != address && u is not BuiltinInst { Name: "share_into" })
            || UsersOf(address).Where(static u => u is not BuiltinInst { Name: var marker } || !SharedStorage.IsMarker(marker)).ToList() is not [StoreInst { Value: SilInstruction held } store]
            || UsersOf(held).Any(u => u != store && u is not BuiltinInst { Name: "share_into" } && held is CopyValueInst)) {
            return;
        }

        // The closure the context held: a copy of it goes; one moved in is destroyed where it was.
        var gone = new HashSet<SilInstruction>([closure, context, address, store, ..UsersOf(closure), ..contextUsers.Where(static u => u is BuiltinInst), ..UsersOf(address).Where(static u => u is BuiltinInst)]);
        if (held is CopyValueInst) {
            gone.Add(held);
        } else {
            store.Block.Instructions.Insert(store.Block.Instructions.IndexOf(store), new DestroyValueInst(held) { Block = store.Block, Location = store.Location });
        }

        foreach (var block in function.Blocks) {
            block.Instructions.RemoveAll(gone.Contains);
        }
    }

    /// <summary>The closure a reabstraction's context was given a copy of, or null.</summary>
    static SilValue? Reabstracted(SilFunction function, AllocRefInst context) =>
        context.ClassType.Fields is [("function", ThickFunctionType)]
            ? function.Blocks.SelectMany(static b => b.Instructions).OfType<StoreInst>()
                .FirstOrDefault(s => s.Address is RefElementAddrInst { Field: 0 } field && ReferenceEquals(field.Reference, context))?.Value switch {
                    CopyValueInst { Value: var original } => original,
                    var moved => moved,
                }
            : null;

    static bool InLoop(SilBlock block) {
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>(block.Successors);
        while (pending.TryPop(out var next)) {
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
    ///     Replaces a call of a generic function with one of its copy: each
    ///     argument passed as the type it is, the metadata and tables left
    ///     out, and a result the copy returns stored where the call was to
    ///     write it. False, having changed nothing, when an argument cannot
    ///     be seen as its type here.
    /// </summary>
    bool CallCopy(SilFunction function, SilInstruction call, Plan plan, SilFunction copy) {
        var arguments = call.Operands.Skip(1).ToList();
        var parameters = copy.Type.Parameters;
        var conversions = new List<Conversion>();
        SilValue? outAddress = null;
        var p = 0;
        for (var i = 0; i < arguments.Count; i++) {
            switch (plan.Roles[i]) {
                case Role.Value when plan.Natural.ContainsKey(i):
                    conversions.Add(NaturalClosure(function, (PartialApplyInst)arguments[i]));
                    p++;
                    break;
                case Role.Value:
                    if (Convert(function, arguments[i], parameters[p], copy.Type.IsOwned(p), call) is not { } conversion) {
                        return false;
                    }

                    conversions.Add(conversion);
                    p++;
                    break;
                case Role.Out:
                    if (Convert(function, arguments[i], SilType.Address(copy.Type.Result), false, call) is not { } address) {
                        return false;
                    }

                    conversions.Add(address);
                    outAddress = arguments[i];
                    break;
            }
        }

        // A result of a type the call's users see opaque — an array of a type parameter's values — is seen so again.
        var resultType = call switch {
            ApplyInst apply => apply.Type,
            TryApplyInst tryApply => tryApply.ResultType,
            _ => SilType.Unit,
        };
        if (call is TryApplyInst { Error.Arguments: [var thrownArgument] } && copy.Type.Thrown is { } copyThrown && thrownArgument.Type != copyThrown
            && !(thrownArgument.Type is AddressType && copyThrown is AddressType && SilType.SameErasingOpaque(thrownArgument.Type, copyThrown, opaqueOnly: true))) {
            return false;
        }

        if (outAddress is null && resultType != copy.Type.Result) {
            if (call is TryApplyInst { Normal.Arguments.Count: not 1 } || !SilType.SameErasingOpaque(copy.Type.Result, resultType, opaqueOnly: true)) {
                return false;
            }
        }

        var block = call.Block;
        var reference = Insert(block, call, new FunctionRefInst(copy.Name, copy.Type) { Location = call.Location });
        var converted = new List<SilValue>();
        SilValue? outConverted = null;
        var c = 0;
        for (var i = 0; i < arguments.Count; i++) {
            switch (plan.Roles[i]) {
                case Role.Value:
                    converted.Add(conversions[c++].Emit(function));
                    break;
                case Role.Out:
                    outConverted = conversions[c++].Emit(function);
                    break;
            }
        }

        var at = block.Instructions.IndexOf(call);
        SilInstruction replacement = call switch {
            TryApplyInst tryApply => new TryApplyInst(reference, copy.Type.Result, [..converted],
                resultType == copy.Type.Result ? tryApply.Normal : Returned(function, tryApply.Normal, copy.Type.Result), Thrown(function, tryApply.Error, copy.Type.Thrown)),
            _ => new ApplyInst(reference, copy.Type.Result, [..converted]),
        };
        replacement.Location = call.Location;
        replacement.InlinedSite = call.InlinedSite;
        replacement.Block = block;
        block.Instructions[at] = replacement;

        var after = at + 1;
        if (outConverted is not null) {
            block.Instructions.Insert(after++, new StoreInst(replacement, outConverted, StoreKind.Init) { Block = block, Location = call.Location });
        } else if (call is ApplyInst && call.HasResult) {
            SilValue result = replacement;
            if (resultType != copy.Type.Result) {
                result = new BuiltinInst("opaque_take_as", resultType, replacement) { Block = block, Location = call.Location };
                block.Instructions.Insert(after++, (SilInstruction)result);
            }

            Uses.Replace(function, call, result);
        }

        foreach (var conversion in conversions) {
            conversion.After(block, ref after);
        }

        return true;
    }

    /// <summary>
    ///     Where a throwing call that now returns the type it is goes: its
    ///     normal block, taking that type — what only viewed the result as
    ///     the type it is now the result — or else a block that views it as
    ///     the call's users did and goes on there.
    /// </summary>
    static SilBlock Returned(SilFunction function, SilBlock normal, SilType type) {
        var argument = normal.Arguments[0];
        var users = function.Blocks.SelectMany(static b => b.Instructions).Where(i => i.Operands.Any(o => ReferenceEquals(o, argument))).ToList();
        var predecessors = function.Blocks.Count(b => b.Successors.Contains(normal));
        if (predecessors == 1 && users.Count > 0 && users.All(u => u is BuiltinInst { Name: "opaque_take_as" } view && view.Type == type)) {
            var retyped = new SilArgument(normal, type);
            normal.Arguments[0] = retyped;
            foreach (var view in users) {
                Uses.Replace(function, view, retyped);
                view.Block.Instructions.Remove(view);
            }

            return normal;
        }

        var relay = function.AddBlock();
        var result = relay.AddArgument(type);
        var seen = relay.Append(new BuiltinInst("opaque_take_as", argument.Type, result));
        relay.Append(new BranchInst(normal, seen));
        return relay;
    }

    /// <summary>
    ///     Where a throwing call whose error the copy throws as the type it is
    ///     goes: its error block, or, for an error written where the caller
    ///     said, a block that views that address as the call's users did.
    /// </summary>
    static SilBlock Thrown(SilFunction function, SilBlock error, SilType? thrown) {
        if (thrown is null || error.Arguments is not [var argument] || argument.Type == thrown) {
            return error;
        }

        var relay = function.AddBlock();
        var address = relay.AddArgument(thrown);
        relay.Append(new BranchInst(error, relay.Append(new BuiltinInst("opaque_cast", argument.Type, address))));
        return relay;
    }

    static T Insert<T>(SilBlock block, SilInstruction before, T instruction) where T : SilInstruction {
        instruction.Block = block;
        block.Instructions.Insert(block.Instructions.IndexOf(before), instruction);
        return instruction;
    }

    /// <summary>How an argument is passed as the type the copy takes: made before the call, and ended after it.</summary>
    sealed class Conversion(Func<SilFunction, SilValue> emit) {
        public List<SilInstruction> Ends { get; } = [];
        public SilValue Emit(SilFunction function) => emit(function);

        public void After(SilBlock block, ref int at) {
            foreach (var end in Ends) {
                end.Block = block;
                block.Instructions.Insert(at++, end);
            }
        }
    }

    /// <summary>
    ///     A reabstracted closure passed as the closure it reabstracts: a copy
    ///     of that, made where the reabstraction's context was given one, and
    ///     destroyed wherever the reabstraction is.
    /// </summary>
    static Conversion NaturalClosure(SilFunction function, PartialApplyInst closure) =>
        new(_ => {
            var given = function.Blocks.SelectMany(static b => b.Instructions).OfType<StoreInst>()
                .First(s => s.Address is RefElementAddrInst { Field: 0 } field && ReferenceEquals(field.Reference, closure.Context));
            // A copy of the closure, made where the context was given it (or its own copy of it).
            var copy = given.Value is CopyValueInst { Value: var original }
                ? InsertAfter(given.Value as SilInstruction ?? given, new CopyValueInst(original) { Location = closure.Location })
                : Insert(given.Block, given, new CopyValueInst(given.Value) { Location = closure.Location });
            foreach (var destroy in function.Blocks.SelectMany(static b => b.Instructions).OfType<DestroyValueInst>().Where(d => ReferenceEquals(d.Value, closure)).ToList()) {
                InsertAfter(destroy, new DestroyValueInst(copy) { Location = destroy.Location });
            }

            return copy;
        });

    /// <summary>
    ///     How a caller's argument is seen as the type the copy takes: as it
    ///     is; the storage a view of it was made from; or a view made at the
    ///     call. Null when it cannot be.
    /// </summary>
    Conversion? Convert(SilFunction function, SilValue argument, SilType target, bool owned, SilInstruction call) {
        if (argument.Type == target) {
            return new(_ => argument);
        }

        // An address seen as a type parameter's: the address it was.
        if (target is AddressType) {
            if (argument is BuiltinInst { Name: "opaque_cast", Operands: [var original] } && original.Type == target) {
                return new(_ => original);
            }

            return argument.Type is AddressType from && SilType.SameErasingOpaque(from, target, opaqueOnly: true)
                ? new(_ => Insert(call.Block, call, new BuiltinInst("opaque_cast", target, argument) { Location = call.Location }))
                : null;
        }

        // A value borrowed from a slot seen as a type parameter's: borrowed from the slot.
        if (!owned && argument is LoadBorrowInst { Address: BuiltinInst { Name: "opaque_cast", Operands: [var slot] } } borrow && slot.Type == SilType.Address(target)) {
            if (target.IsTrivial) {
                return new(_ => InsertAfter(borrow, new LoadInst(slot, LoadKind.Trivial) { Location = borrow.Location }));
            }

            return new(function => {
                var again = InsertAfter(borrow, new LoadBorrowInst(slot) { Location = borrow.Location });
                foreach (var end in function.Blocks.SelectMany(static b => b.Instructions).OfType<EndBorrowInst>().Where(e => ReferenceEquals(e.Borrowed, borrow)).ToList()) {
                    end.Block.Instructions.Insert(end.Block.Instructions.IndexOf(end), new EndBorrowInst(again) { Block = end.Block, Location = end.Location });
                }

                return again;
            });
        }

        // A closure made here in the generic convention, by a reabstraction of one that knows the types:
        // the same closure, its reabstraction specialised — when nothing but the call and its destruction uses it.
        if (target is ThickFunctionType thick && argument is PartialApplyInst { Function: FunctionRefInst { Function: var thunk } } closure
            && UsersOf(function, closure).All(u => u == call || u is DestroyValueInst) && ThunkFor(thunk, thick) is { } specialisedThunk) {
            return new(_ => {
                var reference = InsertAfter(closure, new FunctionRefInst(specialisedThunk.Name, specialisedThunk.Type) { Location = closure.Location });
                var made = InsertAfter(reference, new PartialApplyInst(reference, closure.Context, thick) { Location = closure.Location });
                Uses.Replace(function, closure, made);
                closure.Block.Instructions.Remove(closure);
                return made;
            });
        }

        if (argument is BuiltinInst { Name: "opaque_borrow_as", Operands: [var viewed] } && viewed.Type == target && !owned) {
            return new(_ => viewed);
        }

        // A type parameter's value, borrowed: read where it is.
        if (argument.Type is OpaqueType && !owned) {
            if (target.IsTrivial) {
                return new(_ => {
                    var address = Insert(call.Block, call, new BuiltinInst("opaque_address", SilType.Address(target), argument) { Location = call.Location });
                    return Insert(call.Block, call, new LoadInst(address, LoadKind.Trivial) { Location = call.Location });
                });
            }

            if (call is not ApplyInst) {
                return null;
            }

            Conversion? conversion = null;
            conversion = new(_ => {
                var address = Insert(call.Block, call, new BuiltinInst("opaque_address", SilType.Address(target), argument) { Location = call.Location });
                var borrowed = Insert(call.Block, call, new LoadBorrowInst(address) { Location = call.Location });
                conversion!.Ends.Add(new EndBorrowInst(borrowed) { Location = call.Location });
                return borrowed;
            });
            return conversion;
        }

        // A value seen as one naming a type parameter — an array of them — is the same bits seen as the type it is.
        if (SilType.MentionsOpaque(argument.Type) && SilType.SameErasingOpaque(argument.Type, target, opaqueOnly: true)) {
            return new(_ => Insert(call.Block, call, new BuiltinInst(owned ? "opaque_take_as" : "opaque_borrow_as", target, argument) { Location = call.Location }));
        }

        return null;
    }

    static IEnumerable<SilInstruction> UsersOf(SilFunction function, SilValue value) =>
        function.Blocks.SelectMany(static b => b.Instructions).Where(i => i.Operands.Any(o => ReferenceEquals(o, value)));

    static T InsertAfter<T>(SilInstruction after, T instruction) where T : SilInstruction {
        instruction.Block = after.Block;
        after.Block.Instructions.Insert(after.Block.Instructions.IndexOf(after) + 1, instruction);
        return instruction;
    }

    /// <summary>
    ///     Deletes the views, metadata and tables the specialised calls no
    ///     longer use, and a borrow of a view nothing reads but its ends.
    /// </summary>
    internal static void RemoveDeadViews(SilFunction function) {
        for (var again = true; again;) {
            again = false;
            var uses = new Uses(function);
            foreach (var block in function.Blocks) {
                again |= block.Instructions.RemoveAll(instruction => instruction switch {
                    EndBorrowInst { Borrowed: LoadBorrowInst borrow } => Dead(borrow, uses),
                    LoadBorrowInst borrow => Dead(borrow, uses),
                    MetadataInst or FunctionRefInst => uses.Of(instruction).Count == 0,
                    BuiltinInst { Name: var name } => uses.Of(instruction).Count == 0 && Pure(name),
                    _ => false,
                }) > 0;
            }
        }

        static bool Dead(LoadBorrowInst borrow, Uses uses) => uses.Of(borrow).All(static u => u is EndBorrowInst);

        static bool Pure(string name) =>
            name is "opaque_cast" or "opaque_borrow_as" or "opaque_address" or "metadata_array"
            || name.StartsWith("witness_table_known:", StringComparison.Ordinal) || name.StartsWith("witness_table:", StringComparison.Ordinal)
            || name.StartsWith("witness_base:", StringComparison.Ordinal) || name.StartsWith("witness_associated:", StringComparison.Ordinal)
            || name.StartsWith("witness_generic:", StringComparison.Ordinal) || name.StartsWith("opaque_metadata:", StringComparison.Ordinal);
    }
}

/// <summary>
///     One specialised copy of a generic function (<see cref="Specialise" />):
///     its signature for the types, then its body, instruction by instruction.
/// </summary>
sealed partial class SpecialisedCopy(Specialiser specialiser, Specialiser.Plan plan) {
    /// <summary>An opaque type named in a type's name, as <see cref="SilGen.Sanitise" /> writes <c>@opaque(T.Element)</c>: <c>$opaque$T.Element$</c>.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\$opaque\$([A-Za-z0-9_.]+)\$")]
    private static partial System.Text.RegularExpressions.Regex OpaqueInName();

    sealed class Refused(string why) : Exception(why);

    /// <summary>A function value of a generic function or a witness, called only once its types are known.</summary>
    sealed class GenericReference(string function) : SilValue {
        public string Function { get; } = function;
        public override SilType Type => SilType.Unit;
    }

    /// <summary>A closure parameter taken as its caller made it, which returns its result: only called, its result stored where the call said.</summary>
    sealed class NaturalClosureValue(SilArgument closure) : SilValue {
        public SilArgument Closure { get; } = closure;
        public override SilType Type => Closure.Type;
    }

    /// <summary>The storage of a borrowed value, read only: what is read from it is the value.</summary>
    sealed class ValueAddress(SilValue value) : SilValue {
        public SilValue Value { get; } = value;
        public override SilType Type => SilType.Address(Value.Type);
    }

    readonly SilFunction _generic = plan.Generic;
    readonly SilModule _module = specialiser.Module;
    readonly Dictionary<string, SilType> _types = new(plan.Types, StringComparer.Ordinal);
    readonly Dictionary<SilValue, SilValue> _values = [];
    readonly Dictionary<SilBlock, SilBlock> _blocks = [];
    readonly HashSet<SilValue> _unborrowed = [];
    readonly Dictionary<string, SilType> _substituted = new(StringComparer.Ordinal);
    SilFunction _copy = null!;
    AllocStackInst? _out;

    /// <summary>Set where the rest of the block being copied is not reached.</summary>
    bool _unreached;

    /// <summary>The generic body's blocks a branch taken in this copy leads to.</summary>
    readonly HashSet<SilBlock> _reached = new(ReferenceEqualityComparer.Instance);
    SilBlock? _into;

    /// <summary>The copy's signature, its body yet to come; null when the types do not make one.</summary>
    public SilFunction? Shell() {
        try {
            // An associated type is what its base's conformance binds it to.
            for (var progress = true; progress;) {
                progress = false;
                foreach (var associated in _generic.AssociatedMetadata) {
                    if (_types.ContainsKey(associated.Name) || !_types.TryGetValue(associated.Base, out var baseType)) {
                        continue;
                    }

                    if (_module.FindWitnessTable(baseType, associated.Protocol) is not { } table || associated.Index >= table.AssociatedTypes.Count) {
                        throw new Refused($"no table of {baseType}: {associated.Protocol} for {associated.Name}");
                    }

                    _types[associated.Name] = table.AssociatedTypes[associated.Index].Type;
                    progress = true;
                }
            }

            var parameters = ImmutableArray.CreateBuilder<SilType>();
            var owned = ImmutableArray.CreateBuilder<bool>();
            var result = Substitute(_generic.Type.Result);
            for (var i = 0; i < plan.Roles.Length; i++) {
                switch (plan.Roles[i]) {
                    case Specialiser.Role.Value when plan.Natural.TryGetValue(i, out var natural):
                        // The generic convention's signature, as the types make it, must be the closure's with its result written out.
                        var substituted = ((ThickFunctionType)Substitute(_generic.Type.Parameters[i])).Signature;
                        if (substituted.Parameters[..^1].Length != natural.Signature.Parameters.Length
                            || !substituted.Parameters[..^1].SequenceEqual(natural.Signature.Parameters)
                            || substituted.Parameters[^1] != SilType.Address(natural.Signature.Result)
                            || substituted.Thrown?.ToString() != natural.Signature.Thrown?.ToString() || substituted.IsAsync != natural.Signature.IsAsync) {
                            throw new Refused($"{natural} is not {substituted} with its result returned");
                        }

                        parameters.Add(natural);
                        owned.Add(_generic.Type.IsOwned(i));
                        break;
                    case Specialiser.Role.Value:
                        parameters.Add(Substitute(_generic.Type.Parameters[i]));
                        owned.Add(_generic.Type.IsOwned(i));
                        break;
                    case Specialiser.Role.Out:
                        result = Substitute(((AddressType)_generic.Type.Parameters[i]).Pointee);
                        break;
                }
            }

            var type = new FunctionType(parameters.ToImmutable(), result, _generic.Type.Thrown is { } thrown ? Substitute(thrown) : null, owned.ToImmutable(), _generic.Type.IsAsync);
            var baseName = _generic.Name.EndsWith("$generic", StringComparison.Ordinal) ? _generic.Name[..^"$generic".Length] : _generic.Name;
            var types = plan.Roles.Contains(Specialiser.Role.Metadata)
                ? string.Join("$", plan.Roles.Select((r, i) => (r, i)).Where(static p => p.r == Specialiser.Role.Metadata)
                    .Select(p => SilGen.Sanitise(_types[_generic.ParameterNames[p.i]["$metadata.".Length..]].ToString())))
                : string.Join("$", plan.Types.Values.Select(static t => SilGen.Sanitise(t.ToString())));
            _copy = new SilFunction(specialiser.FreshName($"{baseName}$specialised${types}"), type) {
                DisplayName = _generic.DisplayName,
                SourceFile = _generic.SourceFile,
                Line = _generic.Line,
                Inline = _generic.Inline,
                Vectorised = _generic.Vectorised,
            };
            _copy.Scopes.AddRange(_generic.Scopes);
            _copy.Decisions.AddRange(_generic.Decisions);
            for (var i = 0; i < plan.Roles.Length; i++) {
                if (plan.Roles[i] == Specialiser.Role.Value) {
                    _copy.ParameterNames.Add(i < _generic.ParameterNames.Count ? _generic.ParameterNames[i] : $"$argument{i}");
                }
            }

            if (_generic.ResultRoots is { } roots) {
                _copy.ResultRoots = [..roots.Where(r => r < plan.Roles.Length && plan.Roles[r] == Specialiser.Role.Value)
                    .Select(r => plan.Roles.Take(r).Count(static x => x == Specialiser.Role.Value))];
            }

            return _copy;
        } catch (Refused why) {
            Trace(why.Message);
            return null;
        }
    }

    /// <summary>Copies the body; false when something in it cannot be specialised, and the copy is to be dropped.</summary>
    public bool Fill() {
        try {
            Body();
            Specialiser.RemoveDeadViews(_copy);
            // What the generic body read by copying, the copy borrows where it can, and a slot given a copy of a parameter is the parameter.
            new BorrowLoads().Run(_copy);
            Specialiser.ForwardCopiedSlots(_copy);
            return true;
        } catch (Refused why) {
            Trace(why.Message);
            return false;
        }
    }

    /// <summary>With <c>SABLE_SPECIALISE_TRACE</c> set, why a copy was not made.</summary>
    void Trace(string why) {
        if (Environment.GetEnvironmentVariable("SABLE_SPECIALISE_TRACE") is not null) {
            Console.Error.WriteLine($"not specialised {plan.Key}: {why}");
        }
    }

    void Body() {
        var order = SilVerifier.ReversePostorder(_generic);
        foreach (var block in order) {
            var copy = _copy.AddBlock();
            copy.Outcome = block.Outcome;
            _blocks[block] = copy;
            if (block == _generic.Entry) {
                continue;
            }

            foreach (var argument in block.Arguments) {
                var type = Substitute(argument.Type);
                _values[argument] = copy.AddArgument(type, argument.IsGuaranteed && !type.IsTrivial);
            }
        }

        // The parameters: each value as the type it is; metadata and tables constants; the result's address a slot of the copy's own.
        var entry = _blocks[_generic.Entry];
        _into = entry;
        var tables = 0;
        for (var i = 0; i < plan.Roles.Length; i++) {
            var parameter = _generic.Entry.Arguments[i];
            switch (plan.Roles[i]) {
                case Specialiser.Role.Value when plan.Natural.TryGetValue(i, out var natural):
                    _values[parameter] = new NaturalClosureValue(entry.AddArgument(natural));
                    break;
                case Specialiser.Role.Value:
                    _values[parameter] = entry.AddArgument(Substitute(parameter.Type));
                    break;
                case Specialiser.Role.Metadata:
                    _values[parameter] = Emit(new MetadataInst(_types[_generic.ParameterNames[i]["$metadata.".Length..]]), null);
                    break;
                case Specialiser.Role.Table:
                    var table = plan.Tables[tables++];
                    _values[parameter] = Emit(new BuiltinInst($"witness_table_known:{table.Protocol}", SilType.WitnessTable, Emit(new MetadataInst(table.Conformer), null)), null);
                    break;
                case Specialiser.Role.Out:
                    _values[parameter] = _out = Emit(new AllocStackInst(_copy.Type.Result) { VariableName = "$out" }, null);
                    break;
            }
        }

        // A block only branches this copy never takes lead to is left out:
        // what is in it may be what only another type's copy can say.
        _reached.Add(_generic.Entry);
        foreach (var block in order) {
            if (!_reached.Contains(block)) {
                continue;
            }

            _into = _blocks[block];
            _unreached = false;
            foreach (var instruction in block.Instructions) {
                if (_unreached) {
                    break;
                }

                if (instruction is CondBranchInst decided && Raw(decided.Condition) is IntegerLiteralInst { Value: var taken }) {
                    var (target, passed) = taken != 0 ? (decided.WhenTrue, decided.TrueArguments) : (decided.WhenFalse, decided.FalseArguments);
                    Emit(new BranchInst(Block(target), [..passed.Select(Value)]), instruction);
                    _reached.Add(target);
                    break;
                }

                Copy(instruction);
                if (instruction.IsTerminator && !_unreached) {
                    _reached.UnionWith(block.Successors);
                }
            }
        }

        foreach (var (block, copy) in _blocks) {
            if (!_reached.Contains(block)) {
                copy.Append(new UnreachableInst());
            }
        }

        _copy.RemoveUnreachableBlocks();
    }

    T Emit<T>(T instruction, SilInstruction? original) where T : SilInstruction {
        if (original is not null) {
            instruction.Location = original.Location;
            instruction.InlinedSite = original.InlinedSite;
        }

        return Checked(_into!.Append(instruction));
    }

    /// <summary>The instruction, unless what it makes is still of an opaque type — a closure's context, a function taking one — which the copy cannot have.</summary>
    static T Checked<T>(T instruction) where T : SilInstruction =>
        instruction.HasResult && SilType.MentionsOpaque(instruction.Type) ? throw new Refused($"{instruction.Opcode} of {instruction.Type}") : instruction;

    /// <summary>The copy's value for one of the generic body's; a function value of a generic function or a value's storage only where <see cref="Copy" /> takes one.</summary>
    SilValue Value(SilValue value) {
        var mapped = Raw(value);
        return mapped is GenericReference or ValueAddress or NaturalClosureValue ? throw new Refused($"a generic function value or a value's storage used as {mapped.Type}") : mapped;
    }

    SilValue Raw(SilValue value) => _values.TryGetValue(value, out var mapped) ? mapped : throw new Refused("a value used where it is not defined");

    SilBlock Block(SilBlock block) => _blocks.TryGetValue(block, out var mapped) ? mapped : throw new Refused("a branch to a block not reached");

    void Map(SilInstruction original, SilValue copy) => _values[original] = copy;

    void Copy(SilInstruction instruction) {
        switch (instruction) {
            case MetadataInst metadata:
                Map(instruction, Emit(new MetadataInst(Substitute(metadata.Described)), instruction));
                return;

            case BuiltinInst builtin:
                Builtin(builtin);
                return;

            case FunctionRefInst reference:
                Map(instruction, _module.Find(reference.Function) is { IsUnspecialised: true } || SilType.MentionsOpaque(reference.Type)
                    ? new GenericReference(reference.Function)
                    : Emit(new FunctionRefInst(reference.Function, (FunctionType)Substitute(reference.Type)), instruction));
                return;

            case ApplyInst or TryApplyInst:
                Call(instruction);
                return;

            case StructInst structInst:
                Map(instruction, Emit(new StructInst(AsStruct(Substitute(structInst.StructType)), [..structInst.Operands.Select(Value)]), instruction));
                return;

            case EnumInst enumInst:
                Map(instruction, Emit(new EnumInst(AsEnum(Substitute(enumInst.EnumType)), enumInst.Case, enumInst.Payload is { } payload ? Value(payload) : null), instruction));
                return;

            case TupleInst tupleInst:
                Map(instruction, Emit(new TupleInst(Substitute(tupleInst.TupleType) as TupleType ?? throw new Refused("a tuple that is not one"), [..tupleInst.Operands.Select(Value)]), instruction));
                return;

            case AllocStackInst slot:
                Map(instruction, Emit(new AllocStackInst(Substitute(slot.ElementType)) { VariableName = slot.VariableName, Unmanaged = slot.Unmanaged }, instruction));
                return;

            case LoadInst load: {
                var address = Raw(load.Address);
                if (address is ValueAddress storage) {
                    Map(instruction, load.Kind switch {
                        _ when storage.Value.Type.IsTrivial => storage.Value,
                        LoadKind.Copy => Emit(new CopyValueInst(storage.Value), instruction),
                        _ => throw new Refused("a borrowed value's storage taken from"),
                    });
                    return;
                }

                var loaded = Value(load.Address).Type is AddressType { Pointee: var pointee } ? pointee : throw new Refused("a load from a value");
                Map(instruction, Emit(new LoadInst(Value(load.Address), loaded.IsTrivial ? LoadKind.Trivial : load.Kind == LoadKind.Trivial ? LoadKind.Copy : load.Kind), instruction));
                return;
            }

            case LoadBorrowInst borrow: {
                var address = Raw(borrow.Address);
                if (address is ValueAddress storage) {
                    _unborrowed.Add(instruction);
                    Map(instruction, storage.Value);
                    return;
                }

                if (((AddressType)Value(borrow.Address).Type).Pointee.IsTrivial) {
                    _unborrowed.Add(instruction);
                    Map(instruction, Emit(new LoadInst(Value(borrow.Address), LoadKind.Trivial), instruction));
                    return;
                }

                Map(instruction, Emit(new LoadBorrowInst(Value(borrow.Address)), instruction));
                return;
            }

            case EndBorrowInst end:
                if (!_unborrowed.Contains(end.Borrowed) && !Value(end.Borrowed).Type.IsTrivial) {
                    Emit(new EndBorrowInst(Value(end.Borrowed)), instruction);
                }

                return;

            case CopyValueInst copy: {
                var value = Value(copy.Value);
                Map(instruction, value.Type.IsTrivial ? value : Emit(new CopyValueInst(value), instruction));
                return;
            }

            case DestroyValueInst destroy:
                if (!Value(destroy.Value).Type.IsTrivial) {
                    Emit(new DestroyValueInst(Value(destroy.Value)) { Uncounted = destroy.Uncounted }, instruction);
                }

                return;

            case DestroyAddrInst destroy:
                if (Value(destroy.Address).Type is AddressType { Pointee.IsTrivial: false }) {
                    Emit(new DestroyAddrInst(Value(destroy.Address)) { Uncounted = destroy.Uncounted }, instruction);
                }

                return;

            case AllocRefInst alloc:
                Map(instruction, Emit(new AllocRefInst(AsClass(Substitute(alloc.ClassType))) { OnStack = alloc.OnStack }, instruction));
                return;

            case PartialApplyInst partial: {
                var thick = Substitute(partial.ThickType) as ThickFunctionType ?? throw new Refused("a closure that is not one");
                var function = Raw(partial.Function) is GenericReference { Function: var thunk }
                    ? Emit(new FunctionRefInst(specialiser.ThunkFor(thunk, thick)?.Name ?? throw new Refused($"{thunk} is not specialised for {thick}"), thick.Implementation), instruction)
                    : Value(partial.Function);
                Map(instruction, Emit(new PartialApplyInst(function, partial.Context is { } context ? Value(context) : null, thick), instruction));
                return;
            }

            case UncheckedRefCastInst cast:
                Map(instruction, Emit(new UncheckedRefCastInst(Value(cast.Value), AsClass(Substitute(cast.Type))), instruction));
                return;

            case UpcastInst upcast:
                Map(instruction, Emit(new UpcastInst(Value(upcast.Value), AsClass(Substitute(upcast.Type))), instruction));
                return;

            // One called in its slot's own convention, which no copy of the caller changes.
            case ClassMethodInst { Type: FunctionType slot } when SilType.MentionsOpaque(slot):
                throw new Refused("a call through a generic class's vtable");
            case ClassMethodInst method:
                Map(instruction, Emit(new ClassMethodInst(Value(method.Reference), method.Method, (FunctionType)Substitute(method.Type)), instruction));
                return;

            case GlobalAddrInst global:
                Map(instruction, Emit(new GlobalAddrInst(global.Global, Substitute(global.ValueType)), instruction));
                return;

            case ReturnInst returned when _out is not null:
                var result = Emit(new LoadInst(_out, _out.ElementType.IsTrivial ? LoadKind.Trivial : LoadKind.Take), instruction);
                Emit(new DeallocStackInst(_out), instruction);
                Emit(new ReturnInst(result), instruction);
                return;

            default:
                var cloned = instruction.Clone(Value, Block);
                cloned.InlinedSite = instruction.InlinedSite;
                Map(instruction, Checked(_into!.Append(cloned)));
                return;
        }
    }

    void Call(SilInstruction call) {
        var callee = Raw(call.Operands[0]);
        var arguments = call.Operands.Skip(1).Select(Value).ToList();
        if (callee is NaturalClosureValue { Closure: var closure }) {
            // Its result returned, and stored where the generic convention would have had it written.
            var signature = ((ThickFunctionType)closure.Type).Signature;
            var into = arguments[^1];
            switch (call) {
                case TryApplyInst tryApply: {
                    var returned = _copy.AddBlock();
                    var result = returned.AddArgument(signature.Result);
                    Emit(new TryApplyInst(closure, signature.Result, [..arguments[..^1]], returned, Block(tryApply.Error)), call);
                    returned.Append(new StoreInst(result, into, StoreKind.Init) { Location = call.Location });
                    returned.Append(new BranchInst(Block(tryApply.Normal)) { Location = call.Location });
                    return;
                }
                default:
                    Emit(new StoreInst(Emit(new ApplyInst(closure, signature.Result, [..arguments[..^1]]), call), into, StoreKind.Init), call);
                    return;
            }
        }

        // 'T.self == Int.self', of a T known here.
        if (callee is FunctionRefInst { Function: "Sable.Core.Type.==" } && arguments is [var left, var right]
            && specialiser.KnownMetadata(left) is { } leftType && specialiser.KnownMetadata(right) is { } rightType) {
            Map(call, Emit(new IntegerLiteralInst(SilType.Bool, leftType == rightType ? 1 : 0), call));
            return;
        }

        if (callee is not GenericReference generic) {
            var callee1 = Value(call.Operands[0]);
            Map(call, call switch {
                TryApplyInst tryApply => Emit(new TryApplyInst(callee1, Substitute(tryApply.ResultType), [..arguments], Block(tryApply.Normal), Block(tryApply.Error)), call),
                _ => Emit(new ApplyInst(callee1, Substitute(call.Type), [..arguments]), call),
            });
            return;
        }

        var copy = CopyOf(generic.Function, arguments, call is TryApplyInst, out var passed, out var outAddress);
        var reference = Emit(new FunctionRefInst(copy.Name, copy.Type), call);
        switch (call) {
            case TryApplyInst tryApply:
                if (Substitute(tryApply.ResultType) != copy.Type.Result) {
                    throw new Refused("a throwing call whose result changes type");
                }

                if (Block(tryApply.Error).Arguments is [var thrown] && copy.Type.Thrown is { } copyThrown && thrown.Type != copyThrown) {
                    throw new Refused($"a call throwing {copyThrown} to a block taking {thrown.Type}");
                }

                Map(call, Emit(new TryApplyInst(reference, copy.Type.Result, [..passed], Block(tryApply.Normal), Block(tryApply.Error)), call));
                return;
            default:
                var apply = Emit(new ApplyInst(reference, copy.Type.Result, [..passed]), call);
                if (outAddress is not null) {
                    Emit(new StoreInst(apply, outAddress, StoreKind.Init), call);
                } else if (SilType.MentionsOpaque(call.Type) ? !SilType.SameErasingOpaque(call.Type, copy.Type.Result, opaqueOnly: true) : call.Type != copy.Type.Result) {
                    throw new Refused("a call whose result changes type");
                }

                Map(call, apply);
                return;
        }
    }

    Dictionary<SilValue, List<SilInstruction>>? _users;

    /// <summary>Whether a value seen as a callee's type is only passed to calls, borrowed from there or not.</summary>
    bool IsLent(SilValue value) {
        if (_users is null) {
            _users = new(ReferenceEqualityComparer.Instance);
            foreach (var instruction in plan.Generic.Blocks.SelectMany(static b => b.Instructions)) {
                foreach (var operand in instruction.Operands) {
                    (_users.TryGetValue(operand, out var list) ? list : _users[operand] = []).Add(instruction);
                }
            }
        }

        return _users.TryGetValue(value, out var users) && users.TrueForAll(u => u switch {
            ApplyInst or TryApplyInst => !ReferenceEquals(u.Operands[0], value),
            EndBorrowInst => true,
            LoadBorrowInst => IsLent(u),
            _ => false,
        });
    }

    /// <summary>
    ///     The copy a call of a generic function or a witness, with types now
    ///     known, calls instead; what it is passed of the arguments, and
    ///     where the call was to write a result the copy returns.
    /// </summary>
    SilFunction CopyOf(string name, List<SilValue> arguments, bool throwing, out List<SilValue> passed, out SilValue? outAddress) {
        if (_module.Find(name) is not { } function || specialiser.PlanCall(function, arguments) is not { } calleePlan) {
            throw new Refused($"a call of {name} whose types are not known");
        }

        if (throwing && calleePlan.Roles.Contains(Specialiser.Role.Out)) {
            throw new Refused($"a throwing call of {name} with an indirect result");
        }

        var copy = specialiser.CopyFor(calleePlan, hot: true) ?? throw new Refused($"{name} is not specialised for {calleePlan.Key}");
        passed = [];
        outAddress = null;
        var p = 0;
        for (var i = 0; i < arguments.Count; i++) {
            switch (calleePlan.Roles[i]) {
                case Specialiser.Role.Value:
                    passed.Add(arguments[i].Type == copy.Type.Parameters[p] ? arguments[i] : throw new Refused($"{arguments[i].Type} passed as {copy.Type.Parameters[p]}"));
                    p++;
                    break;
                case Specialiser.Role.Out:
                    outAddress = arguments[i].Type == SilType.Address(copy.Type.Result) ? arguments[i] : throw new Refused($"a result of {copy.Type.Result} written to {arguments[i].Type}");
                    break;
            }
        }

        return copy;
    }

    /// <summary>
    ///     A value written out, or boxed as <c>any Error</c>, by the function
    ///     made for its type to do it given the value's storage
    ///     (<see cref="SilModule.Describers" />, <see cref="SilModule.ErrorErasers" />):
    ///     that function's copy for the type, called with the value. Boxing
    ///     takes the value; the function copies it.
    /// </summary>
    void Boxing(BuiltinInst builtin, Dictionary<string, (SilType Type, string Function)> made, bool takes) {
        var value = Value(builtin.Operands[0]);
        if (!made.TryGetValue(value.Type.ToString(), out var helper)) {
            throw new Refused($"{builtin.Name} of {value.Type}, which has no function to do it");
        }

        var copy = CopyOf(helper.Function, [value, Emit(new MetadataInst(value.Type), builtin)], false, out var passed, out _);
        var apply = Emit(new ApplyInst(Emit(new FunctionRefInst(copy.Name, copy.Type), builtin), copy.Type.Result, [..passed]), builtin);
        if (takes && !value.Type.IsTrivial) {
            Emit(new DestroyValueInst(value), builtin);
        }

        Map(builtin, apply);
    }

    void Builtin(BuiltinInst builtin) {
        var name = builtin.Name;
        if (name.StartsWith("opaque_metadata:", StringComparison.Ordinal)) {
            Map(builtin, Emit(new MetadataInst(Associated(name["opaque_metadata:".Length..]) ?? throw new Refused($"no type for {name}")), builtin));
            return;
        }

        if (name.StartsWith("witness_generic:", StringComparison.Ordinal)) {
            var table = specialiser.KnownTable(Value(builtin.Operands[0])) ?? throw new Refused($"{name} of a table not known");
            var index = int.Parse(name["witness_generic:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Map(builtin, new GenericReference(index < table.Entries.Count && table.Entries[index].Function is { } witness ? witness : throw new Refused($"no witness {index} in {table.Conformer}: {table.Protocol}")));
            return;
        }

        if (name.StartsWith("witness_associated:", StringComparison.Ordinal) && specialiser.KnownMetadata(new BuiltinInst(name, SilType.Metadata, Value(builtin.Operands[0]))) is { } associated) {
            Map(builtin, Emit(new MetadataInst(associated), builtin));
            return;
        }

        var operands = builtin.Operands.Select(Raw).ToList();
        switch (name) {
            case "opaque_cast" or "opaque_borrow_as" or "opaque_take_as": {
                var viewed = Value(builtin.Operands[0]);

                // A function a generic type keeps in its own convention, seen as this code's: the copy would call it in another.
                // One over it that converts, as code that knows the types has.
                if (builtin.Operands[0].Type is ThickFunctionType && IsStored(builtin.Operands[0].Type) != IsStored(builtin.Type)) {
                    if (name != "opaque_take_as" || viewed.Type is not ThickFunctionType from || Substitute(builtin.Type) is not ThickFunctionType to
                        || SilGen.Reabstract(_module, from, to) is not var (thunk, context)) {
                        throw new Refused($"{name} of a function kept in a generic type");
                    }

                    if (from == to) {
                        Map(builtin, viewed);
                        return;
                    }

                    var holder = Emit(new AllocRefInst(context), builtin);
                    Emit(new StoreInst(viewed, Emit(new RefElementAddrInst(holder, 0), builtin), StoreKind.Init), builtin);
                    Map(builtin, Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, to.Implementation), builtin), holder, to), builtin));
                    return;
                }

                // A value lent to another generic function as its own type parameter's: in the copy, as it is.
                if (name is not "opaque_take_as" && !SilType.MentionsOpaque(viewed.Type) && IsLent(builtin)) {
                    Map(builtin, viewed);
                    return;
                }

                var target = Substitute(builtin.Type);
                if (viewed.Type == target) {
                    Map(builtin, viewed);
                    return;
                }

                if (!SilType.MentionsOpaque(viewed.Type) && !SilType.MentionsOpaque(target)) {
                    throw new Refused($"{name} from {viewed.Type} to {target}");
                }

                break;
            }

            case "opaque_address": {
                var value = Value(builtin.Operands[0]);
                if (SilType.Address(value.Type) != Substitute(builtin.Type)) {
                    throw new Refused($"the storage of {value.Type} seen as {Substitute(builtin.Type)}");
                }

                Map(builtin, new ValueAddress(value));
                return;
            }

            case "metadata_array" when specialiser.KnownMetadata(operands[0]) is { } element:
                Map(builtin, Emit(new MetadataInst(new ArrayType(element)), builtin));
                return;

            case "metadata_equal" when specialiser.KnownMetadata(operands[0]) is { } a && specialiser.KnownMetadata(operands[1]) is { } b:
                Map(builtin, Emit(new IntegerLiteralInst(SilType.Bool, a == b ? 1 : 0), builtin));
                return;

            case "metadata_kind" when specialiser.KnownMetadata(operands[0]) is { } kindOf && TypeMetadata.KindOf(kindOf) is var kind:
                Map(builtin, Emit(new IntegerLiteralInst(SilType.Int, (long)kind), builtin));
                return;

            case "cmp_eq_Int" when operands is [IntegerLiteralInst one, IntegerLiteralInst other]:
                Map(builtin, Emit(new IntegerLiteralInst(SilType.Bool, one.Value == other.Value ? 1 : 0), builtin));
                return;

            case "opaque_copy_as": {
                var held = Value(builtin.Operands[0]);
                if (held.Type != builtin.Type) {
                    throw new Refused($"{name} of {held.Type} as {builtin.Type}");
                }

                Map(builtin, held.Type.IsTrivial ? held : Emit(new CopyValueInst(held), builtin));
                return;
            }

            // Its dynamic type: what the metadata says, but for an object, whose class may be a subclass.
            // And for an existential, what its box holds.
            case "opaque_type" when specialiser.KnownMetadata(operands[1]) is ClassType known && Value(builtin.Operands[0]).Type == known:
                Map(builtin, Emit(new BuiltinInst("object_metadata", SilType.Metadata, Value(builtin.Operands[0])), builtin));
                return;

            case "opaque_type" when specialiser.KnownMetadata(operands[1]) is { } known && TypeMetadata.IsExistential(known):
                if (Value(builtin.Operands[0]).Type != known) {
                    throw new Refused($"{name} of an existential elsewhere");
                }

                Map(builtin, Emit(new BuiltinInst("box_metadata", SilType.Metadata, Emit(new StructExtractInst(Value(builtin.Operands[0]), 0), builtin)), builtin));
                return;

            case "opaque_type" when specialiser.KnownMetadata(operands[1]) is { } and not ClassType:
                Map(builtin, operands[1]);
                return;

            case "value_witness_copy" when specialiser.KnownMetadata(operands[0]) is { } type && Value(builtin.Operands[1]).Type == type: {
                var value = Value(builtin.Operands[1]);
                Map(builtin, type.IsTrivial ? value : Emit(new CopyValueInst(value), builtin));
                return;
            }

            case "value_witness_take" when specialiser.KnownMetadata(operands[0]) is { } type && Value(builtin.Operands[1]).Type == type:
                Map(builtin, Value(builtin.Operands[1]));
                return;

            case "value_witness_destroy" when specialiser.KnownMetadata(operands[0]) is { } type && Value(builtin.Operands[1]).Type == type:
                if (!type.IsTrivial) {
                    Emit(new DestroyValueInst(Value(builtin.Operands[1])), builtin);
                }

                return;

            case "value_witness_assign" when specialiser.KnownMetadata(operands[0]) is { } type && Value(builtin.Operands[2]).Type == type: {
                var value = Value(builtin.Operands[2]);
                Emit(new StoreInst(type.IsTrivial ? value : Emit(new CopyValueInst(value), builtin), Value(builtin.Operands[1]), StoreKind.Assign), builtin);
                return;
            }

            case "generic_describe":
                Boxing(builtin, _module.Describers, takes: false);
                return;

            case "generic_debug_describe":
                Boxing(builtin, _module.DebugDescribers, takes: false);
                return;

            // An error of a type with no values — Never, laid out as nothing — is never thrown: the code that boxes one is not reached.
            case "opaque_erase_error" when Value(builtin.Operands[0]).Type == SilType.Unit:
                Emit(new UnreachableInst(), builtin);
                _unreached = true;
                return;

            case "opaque_erase_error":
                Boxing(builtin, _module.ErrorErasers, takes: true);
                return;

            case "opaque_type":
                throw new Refused($"{name}, which needs the value boxed");
        }

        Map(builtin, Emit(new BuiltinInst(name, Substitute(builtin.Type), [..builtin.Operands.Select(Value)]) { InBounds = builtin.InBounds }, builtin));
    }

    // ---- Types --------------------------------------------------------------

    /// <summary>A function type of a generic type's own convention (SilGen.StoredFunctionType), the same in every copy.</summary>
    static bool IsStored(SilType type) => type is ThickFunctionType && (type.ToString().Contains($"@opaque({Symbols.TypeParameterSymbol.StoredPrefix}", StringComparison.Ordinal)
                                                                        || type.ToString().Contains($"$opaque${Symbols.TypeParameterSymbol.StoredPrefix}", StringComparison.Ordinal));

    static StructType AsStruct(SilType type) => type as StructType ?? throw new Refused($"{type} is not a struct");
    static EnumType AsEnum(SilType type) => type as EnumType ?? throw new Refused($"{type} is not an enum");
    static ClassType AsClass(SilType type) => type as ClassType ?? throw new Refused($"{type} is not a class");

    /// <summary>A type of the generic body as it is in the copy: each opaque type the type it is, and each type made of one the same type made of that.</summary>
    SilType Substitute(SilType type) {
        if (!SilType.MentionsOpaque(type)) {
            return type;
        }

        return type switch {
            OpaqueType opaque => Associated(opaque.Name) ?? throw new Refused($"no type for {opaque}"),
            ArrayType array => new ArrayType(Substitute(array.Element)),
            SliceType slice => new SliceType(Substitute(slice.Element)),
            AddressType address => SilType.Address(Substitute(address.Pointee)),
            InlinePointerType pointer => new InlinePointerType(Substitute(pointer.Pointee)),
            InlineArrayType inline => new InlineArrayType(Substitute(inline.Element), inline.Capacity),
            VectorType vector => vector,
            FunctionType function => new FunctionType([..function.Parameters.Select(Substitute)], Substitute(function.Result), function.Thrown is { } thrown ? Substitute(thrown) : null,
                function.Owned.IsEmpty ? default : function.Owned, function.IsAsync),
            ThickFunctionType when IsStored(type) => type,
            ThickFunctionType thick => new ThickFunctionType((FunctionType)Substitute(thick.Signature)),
            TupleType tuple => SilType.Tuple([..tuple.Elements.Select(Substitute)]),
            WeakStorageType weak => new WeakStorageType(AsEnum(Substitute(weak.Optional))),
            NominalType nominal => SubstituteNominal(nominal),
            _ => throw new Refused($"a type it cannot specialise: {type}"),
        };
    }

    /// <summary>
    ///     The type an opaque type's name stands for: a type parameter's, or
    ///     an associated type read through one, <c>Self.Iter</c>, as a
    ///     conformance of the type it is read through binds it — where the
    ///     function, a witness, does not say which conformance, the one of
    ///     the type's that has an associated type of that name.
    /// </summary>
    SilType? Associated(string name) {
        if (_types.TryGetValue(name, out var known)) {
            return known;
        }

        var dot = name.LastIndexOf('.');
        if (dot < 0 || Associated(name[..dot]) is not { } baseType) {
            return null;
        }

        var member = name[(dot + 1)..];
        var bound = _module.WitnessTables.Where(t => t.Conformer == baseType).SelectMany(static t => t.AssociatedTypes).Where(a => a.Name == member)
            .Select(static a => a.Type).Distinct().ToList();
        return bound is [var type] ? _types[name] = type : null;
    }

    /// <summary>
    ///     A generic type of opaque types — an optional, a generic struct or
    ///     enum, named for them (<c>Optional$$opaque$T$</c>) — as the same type
    ///     of the types they are: the one the module has by that name, or,
    ///     for an enum or a struct with no deinit, one made like it.
    /// </summary>
    SilType SubstituteNominal(NominalType nominal) {
        var name = OpaqueInName().Replace(nominal.Name,
            match => Associated(match.Groups[1].Value) is { } type ? SilGen.Sanitise(type.ToString()) : throw new Refused($"no type for {match.Value} in {nominal}"));
        if (_substituted.TryGetValue(name, out var made)) {
            return made;
        }

        if (SilType.MentionsOpaque(new ScalarType(name))) {
            throw new Refused($"no type for {nominal}");
        }

        if (_module.FindType(name) is { } existing) {
            return existing.GetType() == nominal.GetType() ? existing : throw new Refused($"{name} is not a {nominal.GetType().Name}");
        }

        switch (nominal) {
            case EnumType enumType: {
                var copy = new EnumType(name) { DisplayName = enumType.DisplayName, IsNoncopyable = enumType.IsNoncopyable, IsUnion = enumType.IsUnion };
                _substituted[name] = copy;
                copy.Conformances.AddRange(enumType.Conformances);
                copy.ConditionalConformances.AddRange(enumType.ConditionalConformances);
                copy.TypeArguments = [..enumType.TypeArguments.Select(Substitute)];
                _module.MeetConditionalConformances(copy);
                foreach (var (caseName, payload) in enumType.Cases) {
                    copy.Cases.Add((caseName, payload is null ? null : Substitute(payload)));
                }

                foreach (var (caseName, labels) in enumType.PayloadLabels) {
                    copy.PayloadLabels[caseName] = labels;
                }

                return _module.AddType(copy);
            }

            case StructType { Deinit: null, IsInline: false } structType: {
                var copy = new StructType(name) { DisplayName = structType.DisplayName, IsNoncopyable = structType.IsNoncopyable, IsPacked = structType.IsPacked, Alignment = structType.Alignment };
                _substituted[name] = copy;
                copy.Conformances.AddRange(structType.Conformances);
                copy.ConditionalConformances.AddRange(structType.ConditionalConformances);
                copy.TypeArguments = [..structType.TypeArguments.Select(Substitute)];
                _module.MeetConditionalConformances(copy);
                foreach (var (fieldName, fieldType) in structType.Fields) {
                    copy.Fields.Add((fieldName, Substitute(fieldType)));
                }

                return _module.AddType(copy);
            }

            // A generic class compiled once, laid out for its arguments: no
            // superclass, no vtable, no deinit, so its fields are all of it.
            case ClassType { Superclass: null, VTable.Count: 0, Deinit: null, IsCoroutineFrame: false } classType: {
                var arguments = classType.TypeArguments.Select(Substitute).ToList();
                var display = classType.DisplayName is { } written && written.IndexOf('<') is > 0 and var open
                    ? $"{written[..open]}<{string.Join(", ", arguments.Select(TypeMetadata.DisplayName))}>"
                    : classType.DisplayName;
                var deinit = display is null ? null : _module.GenericDeinits.GetValueOrDefault(display.IndexOf('<') is > 0 and var end ? display[..end] : display);
                if (classType.HasGenericDeinit && deinit is null) {
                    throw new Refused($"{nominal}, whose deinit is another unit's");
                }

                var copy = new ClassType(name) { DisplayName = display, Deinit = deinit };
                _substituted[name] = copy;
                copy.Conformances.AddRange(classType.Conformances);
                copy.ConditionalConformances.AddRange(classType.ConditionalConformances);
                copy.TypeArguments = [..arguments];
                _module.MeetConditionalConformances(copy);
                foreach (var (fieldName, fieldType) in classType.Fields) {
                    copy.Fields.Add((fieldName, Substitute(fieldType)));
                }

                return _module.AddType(copy);
            }

            default:
                throw new Refused($"no type {name} for {nominal}");
        }
    }
}
