using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     The law of exclusivity (docs/02 §2.3, docs/04 §3.4): while an
///     <c>inout</c> access is in progress, nothing else reads or writes the
///     same storage.
/// </summary>
/// <remarks>
///     Statically, the accesses one call makes — its <c>inout</c> and
///     <c>ref</c> arguments, and a mutating method's receiver — are paths
///     from a variable through fields and elements; two that overlap, one of
///     them a change, are an error. What the compiler cannot see, two
///     references to one object, is checked as the program runs: an access
///     to storage inside an object begins before the call and ends after it,
///     and one that conflicts with an access in progress traps.
/// </remarks>
public sealed partial class SilGen {
    sealed partial class FunctionLowering {
        /// <summary>A dynamic access among a call's borrows: begun just before the call, ended with the rest.</summary>
        sealed class AccessMarker(SilValue address, bool modify) : SilValue {
            public SilValue Address { get; } = address;
            public bool Modify { get; } = modify;
            public bool Begun { get; set; }
            public override SilType Type => SilType.Unit;
        }

        /// <summary>
        ///     Notes a dynamic access to storage the compiler cannot prove
        ///     unshared — a global, or a field of an object reached through a
        ///     reference — to begin just before the call and end with the
        ///     call's borrows.
        /// </summary>
        void BeginAccess(SilValue address, bool modify, List<SilValue> borrows) {
            // An element is an access to the whole array, which must not
            // change — grow, be replaced — while the element is lent.
            if (address is BuiltinInst { Name: "array_element_addr" } element) {
                BeginAccess(element.Operands[0], modify: true, borrows);
                return;
            }

            if (SharedStorage.Contains(address)) {
                borrows.Add(new AccessMarker(address, modify));
            }
        }

        /// <summary>
        ///     Begins the call's accesses, once every argument is evaluated: an
        ///     argument after an inout one may read the same storage
        ///     ('f(&amp;box.n, box.n)'), and that read comes before the call.
        /// </summary>
        void BeginAccesses(List<SilValue> borrows) {
            foreach (var borrow in borrows) {
                if (borrow is DeferredStep { Done: false } step) {
                    step.Done = true;
                    step.Run();
                    continue;
                }

                if (borrow is AccessMarker { Begun: false } marker) {
                    Emit(new BuiltinInst(marker.Modify ? "access_begin_modify" : "access_begin_read", SilType.Unit, marker.Address));
                    marker.Begun = true;
                }
            }
        }

        /// <summary>A storage path: a variable, then the fields and elements inside it.</summary>
        /// <remarks>A value's own self, in its mutating members and the closures inside them, is the root <see cref="SelfKey" />.</remarks>
        sealed record AccessPath(object Root, ImmutableArray<string> Steps, string Text) {
            public bool Overlaps(AccessPath other) =>
                ReferenceEquals(Root, other.Root)
                && Steps.Zip(other.Steps).All(static pair => pair.First == pair.Second || pair.First == "[]" || pair.Second == "[]");
        }

        static AccessPath? PathOf(BoundExpression expression) {
            switch (expression) {
                case BoundConversion { ConversionKind: ConversionKind.Identity or ConversionKind.Reference or ConversionKind.Dereference } conversion:
                    return PathOf(conversion.Operand);

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf, Operand: var target }:
                    return PathOf(target);

                case BoundLocalAccess local:
                    return new(local.Local, [], local.Local.Name);

                case BoundParameterAccess parameter:
                    return new(parameter.Parameter, [], parameter.Parameter.Name);

                case BoundSelfAccess { IsMutable: true, Type.IsReferenceType: false }:
                    return new(SelfKey, [], "self");

                // A field of an object reached through the same variable is the
                // same storage; through two variables, that is the runtime's to tell.
                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when PathOf(receiver) is { } owner:
                    return owner with { Steps = owner.Steps.Add(field.Name), Text = $"{owner.Text}.{field.Name}" };

                // An element is an access to the whole collection.
                case BoundIndexAccess { Receiver: { } receiver } when PathOf(receiver) is { } collection:
                    return collection with { Steps = collection.Steps.Add("[]"), Text = $"{collection.Text}[…]" };

                default:
                    return null;
            }
        }

