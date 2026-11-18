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
///         count the while: a value, or a local or inout parameter the loop
///         only reads and writes elements of.
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

    public void Run(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return;
        }

        // One loop at a time, the analysis made again after each, as each copy changes the function.
        var done = new HashSet<SilBlock>();
        while (Find(function, done) is { } loop) {
            Version(function, loop);
        }
    }

    /// <summary>A counted loop to version: what was found about it.</summary>
    sealed record Loop(
        SilBlock Header,
        HashSet<SilBlock> Body,
        SilBlock Preheader,
        CondBranchInst Test,
        SilArgument Index,
        SilValue Bound,
        List<BuiltinInst> Accesses,
        List<SilValue> Arrays,
        List<SilValue> LiveOut);

    static Loop? Find(SilFunction function, HashSet<SilBlock> done) {
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
            if (done.Add(header) && Counted(header, body, predecessors, dominators) is { } loop) {
                return loop;
            }
        }

        return null;
    }

    /// <summary>The loop, if it has the shape this pass versions and an access to check once; null otherwise.</summary>
    static Loop? Counted(
        SilBlock header,
        HashSet<SilBlock> body,
        Dictionary<SilBlock, List<SilBlock>> predecessors,
        Dictionary<SilBlock, HashSet<SilBlock>> dominators
    ) {
        if (body.Sum(static b => b.Instructions.Count) > Limit) {
            return null;
        }

        // One way in, a plain branch from outside.
        if (predecessors[header].Where(p => !body.Contains(p)).ToList() is not [var preheader] || preheader.Terminator is not BranchInst) {
            return null;
        }

        // The header tests 'i < n' and is the only way out; what it leads
        // into is reached from it alone, and leads to all the rest.
        if (header.Terminator is not CondBranchInst { Condition: BuiltinInst { Name: "cmp_lt_Int" } less } test || less.Block != header
            || less.Operands[0] is not SilArgument index || index.Block != header
            || !body.Contains(test.WhenTrue) || body.Contains(test.WhenFalse) || test.TrueArgumentCount != 0
            || predecessors[test.WhenTrue].Count != 1
            || body.Any(b => b != header && !dominators[b].Contains(test.WhenTrue))
            || body.Any(b => b.Successors.Any(s => !body.Contains(s) && b != header))) {
            return null;
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

        // The bound: from outside the loop, or the count of an array the loop does not resize.
        var bound = less.Operands[1];
        if (Inside(bound, body)) {
            if (bound is not BuiltinInst { Name: "array_count" } count || ArrayOf(count.Operands[0], body) is null) {
                return null;
            }
        }

        var accesses = new List<BuiltinInst>();
        var arrays = new List<SilValue>();
        foreach (var block in body.Where(b => b != header)) {
            foreach (var instruction in block.Instructions) {
                if (instruction is not BuiltinInst { InBounds: false } access || access.Operands.Count < 2 || !ReferenceEquals(access.Operands[1], index)) {
                    continue;
                }

                var array = access.Name switch {
                    "array_get" or "array_get_borrowed" => ArrayOf(access.Operands[0], body),
                    "array_set_unique" => Unresized(access.Operands[0], body) ? access.Operands[0] : null,
                    _ => null,
                };

                if (array is null) {
                    continue;
                }

                accesses.Add(access);
                if (!arrays.Contains(array)) {
                    arrays.Add(array);
                }
            }
        }

        if (accesses.Count == 0) {
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

        return new(header, body, preheader, test, index, bound, accesses, arrays, liveOut);
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
    ///     inout parameter the loop does not resize. Null for anything else.
    /// </summary>
    static SilValue? ArrayOf(SilValue array, HashSet<SilBlock> body) =>
        !Inside(array, body) ? array
        : array is LoadBorrowInst { Address: var address } && Unresized(address, body) ? address
        : null;

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
    ///     the Int additions, subtractions and multiplications check for
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
    ///         have overflowed. Otherwise, or when anything overflowed, the
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
                case BuiltinInst { Name: "add_Int" or "sub_Int" or "mul_Int" } site when instruction.Block == block:
                    sites.Add(site);
                    break;
                // An exclusivity check is left out too: it can trap, and must not after an overflow.
                case BuiltinInst { InBounds: true, Name: "array_set_unique" } write when ((ArrayType)((AddressType)write.Operands[0].Type).Pointee).Element.IsTrivial:
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
            foreach (var site in sites.Where(static s => s.Name is "add_Int" or "sub_Int")) {
                foreach (var side in site.Name == "add_Int" ? [0, 1] : new[] { 0 }) {
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
            var overflow = new BuiltinInst($"overflow_at_{site.Name[..3]}_Int", SilType.Int, site.Operands[0], site.Operands[1], index, first) {
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
            var bound = block.Append(new IntegerLiteralInst(SilType.Int, 1L << 30));
            var negative = block.Append(new IntegerLiteralInst(SilType.Int, -(1L << 30)));
            block.Instructions.Remove(bound);
            block.Instructions.Remove(negative);
            block.Instructions.Insert(0, bound);
            block.Instructions.Insert(1, negative);
            foreach (var (site, total) in totals) {
                var term = ReferenceEquals(site.Operands[0], total) ? site.Operands[1] : site.Operands[0];
                var at = block.Instructions.IndexOf(site);
                var above = new BuiltinInst("cmp_ge_Int", SilType.Bool, term, bound) { Block = block, Location = site.Location };
                var below = new BuiltinInst("cmp_le_Int", SilType.Bool, term, negative) { Block = block, Location = site.Location };
                var either = new BuiltinInst("or_Bool", SilType.Bool, above, below) { Block = block, Location = site.Location };
                var anyNext = new BuiltinInst("or_Bool", SilType.Bool, any, either) { Block = block, Location = site.Location };
                block.Instructions.InsertRange(at, [above, below, either, anyNext]);
                any = anyNext;
            }

            next.Add(any);
        }

        foreach (var site in sites) {
            site.Rename($"wrapping_{site.Name[..3]}_Int");
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

            var trips = Add(new BuiltinInst("wrapping_sub_Int", SilType.Int, limit, start));
            var many = Add(new BuiltinInst("cmp_ge_Int", SilType.Bool, trips, Add(new IntegerLiteralInst(SilType.Int, 1L << 31))));
            again = Add(new BuiltinInst("or_Bool", SilType.Bool, again, many));
            var high = Add(new IntegerLiteralInst(SilType.Int, 1L << 62));
            var low = Add(new IntegerLiteralInst(SilType.Int, -(1L << 62)));
            foreach (var total in totals.Values) {
                var from = entry[header.Arguments.IndexOf(total)];
                var above = Add(new BuiltinInst("cmp_ge_Int", SilType.Bool, from, high));
                var below = Add(new BuiltinInst("cmp_le_Int", SilType.Bool, from, low));
                again = Add(new BuiltinInst("or_Bool", SilType.Bool, again, Add(new BuiltinInst("or_Bool", SilType.Bool, above, below))));
            }

            Add(new CondBranchInst(again, original, [..entry], test.WhenFalse, [..test.FalseArguments]));
        }

        header.Instructions[^1] = new CondBranchInst(test.Condition, test.WhenTrue, [..test.TrueArguments], check, []) { Block = header, Location = test.Location };
        return initial;
    }

    static void Version(SilFunction function, Loop loop) {
        var (header, body, preheader, test, index, bound, accesses, arrays, liveOut) = loop;

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
        var limit = Inside(bound, body) ? CountOf(ArrayOf(((BuiltinInst)bound).Operands[0], body)!) : bound;
        foreach (var array in arrays) {
            var fits = Add(new BuiltinInst("cmp_le_Int", SilType.Bool, limit, CountOf(array)));
            ok = Add(new BuiltinInst("and_Bool", SilType.Bool, ok, fits));
        }

        List<SilValue> deferred = body.Count == 2
            ? DeferOverflow(function, blocks[header], blocks[test.WhenTrue], (SilArgument)values[index], header, entry.Operands, start, limit, i => Add(i))
            : [];
        preheader.Instructions.InsertRange(preheader.Instructions.Count - 1, checks);
        preheader.Instructions[^1] = new CondBranchInst(
            ok,
            blocks[header],
            [..entry.Operands, ..deferred],
            header,
            [..entry.Operands]
        ) { Block = preheader, Location = at };
        return;

        SilValue CountOf(SilValue array) {
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
