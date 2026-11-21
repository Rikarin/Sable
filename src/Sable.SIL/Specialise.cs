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
///         the type's own copy, move or destroy; and a result written
///         where the caller said is returned. What is left is code as the
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
        if (module.Unit is null && SilPassManager.Reachable(module) is { } reached) {
            module.RemoveAll(f => f.IsUnspecialised && !reached.Contains(f.Name));
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
    internal sealed record Plan(SilFunction Generic, Role[] Roles, Dictionary<string, SilType> Types, List<SilWitnessTable> Tables, string Key);

    /// <summary>Each parameter's role, or null for a function the copy cannot be made of.</summary>
    internal static Role[]? Roles(SilFunction function) {
        var parameters = function.Type.Parameters;
        if (function.Blocks.Count == 0 || function.Entry.Arguments.Count != parameters.Length || function.Type.IsAsync) {
            return null;
        }

        var roles = new Role[parameters.Length];
        for (var i = 0; i < parameters.Length; i++) {
            var name = i < function.ParameterNames.Count ? function.ParameterNames[i] : "";
            if (name.StartsWith("$metadata.", StringComparison.Ordinal)) {
                roles[i] = Role.Metadata;
            } else if (name.StartsWith("$witnesses.", StringComparison.Ordinal)) {
                roles[i] = Role.Table;
            } else if (name == "$error") {
                // An error of a type parameter's type, thrown where the caller said.
                return null;
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
                if (CopyFor(plan, hot) is not { } copy || !CallCopy(function, call, plan, copy)) {
                    continue;
                }

                changed = true;
            }
        }

        if (changed) {
            RemoveDeadViews(function);
        }
    }

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
                case Role.Value:
                    if (Convert(arguments[i], parameters[p], copy.Type.IsOwned(p), call) is not { } conversion) {
                        return false;
                    }

                    conversions.Add(conversion);
                    p++;
                    break;
                case Role.Out:
                    if (Convert(arguments[i], SilType.Address(copy.Type.Result), false, call) is not { } address) {
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
        if (outAddress is null && resultType != copy.Type.Result) {
            if (call is TryApplyInst || !SilType.SameErasingOpaque(copy.Type.Result, resultType, opaqueOnly: true)) {
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
            TryApplyInst tryApply => new TryApplyInst(reference, copy.Type.Result, [..converted], tryApply.Normal, tryApply.Error),
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
    ///     How a caller's argument is seen as the type the copy takes: as it
    ///     is; the storage a view of it was made from; or a view made at the
    ///     call. Null when it cannot be.
    /// </summary>
    Conversion? Convert(SilValue argument, SilType target, bool owned, SilInstruction call) {
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
sealed class SpecialisedCopy(Specialiser specialiser, Specialiser.Plan plan) {
    sealed class Refused(string why) : Exception(why);

    /// <summary>A function value of a generic function or a witness, called only once its types are known.</summary>
    sealed class GenericReference(string function) : SilValue {
        public string Function { get; } = function;
        public override SilType Type => SilType.Unit;
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
                        return null;
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
            var types = string.Join("$", plan.Roles.Select((r, i) => (r, i)).Where(static p => p.r == Specialiser.Role.Metadata)
                .Select(p => SilGen.Sanitise(_types[_generic.ParameterNames[p.i]["$metadata.".Length..]].ToString())));
            _copy = new SilFunction(specialiser.FreshName($"{baseName}$specialised${types}"), type) {
                DisplayName = _generic.DisplayName,
                SourceFile = _generic.SourceFile,
                Line = _generic.Line,
                Inline = _generic.Inline,
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
        } catch (Refused) {
            return null;
        }
    }

    /// <summary>Copies the body; false when something in it cannot be specialised, and the copy is to be dropped.</summary>
    public bool Fill() {
        try {
            Body();
            Specialiser.RemoveDeadViews(_copy);
            return true;
        } catch (Refused why) {
            if (Environment.GetEnvironmentVariable("SABLE_SPECIALISE_TRACE") is not null) {
                Console.Error.WriteLine($"not specialised {_generic.Name} for {plan.Key}: {why.Message}");
            }

            return false;
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

        foreach (var block in order) {
            _into = _blocks[block];
            foreach (var instruction in block.Instructions) {
                Copy(instruction);
            }
        }
    }

    T Emit<T>(T instruction, SilInstruction? original) where T : SilInstruction {
        if (original is not null) {
            instruction.Location = original.Location;
            instruction.InlinedSite = original.InlinedSite;
        }

        return _into!.Append(instruction);
    }

    /// <summary>The copy's value for one of the generic body's; a function value of a generic function or a value's storage only where <see cref="Copy" /> takes one.</summary>
    SilValue Value(SilValue value) {
        var mapped = Raw(value);
        return mapped is GenericReference or ValueAddress ? throw new Refused($"a generic function value or a value's storage used as {mapped.Type}") : mapped;
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
                Map(instruction, _module.Find(reference.Function) is { IsUnspecialised: true }
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

            case PartialApplyInst partial:
                Map(instruction, Emit(new PartialApplyInst(Value(partial.Function), partial.Context is { } context ? Value(context) : null,
                    Substitute(partial.ThickType) as ThickFunctionType ?? throw new Refused("a closure that is not one")), instruction));
                return;

            case UncheckedRefCastInst cast:
                Map(instruction, Emit(new UncheckedRefCastInst(Value(cast.Value), AsClass(Substitute(cast.Type))), instruction));
                return;

            case UpcastInst upcast:
                Map(instruction, Emit(new UpcastInst(Value(upcast.Value), AsClass(Substitute(upcast.Type))), instruction));
                return;

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
                Map(instruction, _into!.Append(cloned));
                return;
        }
    }

    void Call(SilInstruction call) {
        var callee = Raw(call.Operands[0]);
        var arguments = call.Operands.Skip(1).Select(Value).ToList();
        if (callee is not GenericReference generic) {
            var callee1 = Value(call.Operands[0]);
            Map(call, call switch {
                TryApplyInst tryApply => Emit(new TryApplyInst(callee1, Substitute(tryApply.ResultType), [..arguments], Block(tryApply.Normal), Block(tryApply.Error)), call),
                _ => Emit(new ApplyInst(callee1, Substitute(call.Type), [..arguments]), call),
            });
            return;
        }

        // A generic function or a witness, called with types now known: the copy for them.
        if (_module.Find(generic.Function) is not { } function || specialiser.PlanCall(function, arguments) is not { } calleePlan) {
            throw new Refused($"a call of {generic.Function} whose types are not known");
        }

        if (call is TryApplyInst && calleePlan.Roles.Contains(Specialiser.Role.Out)) {
            throw new Refused($"a throwing call of {generic.Function} with an indirect result");
        }

        var copy = specialiser.CopyFor(calleePlan, hot: true) ?? throw new Refused($"{generic.Function} is not specialised for {calleePlan.Key}");
        var passed = new List<SilValue>();
        SilValue? outAddress = null;
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

        var reference = Emit(new FunctionRefInst(copy.Name, copy.Type), call);
        switch (call) {
            case TryApplyInst tryApply:
                if (Substitute(tryApply.ResultType) != copy.Type.Result) {
                    throw new Refused("a throwing call whose result changes type");
                }

                Map(call, Emit(new TryApplyInst(reference, copy.Type.Result, [..passed], Block(tryApply.Normal), Block(tryApply.Error)), call));
                return;
            default:
                var apply = Emit(new ApplyInst(reference, copy.Type.Result, [..passed]), call);
                if (outAddress is not null) {
                    Emit(new StoreInst(apply, outAddress, StoreKind.Init), call);
                } else if (Substitute(call.Type) != copy.Type.Result) {
                    throw new Refused("a call whose result changes type");
                }

                Map(call, apply);
                return;
        }
    }

    void Builtin(BuiltinInst builtin) {
        var name = builtin.Name;
        if (name.StartsWith("opaque_metadata:", StringComparison.Ordinal)) {
            Map(builtin, Emit(new MetadataInst(_types.GetValueOrDefault(name["opaque_metadata:".Length..]) ?? throw new Refused($"no type for {name}")), builtin));
            return;
        }

        if (name.StartsWith("witness_generic:", StringComparison.Ordinal)) {
            var table = specialiser.KnownTable(Value(builtin.Operands[0])) ?? throw new Refused($"{name} of a table not known");
            var index = int.Parse(name["witness_generic:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Map(builtin, new GenericReference(index < table.GenericEntries.Count && table.GenericEntries[index].Function is { } witness ? witness : throw new Refused($"no witness {index} in {table.Conformer}: {table.Protocol}")));
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

            // Its dynamic type: what the metadata says, but for an object, whose class may be a subclass.
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

            case "generic_describe" or "opaque_erase_error" or "opaque_type":
                throw new Refused($"{name}, which needs the value boxed");
        }

        Map(builtin, Emit(new BuiltinInst(name, Substitute(builtin.Type), [..builtin.Operands.Select(Value)]) { InBounds = builtin.InBounds }, builtin));
    }

    // ---- Types --------------------------------------------------------------

    static StructType AsStruct(SilType type) => type as StructType ?? throw new Refused($"{type} is not a struct");
    static EnumType AsEnum(SilType type) => type as EnumType ?? throw new Refused($"{type} is not an enum");
    static ClassType AsClass(SilType type) => type as ClassType ?? throw new Refused($"{type} is not a class");

    /// <summary>A type of the generic body as it is in the copy: each opaque type the type it is, and each type made of one the same type made of that.</summary>
    SilType Substitute(SilType type) {
        if (!SilType.MentionsOpaque(type)) {
            return type;
        }

        return type switch {
            OpaqueType opaque => _types.TryGetValue(opaque.Name, out var known) ? known : throw new Refused($"no type for {opaque}"),
            ArrayType array => new ArrayType(Substitute(array.Element)),
            SliceType slice => new SliceType(Substitute(slice.Element)),
            AddressType address => SilType.Address(Substitute(address.Pointee)),
            InlinePointerType pointer => new InlinePointerType(Substitute(pointer.Pointee)),
            InlineArrayType inline => new InlineArrayType(Substitute(inline.Element), inline.Capacity),
            FunctionType function => new FunctionType([..function.Parameters.Select(Substitute)], Substitute(function.Result), function.Thrown is { } thrown ? Substitute(thrown) : null,
                function.Owned.IsEmpty ? default : function.Owned, function.IsAsync),
            ThickFunctionType thick => new ThickFunctionType((FunctionType)Substitute(thick.Signature)),
            TupleType tuple => SilType.Tuple([..tuple.Elements.Select(Substitute)]),
            WeakStorageType weak => new WeakStorageType(AsEnum(Substitute(weak.Optional))),
            NominalType nominal => SubstituteNominal(nominal),
            _ => throw new Refused($"a type it cannot specialise: {type}"),
        };
    }

    /// <summary>
    ///     A generic type of opaque types — an optional, a generic struct or
    ///     enum, named for them (<c>Optional$$opaque$T$</c>) — as the same type
    ///     of the types they are: the one the module has by that name, or,
    ///     for an enum or a struct with no deinit, one made like it.
    /// </summary>
    SilType SubstituteNominal(NominalType nominal) {
        var name = nominal.Name;
        foreach (var (opaque, type) in _types.OrderByDescending(static p => p.Key.Length)) {
            name = name.Replace(SilGen.Sanitise($"@opaque({opaque})"), SilGen.Sanitise(type.ToString()), StringComparison.Ordinal);
        }

        if (_substituted.TryGetValue(name, out var made)) {
            return made;
        }

        if (_module.FindType(name) is { } existing) {
            return existing.GetType() == nominal.GetType() ? existing : throw new Refused($"{name} is not a {nominal.GetType().Name}");
        }

        if (SilType.MentionsOpaque(new ScalarType(name))) {
            throw new Refused($"no type for {nominal}");
        }

        switch (nominal) {
            case EnumType enumType: {
                var copy = new EnumType(name) { DisplayName = enumType.DisplayName, IsNoncopyable = enumType.IsNoncopyable, IsUnion = enumType.IsUnion };
                _substituted[name] = copy;
                copy.Conformances.AddRange(enumType.Conformances);
                copy.TypeArguments = [..enumType.TypeArguments.Select(Substitute)];
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
                copy.TypeArguments = [..structType.TypeArguments.Select(Substitute)];
                foreach (var (fieldName, fieldType) in structType.Fields) {
                    copy.Fields.Add((fieldName, Substitute(fieldType)));
                }

                return _module.AddType(copy);
            }

            default:
                throw new Refused($"no type {name} for {nominal}");
        }
    }
}
