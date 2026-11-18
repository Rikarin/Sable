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