        static BoundExpression Unconverted(BoundExpression expression) =>
            expression is BoundConversion conversion ? Unconverted(conversion.Operand) : expression;

        /// <summary>The variables of the code around a closure that its body uses, and whether it assigns to each.</summary>
        static Dictionary<Symbol, bool> OuterVariables(BoundClosure closure) {
            var found = new Dictionary<Symbol, bool>(ReferenceEqualityComparer.Instance);

            bool Inside(Symbol symbol) {
                for (var container = symbol.ContainingSymbol; container is not null; container = container.ContainingSymbol) {
                    if (ReferenceEquals(container, closure.Symbol)) {
                        return true;
                    }
                }

                return false;
            }

            void Visit(BoundNode node) {
                switch (node) {
                    case BoundAssignment { Target: var target } when Root(target) is { } assigned && !Inside(assigned):
                        found[assigned] = true;
                        break;

                    case BoundLocalAccess local when !Inside(local.Local):
                        found.TryAdd(local.Local, false);
                        break;

                    case BoundParameterAccess parameter when !Inside(parameter.Parameter):
                        found.TryAdd(parameter.Parameter, false);
                        break;
                }

                foreach (var child in node.Children) {
                    Visit(child);
                }
            }

            static Symbol? Root(BoundExpression target) => PathOf(target)?.Root as Symbol;

            Visit(closure.Body);
            return found;
        }

        /// <summary>
        ///     What a closure inside a mutating member reads and changes of the
        ///     member's self, each as the path it names: a closure that changes
        ///     'count' may run while 'items' changes, as in Swift.
        /// </summary>
        static List<(AccessPath Path, bool Modify)> SelfAccesses(BoundClosure closure) {
            var found = new List<(AccessPath, bool)>();

            void Place(BoundExpression place, bool modify) {
                if (PathOf(place) is { } path) {
                    if (path.Root is string) {
                        found.Add((path, modify));
                    }

                    Indices(place);
                    return;
                }

                switch (place) {
                    case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf, Operand: var operand }:
                        Place(operand, modify);
                        break;
                    case BoundConversion conversion:
                        Place(conversion.Operand, modify);
                        break;
                    case BoundFieldAccess { Receiver: { } receiver }:
                        Place(receiver, modify && !receiver.Type.IsReferenceType);
                        break;
                    case BoundTupleElementAccess tuple:
                        Place(tuple.Receiver, modify);
                        break;
                    case BoundIndexAccess index:
                        Place(index.Receiver, modify && !index.Receiver.Type.IsReferenceType);
                        foreach (var at in index.Indices) {
                            Visit(at);
                        }

                        break;
                    // A self that cannot change, or a static field: no place of self's.
                    case BoundSelfAccess or BoundFieldAccess:
                        break;
                    default:
                        Visit(place);
                        break;
                }
            }

            // What the subscripts along a path read: 'items[count]'.
            void Indices(BoundExpression place) {
                switch (place) {
                    case BoundConversion conversion:
                        Indices(conversion.Operand);
                        break;
                    case BoundUnaryOperator unary:
                        Indices(unary.Operand);
                        break;
                    case BoundFieldAccess { Receiver: { } receiver }:
                        Indices(receiver);
                        break;
                    case BoundIndexAccess index:
                        Indices(index.Receiver);
                        foreach (var at in index.Indices) {
                            Visit(at);
                        }

                        break;
                }
            }

            void Visit(BoundNode node) {
                switch (node) {
                    case BoundAssignment assignment:
                        Place(assignment.Target, true);
                        Visit(assignment.Value);
                        return;

                    case BoundCall call:
                        if (call.Receiver is { } receiver) {
                            Place(receiver, call.Function.IsMutating);
                        } else if (call.Function is { IsStatic: false, FunctionKind: FunctionKind.Ordinary, ContainingSymbol: NamedTypeSymbol or ExtensionSymbol }) {
                            found.Add((new AccessPath(SelfKey, [], "self"), call.Function.IsMutating));
                        }

                        for (var i = 0; i < call.Arguments.Length; i++) {
                            Place(call.Arguments[i], i < call.Function.Parameters.Length
                                && call.Function.Parameters[i].Modifier is ParameterModifier.InOut or ParameterModifier.Out);
                        }

                        return;

                    case BoundIndirectCall indirect:
                        Visit(indirect.Callee);
                        for (var i = 0; i < indirect.Arguments.Length; i++) {
                            Place(indirect.Arguments[i], indirect.Callee.Type is FunctionTypeSymbol type && type.IsInOut(i));
                        }

                        return;

                    case BoundSelfAccess or BoundFieldAccess or BoundIndexAccess or BoundTupleElementAccess:
                        Place((BoundExpression)node, node is BoundFieldAccess { Field.IsLazy: true });
                        return;
                }

                foreach (var child in node.Children) {
                    Visit(child);
                }
            }

