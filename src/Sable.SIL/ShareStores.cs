namespace Sable.SIL;

/// <summary>
///     Keeps the invariant the counts depend on (docs/02 §2.7): what an object
///     more than one thread may reach holds is counted atomically too. A
///     reference put into such an object, or into a global, is shared
///     (runtime/tasks.c's <c>sable_share</c>) as it is put there.
/// </summary>
/// <remarks>
///     <para>
///         Every address inside an object comes from one of a few
///         instructions: a class's field (<c>ref_element_addr</c>), an element
///         of an array (<c>array_element_addr</c>) and the value in an
///         existential's box (<c>box_project_unique</c>); a global's from
///         <c>global_addr</c>. Each names what the address is inside: its
///         owner. The pass follows each such address, and looks at what is
///         done through it:
///     </para>
///     <list type="bullet">
///         <item>
///             a store in this function: the value is shared before it, if the
///             owner is (<c>share_into</c>);
///         </item>
///         <item>
///             anything that may store there out of sight — a call given the
///             address, or a closure that holds it, or a builtin that replaces
///             an array's buffer: the storage is looked at once it is done, and
///             what it holds shared, if the owner is (<c>share_into_at</c>).
///             The callee does not know the owner, and need not: until it
///             returns, the access is its caller's alone (the law of
///             exclusivity, and the lock or the actor that keeps other threads
///             from the owner), so no other thread has looked.
///         </item>
///     </list>
///     <para>
///         Run on the code as it will be built, after the passes: nothing is
///         stored later, or through an address made later, but into a
///         coroutine's frame, which its task alone reaches. An instruction
///         the pass does not know is taken to store.
///     </para>
/// </remarks>
static class ShareStores {
    public static void Run(SilModule module) {
        foreach (var function in module.Functions) {
            new Function(function).Run();
        }
    }

    /// <summary>Builtins given an address that store no reference there.</summary>
    static bool StoresNothing(string name) =>
        SharedStorage.IsMarker(name)
        || name is "ref_is_unique" or "address_to_int" or "opaque_free" or "array_clear" or "share_into" or "share_into_at"
            // In place, in a buffer that is the only reference: an element is
            // shared into the buffer as it is stored (the code generator), and
            // an element's address is followed as one.
            or "array_set_unique" or "array_element_addr_unique" or "simd_store_unique"
        || name.StartsWith("bytes_store_unique_", StringComparison.Ordinal) || name.StartsWith("bytes_store_array_unique_", StringComparison.Ordinal);

    /// <summary>Whether an array builtin works on the place of an inline array, whose elements are where it is.</summary>
    static bool InInlineArray(BuiltinInst builtin) => builtin.Operands is [{ Type: AddressType { Pointee: FixedArrayType } }, ..];

    /// <summary>Whether a value of the type may hold an address, or a closure that does: a closure's context, the closure.</summary>
    static bool MayHold(SilType type) => MayHold(type, []);

    static bool MayHold(SilType type, HashSet<SilType> seen) =>
        type switch {
            AddressType or FunctionType or ThickFunctionType => true,
            NominalType when !seen.Add(type) => false,
            ClassType classType => classType.Fields.Exists(f => MayHold(f.Type, seen)),
            StructType structType => structType.Fields.Exists(f => MayHold(f.Type, seen)),
            EnumType enumType => enumType.Cases.Exists(c => c.Payload is { } payload && MayHold(payload, seen)),
            TupleType tuple => tuple.Elements.Any(e => MayHold(e, seen)),
            _ => type == SilType.Object || SilType.IsIndirect(type),
        };

    /// <summary>What owns the storage at an address: the object, the slot that holds the array or the box, or null for a global.</summary>
    sealed record Root(SilInstruction Address, SilValue? Owner, bool IsLet);

    sealed class Function(SilFunction function) {
        readonly Dictionary<SilInstruction, List<SilInstruction>> _before = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<SilInstruction, List<SilInstruction>> _after = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<SilBlock, List<SilInstruction>> _atStart = new(ReferenceEqualityComparer.Instance);
        Dictionary<SilValue, List<SilInstruction>>? _uses;
        Dictionary<SilBlock, HashSet<SilBlock>>? _dominators;

