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
        /// <summary>Marks the end of a dynamic access, among a call's borrows.</summary>
        sealed class AccessMarker(SilValue address) : SilValue {
            public SilValue Address { get; } = address;
            public override SilType Type => SilType.Unit;
        }

        /// <summary>
        ///     Begins a dynamic access to storage the compiler cannot prove
        ///     unshared — a field of an object, reached through a reference —
        ///     to be ended with the call's borrows.
        /// </summary>
        void BeginAccess(SilValue address, bool modify, List<SilValue> borrows) {
            if (!InsideObject(address)) {
                return;
            }

            Emit(new BuiltinInst(modify ? "access_begin_modify" : "access_begin_read", SilType.Unit, address));
            borrows.Add(new AccessMarker(address));
        }

        static bool InsideObject(SilValue address) =>
            address switch {
                RefElementAddrInst => true,
                StructElementAddrInst element => InsideObject(element.Address),
                TupleElementAddrInst element => InsideObject(element.Address),
                _ => false,
            };

        /// <summary>A storage path: a variable, then the fields and elements inside it.</summary>
        sealed record AccessPath(Symbol Root, ImmutableArray<string> Steps, string Text) {
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

        /// <summary>Reports a call whose accesses overlap, one of them a change.</summary>
        void CheckExclusivity(BoundCall call, FunctionSymbol callee, bool mutatingReceiver) {
            var accesses = new List<(AccessPath Path, bool Modify, string What)>();
            if (mutatingReceiver && call.Receiver is { } receiver && PathOf(receiver) is { } self) {
                accesses.Add((self, true, $"the call to the mutating '{callee.Name}' changes it"));
            }

            for (var i = 0; i < call.Arguments.Length && i < callee.Parameters.Length; i++) {
                var modifier = callee.Parameters[i].Modifier;
                if (modifier is not (ParameterModifier.InOut or ParameterModifier.Out or ParameterModifier.Ref) || PathOf(call.Arguments[i]) is not { } path) {
                    continue;
                }

                var what = modifier switch {
                    ParameterModifier.InOut => $"argument {i + 1} ('{callee.Parameters[i].Name}') is inout",
                    ParameterModifier.Out => $"argument {i + 1} ('{callee.Parameters[i].Name}') is out",
                    _ => $"argument {i + 1} ('{callee.Parameters[i].Name}') reads it by reference",
                };
                accesses.Add((path, modifier != ParameterModifier.Ref, what));
            }

            for (var j = 1; j < accesses.Count; j++) {
                for (var i = 0; i < j; i++) {
                    var (first, second) = (accesses[i], accesses[j]);
                    if ((first.Modify || second.Modify) && first.Path.Overlaps(second.Path)) {
                        var shorter = first.Path.Steps.Length <= second.Path.Steps.Length ? first.Path : second.Path;
                        generator.Report(
                            ErrorCode.ERR_OverlappingAccess,
                            call.Syntax.Span,
                            $"overlapping accesses to '{shorter.Text}' for the length of the call: {first.What}, and {second.What}; "
                            + $"to fix it, copy one into a local first ('var copy = {second.Path.Text}' … then write it back), "
                            + "or pass storage that does not overlap"
                        );
                        return;
                    }
                }
            }
        }
    }
}
