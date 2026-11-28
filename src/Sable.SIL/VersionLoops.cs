namespace Sable.SIL;

/// <summary>
///     A counted loop over arrays checks its indices once, before it starts,
///     rather than at every element: the loop is versioned, a copy that
///     checks nothing run when the one check passes, the loop as written
///     otherwise.
/// </summary>
/// <remarks>
///     <para>
///         Every <c>a[i]</c> checks <c>i</c> against <c>a</c>'s count, and a
///         check that can trap in the middle of a loop is a second way out of
///         it, which stops LLVM vectorising it. In a loop
///         <c>while i &lt; n { …a[i]…; i = i + 1 }</c> — the header tests the
///         induction variable against a bound the loop does not change, every
///         way round adds one to it, and the test is the only way out — the
///         indices an access sees are <c>start</c>, <c>start + 1</c>, …,
///         <c>n - 1</c>: all in range exactly when <c>start &gt;= 0</c> and
///         <c>n &lt;= a.count</c>. An array the loop indexes must keep its
///         count the while: a value, a local or inout parameter the loop
///         only reads and writes elements of, or a field of an object from
///         outside the loop where the loop writes no field and calls nothing
///         that may.
///     </para>
///     <para>
///         An index may also be at an offset the loop does not change,
///         <c>a[from + j]</c> or <c>a[i - 1]</c>: the check before then takes
///         in the offset, and in the copy the index is worked out without an
///         overflow check, as it lies in range. The loop may have other ways
///         out than its test (a search's <c>return</c>), which only stop it
///         sooner. Such loops, and any found only once values merely handed
///         round them are gone, are versioned when that leaves no access in
///         the copy checked: otherwise the copy saves one comparison a time
///         round, which a loop of a few turns pays for with the check before.
///     </para>
///     <para>
///         So the loop is copied, and in the copy those accesses are
///         <c>[in_bounds]</c>, unchecked; before the loop, one test of the
///         start and of each array's count picks the copy or the original.
///         The original, still checking, is what runs when the test fails,
///         so a program that indexes out of range traps where, and with
///         what, it always did — after whatever the loop did first.
///     </para>
///     <para>
///         Integer overflow is the other way out of a loop. In the copy of a
///         loop whose body is one block, it is checked after the loop instead
///         (DeferOverflow), so that sums, differences and element-wise
///         arithmetic over Int arrays vectorise too.
///     </para>
///     <para>
///         Values the loop works out and the code after it uses (a sum, an
///         index found) leave either version through a block that takes them
///         as arguments; only trivial ones are allowed, so that nothing owned
///         has two ways out.
///     </para>
/// </remarks>
public sealed class VersionLoops : ISilPass {
    public string Name => "version-loops";

    /// <summary>The most instructions a loop may have to be copied: a copy is all code size, and a big body gains little from vectorising.</summary>
    const int Limit = 200;

    /// <summary>The fewest turns for which a loop whose overflow is checked after it runs as the copy rather than as written.</summary>
    const long ShortLoop = 16;

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<SilModule, Dictionary<string, bool>> _writes = new();

    public void Run(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return;
        }

        // Whether a loop's body may write an object's field: the summaries of what each function may write, made once a module.
        Dictionary<string, bool>? writes = null;
        bool MayWrite(HashSet<SilBlock> body) =>
            function.Module is not { } module
            || BorrowFields.MayWrite(module, body.SelectMany(static b => b.Instructions), writes ??= _writes.GetValue(module, BorrowFields.Summarise), ignoring: null);

        // One loop at a time, the analysis made again after each, as each copy changes the function.
        var done = new HashSet<SilBlock>();
        var versioned = new HashSet<SilBlock>();
        while (Find(function, done, MayWrite, strict: false) is { } loop) {
            versioned.Add(loop.Header);
            versioned.Add(Version(function, loop));
        }

        // A value an inlined loop only hands round (the caller's, passed in and back unchanged) is that value; with
        // such values gone, the code after a loop may no longer seem to use what the loop works out. Those loops are
        // looked at again: those read at an offset or from a field, or with another way out, are versioned when
        // that leaves no access in them checked.
        var simplified = false;
        while (Simplify.SimplifyArguments(function)) {
            simplified = true;
        }