        public void Run() {
            foreach (var instruction in function.Blocks.ToList().SelectMany(static b => b.Instructions)) {
                var root = instruction switch {
                    RefElementAddrInst { Reference.Type: ClassType { IsCoroutineFrame: false } type } field => new Root(field, field.Reference, type.LetFields.Contains(field.Field)),
                    // An inline array's element is part of where the array is, which is followed as that.
                    BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique" } element when InInlineArray(element) => null,
                    BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique" or "box_project_unique" } element => new Root(element, element.Operands[0], false),
                    GlobalAddrInst global => new Root(global, null, false),
                    _ => null,
                };
                if (root is not null && instruction.Type is AddressType { Pointee.IsTrivial: false }) {
                    Follow(root);
                }
            }

            if (_before.Count + _after.Count + _atStart.Count == 0) {
                return;
            }

            foreach (var block in function.Blocks) {
                var made = new List<SilInstruction>(block.Instructions.Count + 8);

                void Add(IEnumerable<SilInstruction>? added, SilLocation? location) {
                    foreach (var instruction in added ?? []) {
                        instruction.Block = block;
                        instruction.Location = location;
                        made.Add(instruction);
                    }
                }

                Add(_atStart.GetValueOrDefault(block), block.Instructions[0].Location);
                foreach (var instruction in block.Instructions) {
                    Add(_before.GetValueOrDefault(instruction), instruction.Location);
                    made.Add(instruction);
                    Add(_after.GetValueOrDefault(instruction), instruction.Location);
                }

                block.Instructions.Clear();
                block.Instructions.AddRange(made);
            }
        }

        List<SilInstruction> UsesOf(SilValue value) {
            if (_uses is null) {
                _uses = new(ReferenceEqualityComparer.Instance);
                foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
                    foreach (var operand in instruction.Operands) {
                        if (!_uses.TryGetValue(operand, out var uses)) {
                            _uses[operand] = uses = [];
                        }

                        if (uses.Count == 0 || !ReferenceEquals(uses[^1], instruction)) {
                            uses.Add(instruction);
                        }
                    }
                }
            }

            return _uses.TryGetValue(value, out var found) ? found : [];
        }

        /// <summary>
        ///     Everything done through the root's address: with the address
        ///     itself, or of a part of it (direct), and through what holds it —
        ///     a closure's context, the closure, a slot either is in.
        /// </summary>
        void Follow(Root root) {
            var pending = new Stack<(SilValue Value, bool Direct)>([(root.Address, true)]);
            var seen = new HashSet<SilValue>(ReferenceEqualityComparer.Instance) { root.Address };

            void Holds(SilValue value) {
                if (MayHold(value.Type) && seen.Add(value)) {
                    pending.Push((value, false));
                }
            }

            while (pending.TryPop(out var at)) {
                var (value, direct) = at;
                foreach (var use in UsesOf(value)) {
                    switch (use) {
                        case LoadInst or LoadBorrowInst or LoadWeakInst or LoadUnownedInst when direct:
                        // A weak reference shares its object as it is made (sable_weak_entry).
                        case StoreWeakInst when direct:
                        case DestroyAddrInst or DestroyValueInst or EndBorrowInst or DeallocStackInst or ReturnInst or ThrowInst or ClassMethodInst:
                        case BuiltinInst { Name: var name } inPlace when StoresNothing(name) && !InInlineArray(inPlace):
                            break;

                        // An inline array's element (#304): a part of it, where it is.
                        case BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique" } element when direct && InInlineArray(element):
                            if (element.Type is not AddressType { Pointee.IsTrivial: true } && seen.Add(element)) {
                                pending.Push((element, true));
                            }

                            break;

                        case StoreInst store when ReferenceEquals(store.Value, value):
                            Holds(Base(store.Address));
                            break;

                        case StoreInst store when direct:
                            // Nothing else has an object just made (InitFields).
                            if (!store.Value.Type.IsTrivial && !store.IntoFresh) {
                                Before(store, root, store.Value);
                            }

                            break;

                        case StoreUnownedInst unowned when direct:
                            Before(unowned, root, unowned.Value);
                            break;

                        // Into what holds it, as it is made; else through an address it holds.
                        case StoreInst store when Base(store.Address) is AllocRefInst or AllocStackInst:
                            break;

                        case StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst or BuiltinInst { Name: "opaque_cast" or "opaque_address" or "opaque_borrow_as" } when direct:
                            if (use.Type is not AddressType { Pointee.IsTrivial: true } && seen.Add(use)) {
                                pending.Push((use, true));
                            }

                            break;

                        // A part of what holds it, or what holds that.
                        case LoadInst or LoadBorrowInst or CopyValueInst or UncheckedRefCastInst or UpcastInst or PartialApplyInst or StructInst or TupleInst or EnumInst
                            or StructExtractInst or StructTakeInst or OwnedUpcastInst or TupleExtractInst or RefElementAddrInst or StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst
                            or BuiltinInst { Name: "opaque_cast" or "opaque_address" or "opaque_borrow_as" }:
                            Holds(use);
                            break;

                        case BranchInst branch:
                            Passed(branch.Operands, branch.Target);
                            break;

                        case CondBranchInst branch:
                            Passed([..branch.TrueArguments], branch.WhenTrue);
                            Passed([..branch.FalseArguments], branch.WhenFalse);
                            break;

                        case SwitchEnumInst switched:
                            foreach (var payload in switched.Cases.SelectMany(static c => c.Target.Arguments)) {
                                Holds(payload);
                            }

                            break;

                        // A call, or a builtin, that may store there. What it
                        // returns does not hold the address: an inout parameter
                        // and a closure that does not escape are not returned.
                        default:
                            After(use, root);
                            break;
                    }
                }

                void Passed(IReadOnlyList<SilValue> passed, SilBlock target) {
                    for (var i = 0; i < passed.Count; i++) {
                        if (ReferenceEquals(passed[i], value)) {
                            Holds(target.Arguments[i]);
                        }
                    }
                }
            }
        }

