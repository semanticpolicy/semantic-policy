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
/// <remarks>
/// Every curve replays the whole policy, so a rung's curve moves with the gate and the gate's curve with the lowest
/// rung's threshold. A sweep therefore runs in passes, each swept at the picks of the one before, until a pass picks
/// what it was swept at. Within a pass the rungs are swept at the gate it starts from and the gate at the thresholds
/// just picked: swept all at once, the thresholds and the gate would each follow the other's previous pick, and two
/// consistent operating points could leave them swapping halves of each for ever.
/// </remarks>
public static class OperatingPointSweep
{
    /// <summary>The most passes a sweep makes before it reports that its picks did not settle.</summary>
    public const int MaxPasses = 10;

    /// <summary>Sweeps the binding until its picks settle and measures the recommendation on the test rows.</summary>
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
        IReadOnlyList<GateConstraint> gateConstraints) =>
        Settle(set, policy, bindingIndex, splits, wording, rungConstraints, gateConstraints).Section;

    /// <summary>
    /// As <see cref="Run"/>, with the policy the last pass was swept at: once the picks settle, the policy with the
    /// recommendation in it, which is where <c>compare</c> reads the binding's test rows.
    /// </summary>
    internal static (SweepSection Section, Policy SweptAt) Settle(
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
        if (rule is BooleanRule boolean)
        {
            RejectRungsOffTheLadder(boolean, rungConstraints);
        }
        else if (rungConstraints.Count > 0)
        {
            throw new EvalsException(
                $"--warn, --escalate and --deny cut a Boolean ladder, and rule '{rule.Id}' is a {Names.Camel(rule.Type)} "
                + "rule; only its margin gate can be swept, with --gate.");
        }

        MarginGate? declared = policy.Bindings[bindingIndex].OperatingPoints
            .FirstOrDefault(candidate => string.Equals(candidate.RuleId, rule.Id, StringComparison.Ordinal))?.Gate;
        Policy current = policy;
        RuleOperatingPoint? before = null;
        for (int count = 1; ; count++)
        {
            Pass pass = Sweep(set, current, declared, bindingIndex, rule, splits, wording, rungConstraints, gateConstraints);
            SweepPasses? end = pass switch
            {
                { SweptAt: null } => new SweepPasses(count, PassesEnd.Settled),
                { Picked: null } => new SweepPasses(count, PassesEnd.Conflict),
                { SweptAt: { } sweptAt, Picked: { } picked } when Same(picked, sweptAt) =>
                    new SweepPasses(count, PassesEnd.Settled),
                { SweptAt: { } sweptAt, Picked: { } picked } when before is not null && Same(picked, before) =>
                    new SweepPasses(count, PassesEnd.Alternating, sweptAt, picked),
                { SweptAt: { } sweptAt, Picked: { } picked } when count == MaxPasses =>
                    new SweepPasses(count, PassesEnd.OutOfPasses, sweptAt, picked),
                _ => null,
            };
            if (end is not null)
            {
                string provider = policy.Bindings[bindingIndex].ProviderId;
                bool conflict = Conflicts(pass.Rungs).Count > 0;
                return (new SweepSection(provider, wording, pass.Rungs, conflict, pass.Gate, end), current);
            }

            before = pass.SweptAt;
            current = WithPoint(current, bindingIndex, pass.Picked!);
        }
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

    // One pass: the rungs at the gate the policy holds, then the gate at the thresholds just picked. A rung with
    // nothing recommended keeps the number it was swept at. When the thresholds so picked do not increase with
    // severity no policy can hold them, neither for the gate curve nor for a next pass: the gate is swept at the
    // thresholds the pass started from, and nothing is picked.
    //
    // The gate curve sets every candidate gate itself, so the gate a pass starts from does not move it, and the
    // policy it is computed on keeps the file's gate: for a Choice or Score rule that gate's kind is the only
    // declaration of what a margin is read on, and a pass that picked no gate would otherwise leave none.
    private static Pass Sweep(
        ReplaySet set,
        Policy policy,
        MarginGate? declared,
        int bindingIndex,
        Rule rule,
        SplitSelection splits,
        SplitWording wording,
        IReadOnlyList<RungConstraint> rungConstraints,
        IReadOnlyList<GateConstraint> gateConstraints)
    {
        ProviderBinding binding = policy.Bindings[bindingIndex];
        RuleOperatingPoint? point = binding.OperatingPoints.FirstOrDefault(candidate =>
            string.Equals(candidate.RuleId, rule.Id, StringComparison.Ordinal));

        List<SweptRung> rungs = [];
        if (rule is BooleanRule)
        {
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

        RuleOperatingPoint? thresholds = point is null
            ? null
            : point with
            {
                Thresholds =
                [
                    .. point.Thresholds.Select(threshold =>
                        rungs.FirstOrDefault(rung => rung.Rung == threshold.Verdict)?.Recommendation is
                            { Swept: true, Feasible: true, Threshold: { } atOrAbove }
                            ? threshold with { AtOrAbove = atOrAbove }
                            : threshold),
                ],
            };
        bool holdable = thresholds is not null && Increasing(rule, thresholds.Thresholds);
        Policy gatePolicy = (holdable ? thresholds : point) is { } at
            ? WithPoint(policy, bindingIndex, at with { Gate = declared ?? at.Gate })
            : policy;

        SweptGate? gate = null;

        // A Choice or Score binding with no gate in the file declares no evidence kind for a margin, so there is no
        // curve to draw unless a constraint asks for one, and then KindOf says what the file is missing.
        if (rule is BooleanRule || declared is not null || gateConstraints.Count > 0)
        {
            EvidenceKind kind = GateSweep.KindOf(rule, gatePolicy.Bindings[bindingIndex]);
            GateCurve curve = GateSweep.Compute(set, gatePolicy, bindingIndex, splits.Tune);
            GateRecommendation recommendation = GateSweep.Recommend(curve, gateConstraints, point?.Gate?.Below);
            GatePoint? test = recommendation.Feasible
                ? GateSweep.At(set, gatePolicy, bindingIndex, splits.Test, recommendation.Below)
                : null;
            gate = new SweptGate(kind, curve, recommendation, test, wording.ChosenOn, wording.ReportedOn);
        }

        RuleOperatingPoint? picked = holdable
            ? thresholds! with
            {
                Gate = gate is { Recommendation: { Swept: true, Feasible: true } recommended }
                    ? recommended.Below is { } below ? new MarginGate(gate.Kind, below) : null
                    : point!.Gate,
            }
            : null;
        return new Pass(rungs, gate, point, picked);
    }

    private static bool Increasing(Rule rule, IReadOnlyList<Threshold> thresholds)
    {
        if (rule is not BooleanRule boolean)
        {
            return true;
        }

        double[] values =
        [
            .. boolean.Ladder.Select(rung => thresholds.First(threshold => threshold.Verdict == rung).AtOrAbove),
        ];
        for (int index = 1; index < values.Length; index++)
        {
            if (!(values[index] > values[index - 1]))
            {
                return false;
            }
        }

        return true;
    }

    // Picks are observed values carried over unchanged, so a pass that picks what it was swept at compares equal
    // to the last decimal.
    private static bool Same(RuleOperatingPoint left, RuleOperatingPoint right) =>
        left.Thresholds.SequenceEqual(right.Thresholds) && Equals(left.Gate, right.Gate);

    private static Policy WithPoint(Policy policy, int bindingIndex, RuleOperatingPoint point) =>
        policy with
        {
            Bindings =
            [
                .. policy.Bindings.Select((binding, index) => index != bindingIndex
                    ? binding
                    : binding with
                    {
                        OperatingPoints =
                        [
                            .. binding.OperatingPoints.Select(candidate =>
                                string.Equals(candidate.RuleId, point.RuleId, StringComparison.Ordinal) ? point : candidate),
                        ],
                    }),
            ],
        };

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

    // What one pass swept, the operating point it was swept at, and what it picks for the next; SweptAt is null for
    // a binding with no operating point for the rule, and Picked for thresholds no policy can hold.
    private sealed record Pass(
        IReadOnlyList<SweptRung> Rungs,
        SweptGate? Gate,
        RuleOperatingPoint? SweptAt,
        RuleOperatingPoint? Picked);
}
