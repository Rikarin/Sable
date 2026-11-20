namespace Sable.SIL;

/// <summary>
///     Calls a method directly where the vtable could only ever give one
///     function: <c>class_method</c> becomes <c>function_ref</c>.
/// </summary>
/// <remarks>
///     <para>
///         A whole program knows every class it has. A call through a
///         reference of class C can only reach the method in the slot of C
///         or of a subclass of C; when every one of those slots names the
///         same function — no subclass overrides it, or there is none — that
///         function is the callee. A direct call costs no load from the
///         object's metadata, and the inliner can then inline it.
///     </para>
///     <para>
///         The method's self may be a superclass of the reference's class —
///         an inherited method — and is then given the reference upcast;
///         that is a borrow, so a method that consumes its self is left to
///         the vtable. One unit of a program compiled separately cannot
///         know the classes the others add, and is left alone.
///     </para>
/// </remarks>
public sealed class Devirtualise : ISilPass {
    public string Name => "devirtualise";

    public void Run(SilModule module) {
        if (module.Unit is not null) {
            return;
        }

        var classes = module.Types.OfType<ClassType>().ToList();
        foreach (var function in module.Functions) {
            Run(function, module, classes);
        }
    }

    public void Run(SilFunction function) {
        if (function.Module is { Unit: null } module) {
            Run(function, module, [..module.Types.OfType<ClassType>()]);
        }
    }

    static void Run(SilFunction function, SilModule module, List<ClassType> classes) {
        var uses = (Uses?)null;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not ClassMethodInst { Reference.Type: ClassType owner } dispatch) {
                    continue;
                }

                var targets = classes.Where(c => c.IsSubclassOf(owner))
                    .Select(c => c.VTable.FirstOrDefault(m => m.Method == dispatch.Method).Function)
                    .Distinct()
                    .ToList();
                if (targets is not [{ } name] || module.Find(name) is not { } target) {
                    continue;
                }

                var type = (FunctionType)dispatch.Type;
                if (!Fits(target.Type, type)) {
                    continue;
                }

                // An inherited method takes self as its own class: each call
                // passes the reference upcast.
                uses ??= new Uses(function);
                var upcast = target.Type.Parameters[0] != type.Parameters[0];
                var calls = uses.Of(dispatch);
                if (upcast && (target.Type.IsOwned(0)
                        || calls.Any(c => c is not (ApplyInst or TryApplyInst) || c.Operands.Count < 2 || c.Operands.Skip(1).Contains(dispatch)))) {
                    continue;
                }

                var direct = new FunctionRefInst(target.Name, target.Type) { Location = dispatch.Location, Block = block };
                block.Instructions[i] = direct;
                Uses.Replace(function, dispatch, direct);
                if (upcast) {
                    foreach (var call in calls) {
                        var self = new UpcastInst(call.Operands[1], (ClassType)target.Type.Parameters[0]) { Location = call.Location, Block = call.Block };
                        call.Block.Instructions.Insert(call.Block.Instructions.IndexOf(call), self);
                        call.SetOperand(1, self);
                    }

                    i = block.Instructions.IndexOf(direct);
                }

                uses = null;
            }
        }
    }

    /// <summary>
    ///     True when the function can be called where the vtable's would be:
    ///     the same signature, but for a self of a class the reference's
    ///     class inherits from.
    /// </summary>
    static bool Fits(FunctionType target, FunctionType call) =>
        target.Parameters.Length == call.Parameters.Length
        && target.Parameters.Length > 0
        && target.Result == call.Result
        && target.Thrown == call.Thrown
        && target.Parameters.Skip(1).SequenceEqual(call.Parameters.Skip(1))
        && target.Owned.SequenceEqual(call.Owned)
        && (target.Parameters[0] == call.Parameters[0]
            || call.Parameters[0] is ClassType self && target.Parameters[0] is ClassType inherited && self.IsSubclassOf(inherited));
}

/// <summary>
///     Calls through the vtable that could reach a few small overrides are
///     made to test for each and call it directly — where the inliner can
///     then take its body — with the vtable left for anything else.
/// </summary>
/// <remarks>
///     <para>
///         Of the classes a <c>class_method</c>'s reference could be, the
///         ones with no subclass whose method no other class shares are each
///         tested for (<c>class_is</c>, which for a class with no subclass is
///         one comparison of the object's metadata) — at most
///         <see cref="MostGuesses" />, and only methods the inliner would take,
///         since a direct call alone saves little over an indirect one. The
///         test is exact, so a guess is never wrong; where none matches, the
///         vtable decides as before.
///     </para>
///     <para>
///         The call must be an <c>apply</c> of a method whose self is
///         borrowed, so that the cast to the guessed class only borrows the
///         reference. Only a whole program knows its classes; one unit of
///         one compiled separately is left alone, as by
///         <see cref="Devirtualise" />.
///     </para>
/// </remarks>
public sealed class SpeculativeDevirtualise : ISilPass {
    /// <summary>The most overrides one call guesses at.</summary>
    public const int MostGuesses = 2;

