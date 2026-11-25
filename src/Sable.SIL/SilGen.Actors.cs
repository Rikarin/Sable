using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Actors (SYNTAX.md §19, docs/06 §6.7): each actor object has a serial
///     executor, three words the runtime owns at the end of its fields, and a
///     call into an actor — or into '@MainActor' code — from outside it hops
///     there first and back after (runtime/tasks.c's sable_actor_hop): the
///     task takes the actor if it is free, and waits its turn otherwise. What
///     crosses — the arguments, and the result — is shared, counted
///     atomically from then on, since the actor and its caller may run on
///     different threads. A call that stays inside the actor, or to what is
///     'nonisolated', does not hop.
/// </summary>
public sealed partial class SilGen {
    /// <summary>The executor's three words: an inbox, a queue the actor's holder keeps, and the holder.</summary>
    internal const string ExecutorField = "$executor", ExecutorQueueField = "$executor.queue", ExecutorHolderField = "$executor.holder";

    /// <summary>The main actor, as a hop names it; 0 is anywhere.</summary>
    const long MainActor = 1;

    /// <summary>Declared 'nonisolated': reached from anywhere without a hop.</summary>
    static bool IsNonisolated(Symbol member) => member.IsNonisolated;

    /// <summary>Declared '@MainActor', or inside a declaration that is.</summary>
    static bool OnMainActor(Symbol symbol) => symbol.IsOnMainActor;

    /// <summary>The program's entry point, or code inside it: it runs on the main actor.</summary>
    static bool InEntryPoint(Symbol symbol) => symbol.IsInEntryPoint;

    /// <summary>The actor a member belongs to, if it is an actor's instance member.</summary>
    static NamedTypeSymbol? ActorOf(Symbol member) {
        // A property's accessor is the property's.
        var owner = member.ContainingSymbol is FieldSymbol property ? property.ContainingSymbol : member.ContainingSymbol;
        return (owner is ExtensionSymbol extension ? extension.ExtendedType : owner) is NamedTypeSymbol { TypeKind: TypeKind.Actor } actor
            ? actor.OriginalDefinition
            : null;
    }

    /// <summary>
    ///     Whether code in <paramref name="here" /> runs on the actor: inside
    ///     its own declaration or extensions, and not in a 'nonisolated'
    ///     member of it (or a function inside one), which runs wherever its
    ///     caller does and so reaches the actor's isolated members as code
    ///     outside it does, by a hop.
    /// </summary>
    static bool Inside(Symbol here, NamedTypeSymbol actor) {
        for (Symbol? at = here; at is not null; at = at.ContainingSymbol) {
            if (at is FunctionSymbol && IsNonisolated(at)) {
                return false;
            }

            if (ReferenceEquals(at, actor) || at is ExtensionSymbol { ExtendedType: var extended } && ReferenceEquals(extended.OriginalDefinition, actor)) {
                return true;
            }
        }

        return false;
    }

    sealed partial class FunctionLowering {
        /// <summary>
        ///     Where a call to <paramref name="callee" /> from here must hop:
        ///     its actor's object (the receiver), the main actor, or nowhere.
        ///     Only an async function hops: the binder made a crossing call
        ///     await, so it is in one.
        /// </summary>
        (bool Hops, bool Main) Crossing(FunctionSymbol callee) {
            if (!function.Type.IsAsync || IsNonisolated(callee) || callee.IsStatic || callee.FunctionKind is not (FunctionKind.Ordinary or FunctionKind.Accessor)) {
                return (false, false);
            }

            if (ActorOf(callee) is { } actor) {
                return (InClosure || !Inside(symbol, actor), false);
            }

            if (OnMainActor(callee) && (InClosure || (!OnMainActor(symbol) || IsNonisolated(symbol)) && !InEntryPoint(symbol))) {
                return (true, true);
            }

            return (false, false);
        }

        /// <summary>
        ///     Code in an async closure: it may run as a task of its own,
        ///     spawned where it was written but not on the actor that code
        ///     is on, so it hops into the actor whatever encloses it — at no
        ///     more than a comparison when it is there already.
        /// </summary>
        bool InClosure => symbol is FunctionSymbol { Name.Length: 0 };

        /// <summary>An actor's 'var' read from outside it, in an async function: on the actor.</summary>
        bool IsolatedRead(FieldSymbol field) =>
            function.Type.IsAsync && field is { IsStatic: false, IsMutable: true } && !IsNonisolated(field)
            && ActorOf(field) is { } actor && (InClosure || !Inside(symbol, actor));