        /// <summary>What the storage at an address is part of: a slot, or an object.</summary>
        static SilValue Base(SilValue address) =>
            address switch {
                StructElementAddrInst element => Base(element.Address),
                TupleElementAddrInst element => Base(element.Address),
                EnumPayloadAddrInst element => Base(element.Address),
                RefElementAddrInst field => field.Reference,
                _ => address,
            };

        void Before(SilInstruction store, Root root, SilValue value) {
            if (!_before.TryGetValue(store, out var list)) {
                _before[store] = list = [];
            }

            list.Add(root.Owner is { } owner
                ? new BuiltinInst("share_into", SilType.Unit, owner, value)
                : new BuiltinInst("share_global", SilType.Unit, value));
        }

        /// <summary>What the root's storage holds, shared once the instruction is done.</summary>
        void After(SilInstruction instruction, Root root) {
            // Nothing changes a 'let' once another thread can reach the object.
            if (root.IsLet) {
                return;
            }

            if (!Dominates(root.Address, instruction)) {
                throw new InvalidOperationException($"in {function.Name}, the address a {root.Address.Opcode} gives is used where it may not have been made");
            }

            switch (instruction) {
                case TryApplyInst call:
                    call.Normal = OwnEdge(call.Normal);
                    call.Error = OwnEdge(call.Error);
                    Add(_atStart, call.Normal);
                    Add(_atStart, call.Error);
                    break;

                case { IsTerminator: true }:
                    throw new InvalidOperationException($"in {function.Name}, the address a {root.Address.Opcode} gives is used by a {instruction.Opcode}");

                default:
                    Add(_after, instruction);
                    break;
            }

            void Add<T>(Dictionary<T, List<SilInstruction>> places, T place) where T : class {
                if (!places.TryGetValue(place, out var list)) {
                    places[place] = list = [];
                }

                if (!list.Exists(i => i.Operands.Contains(root.Address, ReferenceEqualityComparer.Instance))) {
                    list.Add(root.Owner is { } owner
                        // The type of what a box holds is known there by its metadata.
                        ? root.Address is BuiltinInst { Name: "box_project_unique", Operands: [_, var metadata] }
                            ? new BuiltinInst("share_into_at", SilType.Unit, owner, root.Address, metadata)
                            : new BuiltinInst("share_into_at", SilType.Unit, owner, root.Address)
                        : new BuiltinInst("share_global_at", SilType.Unit, root.Address));
                }
            }
        }

        /// <summary>
        ///     Where a call goes, for what is done there of it alone: the block,
        ///     or, when other code goes there too, where the address may not
        ///     have been made, one of its own on the way.
        /// </summary>
        SilBlock OwnEdge(SilBlock target) {
            if (_entered is null) {
                _entered = new(ReferenceEqualityComparer.Instance);
                foreach (var successor in function.Blocks.SelectMany(static b => b.Successors)) {
                    _entered[successor] = _entered.GetValueOrDefault(successor) + 1;
                }
            }

            if (_entered.GetValueOrDefault(target) < 2) {
                return target;
            }

            var edge = function.AddBlock();
            var passed = target.Arguments.Select(a => (SilValue)edge.AddArgument(a.Type, a.IsGuaranteed)).ToList();
            edge.Append(new BranchInst(target, [..passed]));
            _entered[edge] = 1;
            return edge;
        }

        Dictionary<SilBlock, int>? _entered;

        bool Dominates(SilInstruction first, SilInstruction then) {
            if (ReferenceEquals(first.Block, then.Block)) {
                return true;
            }

            _dominators ??= SilVerifier.Dominators(function);
            // Code that nothing reaches is not run.
            return !_dominators.TryGetValue(then.Block!, out var dominators) || dominators.Contains(first.Block!);
        }
    }
}