    public string Name => "speculative-devirtualise";

    public void Run(SilModule module) {
        if (module.Unit is not null) {
            return;
        }

        var classes = module.Types.OfType<ClassType>().ToList();
        foreach (var function in module.Functions) {
            Run(function, module, classes);
        }
    }

    public void Run(SilFunction function) {
        if (function.Module is { Unit: null } module) {
            Run(function, module, [..module.Types.OfType<ClassType>()]);
        }
    }

    static void Run(SilFunction function, SilModule module, List<ClassType> classes) {
        var uses = new Uses(function);
        var dispatches = function.Blocks.SelectMany(static b => b.Instructions).OfType<ClassMethodInst>().ToList();
        foreach (var dispatch in dispatches) {
            if (dispatch.Reference.Type is not ClassType owner
                || uses.Of(dispatch) is not [ApplyInst call]
                || !ReferenceEquals(call.Callee, dispatch)
                || call.Block != dispatch.Block
                || call.Operands.Skip(1).Contains(dispatch)
                || ((FunctionType)dispatch.Type).IsAsync
                || Guesses(owner, dispatch, module, classes) is not { Count: > 0 } guesses) {
                continue;
            }

            Speculate(function, dispatch, call, guesses);
        }
    }

    /// <summary>The classes worth testing for, each with the method it would call.</summary>
    static List<(ClassType Class, SilFunction Target)> Guesses(ClassType owner, ClassMethodInst dispatch, SilModule module, List<ClassType> classes) {
        var possible = classes.Where(c => c.IsSubclassOf(owner)).ToList();
        var slots = possible.Select(c => (Class: c, Function: c.VTable.FirstOrDefault(m => m.Method == dispatch.Method).Function)).ToList();
        if (slots.Any(static s => s.Function is null) || slots.Select(static s => s.Function).Distinct().Count() < 2) {
            return [];
        }

        var guesses = new List<(ClassType, SilFunction)>();
        foreach (var (type, name) in slots) {
            if (possible.Any(c => c != type && c.IsSubclassOf(type))
                || slots.Count(s => s.Function == name) != 1
                || module.Find(name) is not { } target
                || target.Type.Parameters.Length == 0
                || target.Type.IsOwned(0)
                || target.Type.Parameters[0] is not ClassType self
                || !type.IsSubclassOf(self)
                || !Fits(target.Type, (FunctionType)dispatch.Type)
                || !Inliner.WouldInline(target)) {
                continue;
            }

            guesses.Add((type, target));
        }

        return guesses.Count > MostGuesses ? [] : guesses;
    }

    static bool Fits(FunctionType target, FunctionType call) =>
        target.Parameters.Length == call.Parameters.Length
        && target.Result == call.Result
        && target.Thrown == call.Thrown
        && target.Parameters.Skip(1).SequenceEqual(call.Parameters.Skip(1))
        && target.Owned.SequenceEqual(call.Owned);

    /// <summary>
    ///     <c>%r = apply %m(%self, …)</c> becomes a test of each guess in turn,
    ///     each calling its method directly, the last falling back to the
    ///     vtable; all meet in a block that takes the result.
    /// </summary>
    static void Speculate(SilFunction function, ClassMethodInst dispatch, ApplyInst call, List<(ClassType Class, SilFunction Target)> guesses) {
        var block = call.Block;
        var at = block.Instructions.IndexOf(call);
        var rest = function.AddBlock();
        foreach (var moved in block.Instructions.Skip(at + 1)) {
            moved.Block = rest;
            rest.Instructions.Add(moved);
        }

        block.Instructions.RemoveRange(at, block.Instructions.Count - at);
        block.Instructions.Remove(dispatch);
        if (call.HasResult) {
            var result = rest.AddArgument(call.Type);
            Uses.Replace(function, call, result);
        }

        var self = call.Operands[1];
        var arguments = call.Operands.Skip(2).ToArray();
        var location = call.Location;
        var testing = block;
        foreach (var (type, target) in guesses) {
            var isA = testing.Append(new BuiltinInst($"class_is:{type.Name}", SilType.Bool, self) { Location = location });
            var direct = function.AddBlock();
            var next = function.AddBlock();
            testing.Append(new CondBranchInst(isA, direct, [], next, []) { Location = location });

            var cast = direct.Append(new UncheckedRefCastInst(self, (ClassType)target.Type.Parameters[0]) { Location = location });
            var callee = direct.Append(new FunctionRefInst(target.Name, target.Type) { Location = location });
            var made = direct.Append(new ApplyInst(callee, call.Type, [cast, ..arguments]) { Location = location });
            direct.Append(new BranchInst(rest, call.HasResult ? [made] : []) { Location = location });
            testing = next;
        }

        testing.Append(dispatch);
        testing.Append(call);
        testing.Append(new BranchInst(rest, call.HasResult ? [call] : []) { Location = location });
    }
}
