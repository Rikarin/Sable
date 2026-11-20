using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Actors (SYNTAX.md §19, docs/06 §6.7): each actor object has a serial
///     executor, two words the runtime owns at the end of its fields, and a
///     call into an actor — or into '@MainActor' code — from outside it hops
///     there first and back after (runtime/tasks.c's sable_actor_hop): the
///     task takes the actor if it is free, and waits its turn otherwise. What
///     crosses — the arguments, and the result — is shared, counted
///     atomically from then on, since the actor and its caller may run on
///     different threads. A call that stays inside the actor, or to what is
///     'nonisolated', does not hop.
/// </summary>
public sealed partial class SilGen {
    /// <summary>The executor's two words: an inbox, and a queue the actor's holder keeps.</summary>
    internal const string ExecutorField = "$executor", ExecutorQueueField = "$executor.queue";

    /// <summary>The main actor, as a hop names it; 0 is anywhere.</summary>
    const long MainActor = 1;

    /// <summary>Declared 'nonisolated': reached from anywhere without a hop.</summary>
    static bool IsNonisolated(Symbol member) =>
        member.DeclaringSyntax.Any(static syntax => {
            for (SyntaxNode? node = syntax; node is not null; node = node.Parent) {
                switch (node) {
                    case FunctionDeclarationSyntax function:
                        return function.Modifiers.Any(static m => m.Kind == SyntaxKind.NonisolatedKeyword);
                    case FieldDeclarationSyntax field:
                        return field.Modifiers.Any(static m => m.Kind == SyntaxKind.NonisolatedKeyword);
                }
            }

            return false;
        });

    /// <summary>Declared '@MainActor', or inside a declaration that is.</summary>
    static bool OnMainActor(Symbol symbol) {
        for (var at = symbol; at is not null and not ModuleSymbol; at = at.ContainingSymbol) {
            foreach (var syntax in at.DeclaringSyntax) {
                for (SyntaxNode? node = syntax; node is not null; node = node.Parent) {
                    if (node is DeclarationSyntax && node.ChildNodes().OfType<AttributeSyntax>().Any(static a => a.Name.ToString().Trim() == "MainActor")) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The program's entry point, or code inside it: it runs on the main actor.</summary>
    static bool InEntryPoint(Symbol symbol) {
        for (var at = symbol; at is not null and not ModuleSymbol; at = at.ContainingSymbol) {
            if (at is FunctionSymbol { ContainingSymbol: ModuleSymbol } function
                && (function.Name == "main" || function.DeclaringSyntax.Any(static s => s is FunctionDeclarationSyntax declaration
                    && declaration.Attributes.Any(static a => a.Name.ToString().Trim() == "main")))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The actor a member belongs to, if it is an actor's instance member.</summary>
    static NamedTypeSymbol? ActorOf(Symbol member) {
        // A property's accessor is the property's.
        var owner = member.ContainingSymbol is FieldSymbol property ? property.ContainingSymbol : member.ContainingSymbol;
        return (owner is ExtensionSymbol extension ? extension.ExtendedType : owner) is NamedTypeSymbol { TypeKind: TypeKind.Actor } actor
            ? actor.OriginalDefinition
            : null;
    }

    /// <summary>Whether code in <paramref name="here" /> is inside the actor's own declaration or extensions.</summary>
    static bool Inside(Symbol here, NamedTypeSymbol actor) {
        for (Symbol? at = here; at is not null; at = at.ContainingSymbol) {
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
                return (!Inside(symbol, actor), false);
            }

            if (OnMainActor(callee) && !OnMainActor(symbol) && !InEntryPoint(symbol)) {
                return (true, true);
            }

            return (false, false);
        }

        /// <summary>An actor's 'var' read from outside it, in an async function: on the actor.</summary>
        bool IsolatedRead(FieldSymbol field) =>
            function.Type.IsAsync && field is { IsStatic: false, IsMutable: true } && !IsNonisolated(field)
            && ActorOf(field) is { } actor && !Inside(symbol, actor);

        /// <summary>
        ///     Before a call into an actor: where the task runs now, kept to
        ///     go back to; the arguments shared; the hop. What to hop back to.
        /// </summary>
        SilValue HopInto(SilValue? actor, IEnumerable<SilValue> arguments) {
            var back = Emit(new BuiltinInst("task_actor_current", SilType.Int));
            foreach (var argument in arguments) {
                if (argument.Type is not AddressType && !argument.Type.IsTrivial && !ReferenceEquals(argument, actor)) {
                    Emit(new BuiltinInst("share", SilType.Unit, argument));
                }
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

        /// <summary>After it: back where the task ran, and the result shared.</summary>
        void HopBack(SilValue back, SilValue? result) {
            Emit(new BuiltinInst("task_hop", SilType.Unit, back));
            if (result is not null && result is not UnitValue && result.Type is not AddressType && !result.Type.IsTrivial) {
                Emit(new BuiltinInst("share", SilType.Unit, result));
            }
        }
    }
}
