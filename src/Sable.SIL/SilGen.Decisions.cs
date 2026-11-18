using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Decisions marked for branch coverage (<c>sable test --coverage</c>):
///     where the source chooses — an <c>if</c>, a <c>while</c>, a ternary,
///     <c>&amp;&amp;</c> and <c>||</c>, a <c>match</c> or <c>switch</c>, an
///     <c>if let</c>, <c>??</c>, <c>try?</c> — each outcome gets a block that
///     only it enters, tagged with the decision and the outcome, for
///     <see cref="Coverage.Instrument" /> to count. Where an outcome's target
///     is entered other ways too (the join after an <c>if</c> without
///     <c>else</c>, a <c>switch</c> case a <c>fallthrough</c> reaches), the
///     outcome gets a block of its own on the way there. Unmarked — every
///     other build — lowering makes nothing extra.
/// </summary>
public sealed partial class SilGen {
    /// <summary>Whether lowering marks the source's decisions, for branch coverage.</summary>
    bool _markDecisions;

    sealed partial class FunctionLowering {
        /// <summary>
        ///     A decision written at <paramref name="at" />, when decisions are
        ///     marked; null otherwise, which the helpers below take as nothing
        ///     to mark.
        /// </summary>
        SilDecision? Decide(int at, string kind, params string[] outcomes) {
            if (!generator._markDecisions) {
                return null;
            }

            var position = source.GetLinePosition(at);
            var decision = new SilDecision(new SilLocation(position.Line, position.Column, _scope), kind, outcomes);
            function.Decisions.Add(decision);
            return decision;
        }

        /// <summary>The source's text of a node, on one line: a match arm's patterns, a case's values.</summary>
        string TextOf(SyntaxNode node) => string.Join(' ', source.ToString(node.Span).Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

        /// <summary>
        ///     Tags a block that only the outcome's edges enter — one just
        ///     made for where the outcome goes — as the outcome; a block
        ///     already standing for another gets one of its own before it.
        /// </summary>
        SilBlock Mark(SilBlock block, SilDecision? decision, int outcome) {
            if (decision is null) {
                return block;
            }

            if (block.Outcome is not null) {
                return Land(block, decision, outcome);
            }

            block.Outcome = (decision, outcome);
            return block;
        }

        /// <summary>
        ///     A block of its own on the way to <paramref name="target" />,
        ///     standing for the outcome: for a target other edges enter too.
        ///     It has no place in the source, so that line coverage does not
        ///     count it as a line run.
        /// </summary>
        SilBlock Land(SilBlock target, SilDecision? decision, int outcome) {
            if (decision is null || target.Arguments.Count > 0) {
                return target;
            }

            var landing = function.AddBlock();
            landing.Append(new BranchInst(target));
            landing.Outcome = (decision, outcome);
            return landing;
        }

        /// <summary>Goes on, from where lowering is, in a block of its own standing for the outcome.</summary>
        void Enter(SilDecision? decision, int outcome) {
            if (decision is null || _current is null) {
                return;
            }

            var block = function.AddBlock();
            block.Outcome = (decision, outcome);
            Branch(block);
            _current = block;
        }
    }
}
