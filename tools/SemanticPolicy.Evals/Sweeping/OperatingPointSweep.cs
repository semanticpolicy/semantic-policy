using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Sweeping;

/// <summary>
/// Sweeps one binding's operating point: every rung's threshold and the margin gate are chosen on the tune rows
/// under the constraints given, then replayed on the test rows as they were chosen. <c>sweep</c> runs it on the
/// binding it is pointed at; <c>compare</c> runs it once per binding, on a policy holding only that binding.
/// </summary>
public static class OperatingPointSweep
{
    /// <summary>Sweeps the binding and measures the recommendation on the test rows.</summary>
    /// <param name="set">The replay set, loaded on the rule to sweep.</param>
    /// <param name="policy">The policy the sweep varies; the set's own policy or a single-binding cut of it.</param>
    /// <param name="bindingIndex">The binding in <paramref name="policy"/> to sweep.</param>
    /// <param name="splits">The rows to choose on and the rows to report on.</param>
    /// <param name="wording">How those two sets of rows are named in the result.</param>
    /// <param name="rungConstraints">The <c>--warn</c>, <c>--escalate</c> and <c>--deny</c> constraints.</param>
    /// <param name="gateConstraints">The <c>--gate</c> constraints.</param>
    /// <exception cref="EvalsException">
    /// A rung constraint names a rung the rule's ladder lacks or is given for a rule with no ladder; a gate
    /// constraint is given for a Choice or Score binding whose operating point declares no gate.
    /// </exception>
    public static SweepSection Run(
        ReplaySet set,
        Policy policy,
        int bindingIndex,
        SplitSelection splits,
        SplitWording wording,
        IReadOnlyList<RungConstraint> rungConstraints,
        IReadOnlyList<GateConstraint> gateConstraints)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(splits);
        ArgumentNullException.ThrowIfNull(wording);
        ArgumentNullException.ThrowIfNull(rungConstraints);
        ArgumentNullException.ThrowIfNull(gateConstraints);
        ArgumentOutOfRangeException.ThrowIfNegative(bindingIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bindingIndex, policy.Bindings.Count);

        Rule rule =
            policy.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, set.Rule.Id, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The policy carries no rule '{set.Rule.Id}'.", nameof(policy));
        ProviderBinding binding = policy.Bindings[bindingIndex];
        RuleOperatingPoint? point = binding.OperatingPoints.FirstOrDefault(candidate =>
            string.Equals(candidate.RuleId, rule.Id, StringComparison.Ordinal));

        List<SweptRung> rungs = [];
        if (rule is BooleanRule boolean)
        {
            RejectRungsOffTheLadder(boolean, rungConstraints);
            foreach (RungCurve curve in ThresholdCurve.Compute(set, policy, bindingIndex, splits.Tune))
            {
                double? file = point?.Thresholds.FirstOrDefault(threshold => threshold.Verdict == curve.Rung)?.AtOrAbove;
                RungRecommendation recommendation = ThresholdSweep.Recommend(curve, rungConstraints, file);
                CurvePoint? test = recommendation.Threshold is { } chosen
                    ? ThresholdCurve.At(set, policy, bindingIndex, splits.Test, curve.Rung, chosen)
                    : null;
                rungs.Add(new SweptRung(curve.Rung, curve, recommendation, test, wording.ChosenOn, wording.ReportedOn));
            }
        }
        else if (rungConstraints.Count > 0)
        {
            throw new EvalsException(
                $"--warn, --escalate and --deny cut a Boolean ladder, and rule '{rule.Id}' is a {Names.Camel(rule.Type)} "
                + "rule; only its margin gate can be swept, with --gate.");
        }

        SweptGate? gate = null;

        // A Choice or Score binding with no gate in the file declares no evidence kind for a margin, so there is no
        // curve to draw unless a constraint asks for one, and then KindOf says what the file is missing.
        if (rule is BooleanRule || point?.Gate is not null || gateConstraints.Count > 0)
        {
            EvidenceKind kind = GateSweep.KindOf(rule, binding);
            GateCurve curve = GateSweep.Compute(set, policy, bindingIndex, splits.Tune);
            GateRecommendation recommendation = GateSweep.Recommend(curve, gateConstraints, point?.Gate?.Below);
            GatePoint? test = recommendation.Feasible
                ? GateSweep.At(set, policy, bindingIndex, splits.Test, recommendation.Below)
                : null;
            gate = new SweptGate(kind, curve, recommendation, test, wording.ChosenOn, wording.ReportedOn);
        }

        return new SweepSection(binding.ProviderId, wording, rungs, Conflicts(rungs).Count > 0, gate);
    }

    /// <summary>
    /// The pairs of rungs whose thresholds, as recommended or kept, do not increase with severity: each rung
    /// against the next more severe one that has a threshold. A policy carrying them would not validate, and
    /// the tool says so rather than moving either number.
    /// </summary>
    /// <param name="rungs">The swept rungs, in ladder order.</param>
    public static IReadOnlyList<(SweptRung Lower, SweptRung Higher)> Conflicts(IReadOnlyList<SweptRung> rungs)
    {
        ArgumentNullException.ThrowIfNull(rungs);
        SweptRung[] cut = [.. rungs.Where(rung => rung.Recommendation.Threshold.HasValue)];
        List<(SweptRung Lower, SweptRung Higher)> conflicts = [];
        for (int index = 1; index < cut.Length; index++)
        {
            if (!(cut[index].Recommendation.Threshold > cut[index - 1].Recommendation.Threshold))
            {
                conflicts.Add((cut[index - 1], cut[index]));
            }
        }

        return conflicts;
    }

    private static void RejectRungsOffTheLadder(BooleanRule rule, IReadOnlyList<RungConstraint> constraints)
    {
        foreach (RungConstraint constraint in constraints)
        {
            if (!rule.Ladder.Contains(constraint.Rung))
            {
                string ladder = string.Join(", ", rule.Ladder.Select(rung => Names.Camel(rung)));
                throw new EvalsException(
                    $"--{Names.Camel(constraint.Rung)} constrains a rung rule '{rule.Id}' does not have; its ladder is "
                    + $"{ladder}.");
            }
        }
    }
}
