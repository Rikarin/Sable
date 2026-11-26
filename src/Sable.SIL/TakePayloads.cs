namespace Sable.SIL;

/// <summary>
///     A switch on an owned enum that each case destroys, in the case's own
///     block, takes the enum apart instead (<c>switch_enum [take]</c>): each
///     case is given its payload owned, and destroys that where it destroyed
///     the enum — or, where all it did with the payload was copy it once,
///     keeps the copy's place for the payload itself, a retain and a release
///     fewer. <c>if let task = newValue { … }</c> of a setter inlined into
///     its caller, which made the optional to pass it, is one.
/// </summary>
/// <remarks>
///     Every case must be entered from the switch alone and destroy the enum
///     exactly once; the default, if any, may stand only for cases that
///     carry nothing to destroy. The pass runs last of the ARC passes, so
///     that the ones before see every switch borrow its payload.
/// </remarks>
public sealed class TakePayloads : ISilPass {
    public string Name => "take-payloads";

    public void Run(SilFunction function) {
        Uses? uses = null;
        foreach (var block in function.Blocks) {
            if (block.Terminator is not SwitchEnumInst { Takes: false, Value: var value } switchEnum
                || value.Type is not EnumType enumType || value.Type.IsTrivial
                || SilVerifier.OwnershipOf(value) != Ownership.Owned) {
                continue;
            }

            uses ??= new Uses(function);
            if (Destroys(function, switchEnum, enumType, uses) is not { } destroys) {
                continue;
            }

            Take(switchEnum, destroys);
            uses = null;
        }
    }

    /// <summary>The destroy of the enum in each of the switch's cases, or null when the switch cannot take it.</summary>
    static Dictionary<SilBlock, DestroyValueInst>? Destroys(SilFunction function, SwitchEnumInst switchEnum, EnumType enumType, Uses uses) {
        var targets = switchEnum.Cases.Select(static c => c.Target).ToList();
        if (switchEnum.Default is { } fallback) {
            // What the default stands for must carry nothing to destroy.
            var listed = switchEnum.Cases.Select(static c => c.Case).ToHashSet();
            if (enumType.Cases.Where((_, i) => !listed.Contains(i)).Any(static c => c.Payload is { IsTrivial: false })) {
                return null;
            }

            targets.Add(fallback);
        }

        if (targets.Distinct().Count() != targets.Count || targets.Contains(switchEnum.Block)) {
            return null;
        }

        var destroys = new Dictionary<SilBlock, DestroyValueInst>();
        foreach (var user in uses.Of(switchEnum.Value)) {
            if (user == switchEnum) {
                continue;
            }

            if (user is not DestroyValueInst { Uncounted: false } destroy || !targets.Contains(destroy.Block) || !destroys.TryAdd(destroy.Block, destroy)) {
                return null;
            }
        }

        if (destroys.Count != targets.Count) {
            return null;
        }

        // Each case is entered from the switch alone.
        foreach (var target in targets) {
            if (function.Blocks.Count(b => b.Successors.Contains(target)) != 1) {
                return null;
            }
        }

        // A borrowed payload is used only in its case's block, before the enum is destroyed.
        foreach (var (_, target) in switchEnum.Cases) {
            if (target.Arguments is not [{ IsGuaranteed: true } payload]) {
                continue;
            }

            var end = target.Instructions.IndexOf(destroys[target]);
            if (uses.Of(payload).Any(u => u.Block != target || target.Instructions.IndexOf(u) > end)) {
                return null;
            }
        }

        return destroys;
    }

    static void Take(SwitchEnumInst switchEnum, Dictionary<SilBlock, DestroyValueInst> destroys) {
        switchEnum.Takes = true;
        foreach (var (target, destroy) in destroys) {
            var at = target.Instructions.IndexOf(destroy);
            if (target.Arguments is not [{ IsGuaranteed: true } borrowed]) {
                // Nothing the case is given needs destroying.
                target.Instructions.RemoveAt(at);
                continue;
            }

            var payload = new SilArgument(target, borrowed.Type);
            target.Arguments[0] = payload;
            Uses.Replace(target.Function, borrowed, payload);
            destroy.SetOperand(0, payload);

            // A payload only copied, once: the copy is the payload.
            var users = target.Instructions.Where(i => i.Operands.Any(o => ReferenceEquals(o, payload))).ToList();
            if (users is [CopyValueInst copy, DestroyValueInst last] && last == destroy) {
                Uses.Replace(target.Function, copy, payload);
                target.Instructions.Remove(copy);
                target.Instructions.Remove(destroy);
            }
        }
    }
}