            Visit(closure.Body);
            return found;
        }

        /// <summary>Reports a call whose accesses overlap, one of them a change.</summary>
        void CheckExclusivity(BoundCall call, FunctionSymbol callee, bool mutatingReceiver) {
            var accesses = new List<(AccessPath Path, bool Modify, string What, bool InClosure, SyntaxNode? Syntax)>();
            if (mutatingReceiver && call.Receiver is { } receiver && PathOf(receiver) is { } self) {
                accesses.Add((self, true, $"the call to the mutating '{callee.Name}' changes it", false, receiver.Syntax));
            } else if (mutatingReceiver && call.Receiver is null && callee is { IsStatic: false } && _selfAddress is not null) {
                accesses.Add((new AccessPath(SelfKey, [], "self"), true, $"the call to the mutating '{callee.Name}' changes it", false, call.Syntax));
            }

            for (var i = 0; i < call.Arguments.Length && i < callee.Parameters.Length; i++) {
                var modifier = callee.Parameters[i].Modifier;
                if (!callee.Parameters[i].IsCallersStorage || PathOf(call.Arguments[i]) is not { } path) {
                    continue;
                }

                var what = modifier switch {
                    ParameterModifier.InOut => $"argument {i + 1} ('{callee.Parameters[i].Name}') is inout",
                    ParameterModifier.Out => $"argument {i + 1} ('{callee.Parameters[i].Name}') is out",
                    _ => $"argument {i + 1} ('{callee.Parameters[i].Name}') reads it by reference",
                };
                accesses.Add((path, modifier is ParameterModifier.InOut or ParameterModifier.Out, what, false, call.Arguments[i].Syntax));
            }

            // A closure written into the call may run during it, and reach a
            // variable the call has inout: 'withWrite(&m) { m = 99 }'.
            for (var i = 0; i < call.Arguments.Length; i++) {
                if (Unconverted(call.Arguments[i]) is not BoundClosure closure) {
                    continue;
                }

                foreach (var (variable, changes) in OuterVariables(closure)) {
                    accesses.Add((
                        new AccessPath(variable, [], variable.Name),
                        changes,
                        $"argument {i + 1} is a closure that {(changes ? "changes" : "reads")} it",
                        true,
                        closure.Syntax
                    ));
                }

                foreach (var (path, changes) in SelfAccesses(closure)) {
                    accesses.Add((path, changes, $"argument {i + 1} is a closure that {(changes ? "changes" : "reads")} it", true, closure.Syntax));
                }
            }

            for (var j = 1; j < accesses.Count; j++) {
                for (var i = 0; i < j; i++) {
                    var (first, second) = (accesses[i], accesses[j]);
                    if ((first.Modify || second.Modify) && !(first.InClosure && second.InClosure) && first.Path.Overlaps(second.Path)) {
                        var shorter = first.Path.Steps.Length <= second.Path.Steps.Length ? first.Path : second.Path;
                        // Each access, where it is written, beside the call.
                        generator.Report(Diagnostic.Error(
                                ErrorCode.ERR_OverlappingAccess,
                                call.Syntax.Span,
                                $"overlapping accesses to '{shorter.Text}' for the length of the call: {first.What}, and {second.What}; "
                                + $"to fix it, copy one into a local first ('var copy = {second.Path.Text}' … then write it back), "
                                + "or pass storage that does not overlap"
                            )
                            .WithRelated(DiagnosticLocation.At(first.Syntax, first.What))
                            .WithRelated(DiagnosticLocation.At(second.Syntax, second.What)));
                        return;
                    }
                }
            }
        }
    }
}