        if (simplified) {
            while (Find(function, versioned, MayWrite, strict: true) is { } loop) {
                versioned.Add(Version(function, loop));
            }
        }
    }

    /// <summary>The accesses that check their index unless they are <c>[in_bounds]</c>.</summary>
    static bool Checked(SilInstruction instruction) =>
        instruction is BuiltinInst {
            InBounds: false,
            Name: "array_get" or "array_get_borrowed" or "array_set" or "array_set_unique" or "array_element_addr" or "array_element_addr_unique",
        };

    /// <summary>A counted loop to version: what was found about it.</summary>
    sealed record Loop(
        SilBlock Header,
        HashSet<SilBlock> Body,
        SilBlock Preheader,
        CondBranchInst Test,
        SilArgument Index,
        SilValue Bound,
        List<BuiltinInst> Accesses,
        List<(SilValue Array, SilValue? Offset, bool Below)> Arrays,
        List<BuiltinInst> Offsets,
        List<SilValue> LiveOut);

    static Loop? Find(SilFunction function, HashSet<SilBlock> done, Func<HashSet<SilBlock>, bool> mayWrite, bool strict) {
        var dominators = SilVerifier.Dominators(function);
        var predecessors = function.Blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in function.Blocks) {
            foreach (var successor in block.Successors) {
                if (predecessors.TryGetValue(successor, out var list)) {
                    list.Add(block);
                }
            }
        }

        // Natural loops, innermost (smallest) first: a header, and every
        // block that reaches a back edge into it without passing it.
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

        foreach (var (header, body) in loops.OrderBy(static l => l.Value.Count)) {
            if (done.Add(header) && Counted(header, body, predecessors, dominators, mayWrite, strict) is { } loop) {
                return loop;
            }
        }

        return null;
    }

    /// <summary>
    ///     The loop, if it has the shape this pass versions and an access to
    ///     check once; null otherwise. A loop with another way out, an array
    ///     read from a field or an access at an offset — or any, when
    ///     <paramref name="strict" /> — is versioned only when that leaves it no
    ///     access that checks: else its copy saves a comparison a time round,
    ///     which a short loop pays for with the check before it.
    /// </summary>
    static Loop? Counted(
        SilBlock header,
        HashSet<SilBlock> body,
        Dictionary<SilBlock, List<SilBlock>> predecessors,
        Dictionary<SilBlock, HashSet<SilBlock>> dominators,
        Func<HashSet<SilBlock>, bool> mayWrite,
        bool strict
    ) {
        if (body.Sum(static b => b.Instructions.Count) > Limit) {
            return null;
        }

        // One way in, a plain branch from outside.
        if (predecessors[header].Where(p => !body.Contains(p)).ToList() is not [var preheader] || preheader.Terminator is not BranchInst) {
            return null;
        }

        // The header tests 'i < n'; what it leads into is reached from it
        // alone, and leads to all the rest. Other ways out (a search's
        // 'return' when it finds) only stop the loop sooner.
        if (header.Terminator is not CondBranchInst { Condition: BuiltinInst { Name: "cmp_lt_Int" } less } test || less.Block != header
            || less.Operands[0] is not SilArgument index || index.Block != header
            || !body.Contains(test.WhenTrue) || body.Contains(test.WhenFalse) || test.TrueArgumentCount != 0
            || predecessors[test.WhenTrue].Count != 1
            || body.Any(b => b != header && !dominators[b].Contains(test.WhenTrue))) {
            return null;
        }

        // Such a way out is taken from either copy into the same block, so nothing
        // the body owns or borrows may be live across it: every value that is not
        // trivial ends in the block that makes it, and none is handed out.
        var exits = body.Where(b => b != header).SelectMany(b => b.Successors.Where(s => !body.Contains(s))).ToList();
        if (exits.Count > 0) {
            var crossing = header.Function.Blocks
                .SelectMany(static b => b.Instructions)
                .SelectMany(static u => u.Operands.OfType<SilInstruction>().Where(o => o.Block != u.Block))
                .ToHashSet();
            if (exits.Any(static e => e.Arguments.Any(static a => !a.Type.IsTrivial))
                || body.Any(b => b.Arguments.Any(static a => !a.Type.IsTrivial)
                    || b.Instructions.Any(i => i.HasResult && !i.Type.IsTrivial && i.Type is not AddressType && crossing.Contains(i)))) {
                return null;
            }
        }

        // What leaves the loop as an argument of the exit is left alone when it is not trivial.
        if (test.WhenFalse.Arguments.Any(static a => !a.Type.IsTrivial)) {
            return null;
        }

        // The induction variable: one more every way round.
        var position = header.Arguments.IndexOf(index);
        foreach (var latch in predecessors[header].Where(body.Contains)) {
            if (latch.Terminator is not BranchInst back
                || back.Operands[position] is not BuiltinInst { Name: "add_Int" } step
                || !ReferenceEquals(step.Operands[0], index)
                || step.Operands[1] is not IntegerLiteralInst { WideValue: var one } || one != 1) {
                return null;
            }
        }

        // Whether the body may write a field, asked once, and only of a loop that reads one.
        bool? writes = null;
        bool Writes() => writes ??= mayWrite(body);

        // The bound: from outside the loop, or the count of an array the loop does not resize.
        var bound = less.Operands[1];
        if (Inside(bound, body)) {
            if (bound is not BuiltinInst { Name: "array_count" } count || ArrayOf(count.Operands[0], body, Writes) is null) {
                return null;
            }
        }

        // The accesses at the index, or at an offset from it the loop does not change ('a[from + j]', 'a[i - 1]').
        var accesses = new List<BuiltinInst>();
        var arrays = new List<(SilValue Array, SilValue? Offset, bool Below)>();
        var offsets = new List<BuiltinInst>();
        var extended = exits.Count > 0;
        foreach (var block in body.Where(b => b != header)) {
            foreach (var instruction in block.Instructions) {
                if (instruction is not BuiltinInst { InBounds: false } access || access.Operands.Count < 2) {
                    continue;
                }

                SilValue? offset = null;
                var below = false;
                if (access.Operands[1] is BuiltinInst { Name: "add_Int" } sum
                    && (ReferenceEquals(sum.Operands[0], index) || ReferenceEquals(sum.Operands[1], index))
                    && sum.Operands[ReferenceEquals(sum.Operands[0], index) ? 1 : 0] is var other && !Inside(other, body)) {
                    offset = other;
                } else if (access.Operands[1] is BuiltinInst { Name: "sub_Int" } difference
                    && ReferenceEquals(difference.Operands[0], index) && !Inside(difference.Operands[1], body)) {
                    offset = difference.Operands[1];
                    below = true;
                } else if (!ReferenceEquals(access.Operands[1], index)) {
                    continue;
                }

                var array = access.Name switch {
                    "array_get" or "array_get_borrowed" => ArrayOf(access.Operands[0], body, Writes),
                    "array_set_unique" => Unresized(access.Operands[0], body) ? access.Operands[0] : null,
                    _ => null,
                };

                if (array is null) {
                    continue;
                }

                accesses.Add(access);
                if (offset is not null && !offsets.Contains((BuiltinInst)access.Operands[1])) {
                    offsets.Add((BuiltinInst)access.Operands[1]);
                }

                extended |= offset is not null || array is RefElementAddrInst;
                if (!arrays.Any(a => Same(a.Array, array) && ReferenceEquals(a.Offset, offset) && a.Below == below)) {
                    arrays.Add((array, offset, below));
                }
            }
        }

        // Found only once handed-round values are gone, a loop of the shape versioned before (none of the
        // above) or one that calls is left as it is: a short tail after a vector loop would pay more for the
        // check before than it saves, and a call costs more than a comparison.
        if (accesses.Count == 0
            || strict && !extended
            || (strict || extended) && body.SelectMany(static b => b.Instructions).Any(i => Checked(i) && !accesses.Contains((BuiltinInst)i))
            || strict && body.SelectMany(static b => b.Instructions).Any(static i => i is ApplyInst or TryApplyInst)) {
            return null;
        }

        // What the code after the loop uses of what the loop works out.
        var liveOut = new List<SilValue>();
        var outside = header.Function.Blocks.Where(b => !body.Contains(b)).SelectMany(static b => b.Instructions).ToList();
        foreach (var block in body) {
            foreach (var value in block.Arguments.Cast<SilValue>().Concat(block.Instructions.Where(static i => i.HasResult))) {
                if (outside.Any(user => user.Operands.Any(o => ReferenceEquals(o, value)))) {
                    if (!value.Type.IsTrivial) {
                        return null;
                    }

                    liveOut.Add(value);
                }
            }
        }

        // What the loop works out leaves it by the header alone.
        if (liveOut.Count > 0 && exits.Count > 0) {
            return null;
        }

        return new(header, body, preheader, test, index, bound, accesses, arrays, offsets, liveOut);
    }

    static bool Inside(SilValue value, HashSet<SilBlock> body) =>
        value switch {
            SilInstruction instruction => body.Contains(instruction.Block),
            SilArgument argument => body.Contains(argument.Block),
            _ => false,
        };

    /// <summary>
    ///     What an array value read in the loop is, as something whose count
    ///     can be read before it: the value itself when it is made outside the
    ///     loop; the address it is borrowed from when that is a local or an
    ///     inout parameter the loop does not resize, or a field of an object
    ///     from outside the loop when the loop writes no field and calls
    ///     nothing that may (BorrowFields.MayWrite). Null for anything else.
    /// </summary>
    static SilValue? ArrayOf(SilValue array, HashSet<SilBlock> body, Func<bool> writes) =>
        !Inside(array, body) ? array
        : array is LoadBorrowInst { Address: var address } && Unresized(address, body) ? address
        : array is LoadBorrowInst { Address: RefElementAddrInst field } && !Inside(field.Reference, body) && !writes() ? field
        : null;

    /// <summary>Whether two arrays <see cref="ArrayOf" /> found are one: the same value, or the same field of the same object.</summary>
    static bool Same(SilValue a, SilValue b) =>
        ReferenceEquals(a, b)
        || a is RefElementAddrInst left && b is RefElementAddrInst right && left.Field == right.Field && ReferenceEquals(left.Reference, right.Reference);

    /// <summary>
    ///     A local slot or inout parameter that, inside the loop, is only
    ///     borrowed to be read and counted, and has elements written: nothing
    ///     there can change its count.
    /// </summary>
    static bool Unresized(SilValue address, HashSet<SilBlock> body) {
        if (Inside(address, body) || address is not (AllocStackInst or SilArgument { Type: AddressType })) {
            return false;
        }

        foreach (var block in body) {
            foreach (var user in block.Instructions) {
                if (!user.Operands.Any(o => ReferenceEquals(o, address))) {
                    continue;
                }

                switch (user) {
                    case BuiltinInst { Name: "array_set" or "array_set_unique" } write when ReferenceEquals(write.Operands[0], address)
                        && !write.Operands.Skip(1).Any(o => ReferenceEquals(o, address)):
                    case BuiltinInst marker when SharedStorage.IsMarker(marker.Name):
                        break;

                    case LoadBorrowInst borrowed:
                        foreach (var reader in body.SelectMany(static b => b.Instructions).Where(i => i.Operands.Any(o => ReferenceEquals(o, borrowed)))) {
                            if (reader is not (EndBorrowInst or BuiltinInst { Name: "array_get" or "array_get_borrowed" or "array_count" })
                                || !ReferenceEquals(reader.Operands[0], borrowed)) {
                                return false;
                            }
                        }

                        break;

                    default:
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     In the checked-once copy of a loop of one block after its header,
    ///     the additions, subtractions and multiplications of Ints and of the
    ///     other signed integers up to 64 bits (<see cref="Deferrable" />) check for
    ///     overflow after the loop rather than in it, so that their traps are
    ///     no way out of it either. Each wraps instead, and what the loop as
    ///     written would have done is worked out on the way out.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An operation on values of one iteration keeps the first
    ///         iteration it overflowed in: a running minimum, which vectorises.
    ///         On the way out the earliest traps, an earlier operation in the
    ///         body first where two overflowed in one iteration: the trap the
    ///         loop as written would have stopped at, with its message and
    ///         place. What the copy did after it (stores of wrapped values)
    ///         nothing can see, as the program stops; so the body may do
    ///         nothing else that is seen, or that traps: no call, no other
    ///         store, no unchecked index.
    ///     </para>
    ///     <para>
    ///         A running total, <c>sum = sum + x</c>, overflows on a partial
    ///         sum, which a vectorised loop does not have. In a loop that reads
    ///         and writes nothing, the copy instead notes any term of 2^30 or
    ///         more either way; when there is none, the loop ran fewer than
    ///         2^31 times and the total started below 2^62, no partial sum can
    ///         have overflowed. Of a total of n bits the same holds with
    ///         2^(n/2-2), 2^(n/2-1) and 2^(n-2): 2^14, 2^15 and 2^30 of an I32.
    ///         Otherwise, or when anything overflowed, the
    ///         loop as written runs again from its start: it has done nothing
    ///         to undo, and finds the overflow, if there is one, as it always did.
    ///     </para>
    /// </remarks>
    /// <returns>What the preheader passes the header's new arguments; empty when nothing changed.</returns>
    static List<SilValue> DeferOverflow(
        SilFunction function,
        SilBlock header,
        SilBlock block,
        SilArgument index,
        SilBlock original,
        IReadOnlyList<SilValue> entry,
        SilValue start,
        SilValue limit,
        Func<SilInstruction, SilValue> before
    ) {
        if (block.Terminator is not BranchInst { Target: var target } latch || target != header) {
            return [];
        }

        var sites = new List<BuiltinInst>();
        var writes = false;
        foreach (var instruction in header.Instructions.Concat(block.Instructions)) {
            switch (instruction) {
                // The induction step: one more than an index below the bound, which cannot overflow.
                case BuiltinInst { Name: "add_Int" } step when ReferenceEquals(step.Operands[0], index) && step.Operands[1] is IntegerLiteralInst:
                    break;
                case BuiltinInst site when instruction.Block == block && Deferrable(site.Name) is not null:
                    sites.Add(site);
                    break;
                // An exclusivity check is left out too: it can trap, and must not after an overflow.
                case BuiltinInst { InBounds: true, Name: "array_set_unique" } write when ArrayType.ElementOf(((AddressType)write.Operands[0].Type).Pointee)!.IsTrivial:
                    writes = true;
                    break;
                case BuiltinInst { InBounds: true, Name: "array_get" or "array_get_borrowed" } read when read.Type.IsTrivial:
                case BuiltinInst { Name: "array_count" }:
                case BuiltinInst builtin when Simplify.Pure(builtin.Name):
                case IntegerLiteralInst or FloatLiteralInst or StructExtractInst or TupleExtractInst or LoadBorrowInst or EndBorrowInst:
                case LoadInst { Kind: LoadKind.Trivial }:
                case BranchInst or CondBranchInst:
                    break;
                default:
                    return [];
            }
        }

        // Running totals: an addition or subtraction of a header argument
        // that only it uses in the loop, handed round as that argument's next
        // value; the header may hand it on out of the loop.
        var totals = new Dictionary<BuiltinInst, SilArgument>();
        if (!writes && header.Arguments.All(static a => a.Type.IsTrivial)) {
            foreach (var site in sites.Where(static s => Deferrable(s.Name)!.Value.Op is "add" or "sub")) {
                foreach (var side in Deferrable(site.Name)!.Value.Op == "add" ? [0, 1] : new[] { 0 }) {
                    if (site.Operands[side] is SilArgument total && total.Block == header && !ReferenceEquals(total, index)
                        && ReferenceEquals(latch.Operands[header.Arguments.IndexOf(total)], site)
                        && header.Instructions.SkipLast(1).Concat(block.Instructions).Count(i => i.Operands.Any(o => ReferenceEquals(o, total))) == 1) {
                        totals[site] = total;
                        break;
                    }
                }
            }
        }

        if (sites.Count == 0) {
            return [];
        }

        var initial = new List<SilValue>();
        var next = new List<SilValue>();

        // Per operation of one iteration, the first iteration it overflowed in, carried round the loop.
        var firsts = new List<(BuiltinInst Site, SilArgument First)>();
        foreach (var site in sites.Where(s => !totals.ContainsKey(s))) {
            var first = header.AddArgument(SilType.Int);
            firsts.Add((site, first));
            initial.Add(before(new IntegerLiteralInst(SilType.Int, long.MaxValue)));
            var overflow = new BuiltinInst($"overflow_at_{site.Name}", SilType.Int, site.Operands[0], site.Operands[1], index, first) {
                Block = block, Location = site.Location, InlinedSite = site.InlinedSite,
            };
            block.Instructions.Insert(block.Instructions.IndexOf(site), overflow);
            next.Add(overflow);
        }

        // For the running totals, whether any term was large.
        SilArgument? large = null;
        if (totals.Count > 0) {
            large = header.AddArgument(SilType.Bool);
            initial.Add(before(new IntegerLiteralInst(SilType.Bool, 0)));
            SilValue any = large;
            // A term's bound, either way, for each type of total: 2^30 of an Int, 2^14 of an I32.
            var bounds = new Dictionary<SilType, (IntegerLiteralInst Above, IntegerLiteralInst Below)>();
            foreach (var type in totals.Values.Select(static t => t.Type).Distinct()) {
                var term = 1L << (Bits(type) / 2 - 2);
                var bound = new IntegerLiteralInst(type, term) { Block = block };
                var negative = new IntegerLiteralInst(type, -term) { Block = block };
                block.Instructions.InsertRange(0, [bound, negative]);
                bounds[type] = (bound, negative);
            }

            foreach (var (site, total) in totals) {
                var term = ReferenceEquals(site.Operands[0], total) ? site.Operands[1] : site.Operands[0];
                var at = block.Instructions.IndexOf(site);
                var (bound, negative) = bounds[total.Type];
                var above = new BuiltinInst($"cmp_ge_{total.Type}", SilType.Bool, term, bound) { Block = block, Location = site.Location };
                var below = new BuiltinInst($"cmp_le_{total.Type}", SilType.Bool, term, negative) { Block = block, Location = site.Location };
                var either = new BuiltinInst("or_Bool", SilType.Bool, above, below) { Block = block, Location = site.Location };
                var anyNext = new BuiltinInst("or_Bool", SilType.Bool, any, either) { Block = block, Location = site.Location };
                // After the addition, not before: the vectorised body then adds
                // each register in before testing it, and keeps fewer live at once
                // (arm64's Int sum spilled four registers the other way).
                block.Instructions.InsertRange(at + 1, [above, below, either, anyNext]);
                any = anyNext;
            }

            next.Add(any);
        }

        foreach (var site in sites) {
            site.Rename(SilBuiltins.WrappingPrefix + site.Name);
        }

        block.Instructions[^1] = new BranchInst(header, [..latch.Operands, ..next]) { Block = block, Location = latch.Location };

        // On the way out, before anything else, what the loop as written would have done.
        var test = (CondBranchInst)header.Terminator!;
        var check = function.AddBlock();
        T Add<T>(T instruction) where T : SilInstruction {
            instruction.Location ??= test.Location;
            return check.Append(instruction);
        }

        var none = Add(new IntegerLiteralInst(SilType.Int, long.MaxValue));
        if (large is null) {
            // Every overflow of one iteration: the earliest traps.
            SilValue earliest = firsts[0].First;
            foreach (var (_, first) in firsts.Skip(1)) {
                earliest = Add(new BuiltinInst("min_Int", SilType.Int, earliest, first));
            }

            var overflowed = Add(new BuiltinInst("cmp_ne_Int", SilType.Bool, earliest, none));
            foreach (var (site, first) in firsts) {
                var here = Add(new BuiltinInst("cmp_eq_Int", SilType.Bool, first, earliest) { Location = site.Location });
                var fails = Add(new BuiltinInst("and_Bool", SilType.Bool, overflowed, here) { Location = site.Location });
                Add(new CondFailInst(fails, "integer overflow") { Location = site.Location, InlinedSite = site.InlinedSite });
            }

            Add(new BranchInst(test.WhenFalse, [..test.FalseArguments]));
        } else {
            // Anything that might have overflowed: the loop as written, again.
            SilValue again = large;
            foreach (var (_, first) in firsts) {
                again = Add(new BuiltinInst("or_Bool", SilType.Bool, again, Add(new BuiltinInst("cmp_ne_Int", SilType.Bool, first, none))));
            }

            // Fewer turns than 2^31 for an Int total, 2^15 for an I32, and a start within 2^62 or 2^30 either way.
            var trips = Add(new BuiltinInst("wrapping_sub_Int", SilType.Int, limit, start));
            var turns = 1L << (totals.Values.Min(static t => Bits(t.Type)) / 2 - 1);
            var many = Add(new BuiltinInst("cmp_ge_Int", SilType.Bool, trips, Add(new IntegerLiteralInst(SilType.Int, turns))));
            again = Add(new BuiltinInst("or_Bool", SilType.Bool, again, many));
            foreach (var total in totals.Values) {
                var from = entry[header.Arguments.IndexOf(total)];
                var reach = 1L << (Bits(total.Type) - 2);
                var high = Add(new IntegerLiteralInst(total.Type, reach));
                var low = Add(new IntegerLiteralInst(total.Type, -reach));
                var above = Add(new BuiltinInst($"cmp_ge_{total.Type}", SilType.Bool, from, high));
                var below = Add(new BuiltinInst($"cmp_le_{total.Type}", SilType.Bool, from, low));
                again = Add(new BuiltinInst("or_Bool", SilType.Bool, again, Add(new BuiltinInst("or_Bool", SilType.Bool, above, below))));
            }

            Add(new CondBranchInst(again, original, [..entry], test.WhenFalse, [..test.FalseArguments]));
        }

        header.Instructions[^1] = new CondBranchInst(test.Condition, test.WhenTrue, [..test.TrueArguments], check, []) { Block = header, Location = test.Location };
        return initial;
    }

    /// <summary>
    ///     The checked additions, subtractions and multiplications whose
    ///     overflow can be checked after the loop: of a signed integer of up
    ///     to 64 bits, Int's or I32's.
    /// </summary>
    internal static (string Op, SilType Type)? Deferrable(string name) {
        if (name.Length < 5 || name[3] != '_' || name[..3] is not ("add" or "sub" or "mul")) {
            return null;
        }

        var type = SilType.Integers.FirstOrDefault(t => t.ToString() == name[4..]);
        return type is not null && SilType.IntegerInfo(type) is (<= 64, true) ? (name[..3], type) : null;
    }

    static int Bits(SilType type) => SilType.IntegerInfo(type)!.Value.Bits;

    /// <returns>The copy's header.</returns>
    static SilBlock Version(SilFunction function, Loop loop) {
        var (header, body, preheader, test, index, bound, accesses, arrays, offsets, liveOut) = loop;

        // The copy, block by block, in the order of the function.
        var blocks = new Dictionary<SilBlock, SilBlock>();
        var values = new Dictionary<SilValue, SilValue>();
        var originals = function.Blocks.Where(body.Contains).ToList();
        foreach (var original in originals) {
            var copy = function.AddBlock();
            blocks[original] = copy;
            foreach (var argument in original.Arguments) {
                values[argument] = copy.AddArgument(argument.Type, argument.IsGuaranteed);
            }
        }

        var copied = new List<SilInstruction>();
        foreach (var original in originals) {
            foreach (var instruction in original.Instructions) {
                var copy = instruction.Clone(static v => v, b => blocks.GetValueOrDefault(b) ?? b);
                copy.InlinedSite = instruction.InlinedSite;
                if (copy is BuiltinInst builtin && instruction is BuiltinInst access && accesses.Contains(access)) {
                    builtin.InBounds = true;
                }

                // An index at an offset is in range there, so cannot have overflowed.
                if (copy is BuiltinInst added && instruction is BuiltinInst offset && offsets.Contains(offset)) {
                    added.Rename(offset.Name == "add_Int" ? "wrapping_add_Int" : "wrapping_sub_Int");
                }

                values[instruction] = copy;
                blocks[original].Append(copy);
                copied.Add(copy);
            }
        }

        foreach (var instruction in copied) {
            for (var i = 0; i < instruction.Operands.Count; i++) {
                if (values.TryGetValue(instruction.Operands[i], out var replacement)) {
                    instruction.SetOperand(i, replacement);
                }
            }
        }

        // Both ways out meet in a block that takes, besides the exit's own
        // arguments, what the code after uses of the loop's work.
        if (liveOut.Count > 0) {
            var exit = test.WhenFalse;
            var join = function.AddBlock();
            var passed = exit.Arguments.Select(a => (SilValue)join.AddArgument(a.Type, a.IsGuaranteed)).ToList();
            var results = liveOut.Select(v => (SilValue)join.AddArgument(v.Type)).ToList();
            join.Append(new BranchInst(exit, [..passed]) { Location = test.Location });

            var users = function.Blocks.Where(b => !body.Contains(b) && !blocks.ContainsValue(b) && b != join).SelectMany(static b => b.Instructions).ToList();
            foreach (var user in users) {
                for (var i = 0; i < user.Operands.Count; i++) {
                    if (liveOut.IndexOf(user.Operands[i]) is >= 0 and var which) {
                        user.SetOperand(i, results[which]);
                    }
                }
            }

            foreach (var (from, map) in new[] { (header, (Func<SilValue, SilValue>)(static v => v)), (blocks[header], v => values.GetValueOrDefault(v) ?? v) }) {
                var branch = (CondBranchInst)from.Terminator!;
                var rebuilt = new CondBranchInst(
                    branch.Condition,
                    branch.WhenTrue,
                    [..branch.TrueArguments],
                    join,
                    [..branch.FalseArguments, ..liveOut.Select(map)]
                ) { Block = from, Location = branch.Location };
                from.Instructions[^1] = rebuilt;
            }
        }

        // Before the loop: the one check that picks the copy.
        var entry = (BranchInst)preheader.Terminator!;
        var start = entry.Operands[header.Arguments.IndexOf(index)];
        var at = entry.Location;
        var checks = new List<SilInstruction>();
        var zero = Add(new IntegerLiteralInst(SilType.Int, 0));
        SilValue ok = Add(new BuiltinInst("cmp_ge_Int", SilType.Bool, start, zero));
        var counts = new List<(SilValue Array, SilValue Count)>();
        var limit = bound;
        if (Inside(bound, body)) {
            var counted = ArrayOf(((BuiltinInst)bound).Operands[0], body, static () => false)!;
            limit = CountOf(counted);
            counts.Add((counted, limit));
        }

        foreach (var (array, offset, below) in arrays) {
            if (counts.FirstOrDefault(c => Same(c.Array, array)).Count is not { } count) {
                count = CountOf(array);
                counts.Add((array, count));
            }

            // At an offset above: 'offset >= 0' and 'n <= count - offset', which with 'start >= 0' puts every
            // index in range, and leaves no room for 'i + offset' to overflow. Below: 'offset >= 0',
            // 'start >= offset' and 'n <= count', and 'i - offset' lies between 0 and 'i'.
            if (offset is not null) {
                ok = Add(new BuiltinInst("and_Bool", SilType.Bool, ok, Add(new BuiltinInst("cmp_ge_Int", SilType.Bool, offset, zero))));
                if (below) {
                    ok = Add(new BuiltinInst("and_Bool", SilType.Bool, ok, Add(new BuiltinInst("cmp_ge_Int", SilType.Bool, start, offset))));
                } else {
                    count = Add(new BuiltinInst("wrapping_sub_Int", SilType.Int, count, offset));
                }
            }

            var fits = Add(new BuiltinInst("cmp_le_Int", SilType.Bool, limit, count));
            ok = Add(new BuiltinInst("and_Bool", SilType.Bool, ok, fits));
        }

        List<SilValue> deferred = body.Count == 2
            ? DeferOverflow(function, blocks[header], blocks[test.WhenTrue], (SilArgument)values[index], header, entry.Operands, start, limit, i => Add(i))
            : [];

        // A loop of a few turns runs as written: the copy whose overflow is
        // checked after it pays for that check on the way out however few
        // turns it makes, and a short one has nothing to vectorise (narrowSum's
        // tail, of fewer than 16 I32s and often none, 200,000 times a run).
        if (deferred.Count > 0) {
            var trips = Add(new BuiltinInst("wrapping_sub_Int", SilType.Int, limit, start));
            var many = Add(new BuiltinInst("cmp_ge_Int", SilType.Bool, trips, Add(new IntegerLiteralInst(SilType.Int, ShortLoop))));
            ok = Add(new BuiltinInst("and_Bool", SilType.Bool, ok, many));
        }

        preheader.Instructions.InsertRange(preheader.Instructions.Count - 1, checks);
        preheader.Instructions[^1] = new CondBranchInst(
            ok,
            blocks[header],
            [..entry.Operands, ..deferred],
            header,
            [..entry.Operands]
        ) { Block = preheader, Location = at };
        return blocks[header];

        SilValue CountOf(SilValue array) {
            // A field read in the loop is read again before it, of the same object.
            if (array is RefElementAddrInst field && Inside(field, body)) {
                array = Add(new RefElementAddrInst(field.Reference, field.Field));
            }

            if (array.Type is not AddressType) {
                return Add(new BuiltinInst("array_count", SilType.Int, array));
            }

            var borrowed = Add(new LoadBorrowInst(array));
            var count = Add(new BuiltinInst("array_count", SilType.Int, borrowed));
            Add(new EndBorrowInst(borrowed));
            return count;
        }

        T Add<T>(T instruction) where T : SilInstruction {
            instruction.Block = preheader;
            instruction.Location = at;
            checks.Add(instruction);
            return instruction;
        }
    }
}