        /// <summary>
        ///     The actor whose state an assignment writes, from an async closure
        ///     written inside it — which may run as a task of its own, off the
        ///     actor: 'self.count += 1' in 'Task.spawn { … }'. The receiver is
        ///     what can be read twice: 'self', a local or a parameter.
        /// </summary>
        BoundExpression? IsolatedWrite(BoundAssignment assignment) {
            if (!function.Type.IsAsync || !InClosure) {
                return null;
            }

            for (var target = assignment.Target; target is not null;) {
                switch (target) {
                    case BoundFieldAccess { Field: { IsStatic: false } field, Receiver: { } receiver } when ActorOf(field) is not null && !IsNonisolated(field):
                        return receiver is BoundSelfAccess or BoundLocalAccess or BoundParameterAccess ? receiver : null;
                    case BoundFieldAccess { Receiver: var inner }:
                        target = inner;
                        break;
                    case BoundIndexAccess index:
                        target = index.Receiver;
                        break;
                    default:
                        return null;
                }
            }

            return null;
        }

        /// <summary>An assignment to an actor's state from an async closure inside it: on the actor, the read and the write together.</summary>
        SilValue? OnActorFor(BoundAssignment assignment, Func<SilValue?> lower) {
            if (IsolatedWrite(assignment) is not { } receiver || Borrow(receiver) is not { Type: ClassType } actor) {
                return lower();
            }

            var back = HopInto(actor, []);
            End(actor);
            var result = lower();
            if (_current is not null) {
                HopBack(back, null);
            }

            return result;
        }

        /// <summary>
        ///     Before a call into an actor: where the task runs now, kept to
        ///     go back to; the arguments shared; the hop. What to hop back to.
        /// </summary>
        SilValue HopInto(SilValue? actor, IEnumerable<SilValue> arguments) {
            var back = Emit(new BuiltinInst("task_actor_current", SilType.Int));
            foreach (var argument in arguments) {
                if (argument.Type is not AddressType && !ReferenceEquals(argument, actor)) {
                    ShareArgument(argument);
                }
            }

            // Seen as the type a callee compiled once takes: the object is the one it is here.
            while (actor is BuiltinInst { Name: "opaque_borrow_as", Operands: [var seen] }) {
                actor = seen;
            }

            SilValue target;
            if (actor is { Type: ClassType actorType } && actorType.FieldIndex(ExecutorField) is var index and >= 0) {
                target = Emit(new BuiltinInst("address_to_int", SilType.Int, Emit(new RefElementAddrInst(actor, index))));
            } else {
                target = Emit(new IntegerLiteralInst(SilType.Int, MainActor));
            }

            Emit(new BuiltinInst("task_hop", SilType.Unit, target));
            return back;
        }

        /// <summary>
        ///     An argument shared: one seen as a type parameter of the callee,
        ///     which only the callee has the metadata of, as the type it is here.
        /// </summary>
        void ShareArgument(SilValue argument) {
            switch (argument) {
                case BuiltinInst { Name: "opaque_borrow_as", Operands: [var seen] } when seen.Type is not AddressType:
                    ShareArgument(seen);
                    return;

                case LoadBorrowInst { Operands: [BuiltinInst { Name: "opaque_cast", Operands: [var place] }] }:
                    if (place.Type is AddressType { Pointee: { IsTrivial: false } }) {
                        var lent = Emit(new LoadBorrowInst(place));
                        Emit(new BuiltinInst("share", SilType.Unit, lent));
                        Emit(new EndBorrowInst(lent));
                    }

                    return;
            }

            if (!argument.Type.IsTrivial) {
                Emit(new BuiltinInst("share", SilType.Unit, argument));
            }
        }

        /// <summary>A result, or an error, a call into an actor wrote where it was told: shared, as one it returns is.</summary>
        SilValue? ShareWritten(SilValue? written, SilValue? hoppedBack) {
            if (hoppedBack is not null && written is not null and not UnitValue && _current is not null && written.Type is not AddressType && !written.Type.IsTrivial) {
                Emit(new BuiltinInst("share", SilType.Unit, written));
            }

            return written;
        }

        /// <summary>After it: back where the task ran, and the result shared.</summary>
        void HopBack(SilValue back, SilValue? result) {
            Emit(new BuiltinInst("task_hop", SilType.Unit, back));
            if (result is not null && result is not UnitValue && result.Type is not AddressType && !result.Type.IsTrivial) {
                Emit(new BuiltinInst("share", SilType.Unit, result));
            }
        }
    }
}
