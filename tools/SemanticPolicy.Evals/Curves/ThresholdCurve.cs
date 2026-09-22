using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Curves;

/// <summary>
/// Sweeps one binding's threshold for a Boolean rule and reports what the rule would have concluded at
/// every candidate. A point is a replay of the whole chain at a policy carrying that number, never a
/// comparison made here, so margin gates, fallback and the failure behaviour move rows exactly as they
/// would at run time.
/// </summary>
public static class ThresholdCurve
{
    private const int _gridSteps = 20;

    /// <summary>Computes one curve per rung of the swept rule's ladder.</summary>
    /// <param name="set">The replay set, loaded on the rule to sweep.</param>
    /// <param name="policy">The policy the sweep varies; every binding of it is kept.</param>
    /// <param name="bindingIndex">The binding in <paramref name="policy"/> whose threshold moves.</param>
    /// <param name="rows">The rows to score; the replay is filtered to them.</param>
    /// <exception cref="ArgumentException">
    /// The policy has no such rule, the rule is not a Boolean one, or a binding carries no operating point
    /// or no rung threshold for it.
    /// </exception>
    public static IReadOnlyList<RungCurve> Compute(
        ReplaySet set,
        Policy policy,
        int bindingIndex,
        IReadOnlyCollection<DatasetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(bindingIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bindingIndex, policy.Bindings.Count);

        BooleanRule rule = Swept(policy, set.Rule.Id);
        ProviderBinding binding = policy.Bindings[bindingIndex];
        RuleOperatingPoint point =
            binding.OperatingPoints.FirstOrDefault(candidate =>
                string.Equals(candidate.RuleId, rule.Id, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Binding '{binding.ProviderId}' carries no operating point for rule '{rule.Id}'.",
                nameof(policy));
        HashSet<string> selected = new(rows.Select(row => row.Id), StringComparer.Ordinal);
        List<(double Threshold, bool Observed)> candidates =
            Candidates(set, binding.ProviderId, rule, point.Thresholds[0].Kind, selected);

        List<RungCurve> curves = new(rule.Ladder.Count);
        foreach (Verdict rung in rule.Ladder)
        {
            // One rung at a time: a policy's thresholds have to increase with severity, so a sweep of the
            // whole ladder at once would be rejected as soon as the candidate passed a rung above it. Cut
            // down to the rung being measured, every candidate is a policy the runtime accepts.
            BooleanRule single = rule with { Ladder = [rung] };
            List<CurvePoint> points = new(candidates.Count);
            foreach ((double threshold, bool observed) in candidates)
            {
                IReadOnlyList<RowOutcome> outcomes =
                    Outcomes(set, Variant(policy, single, bindingIndex, threshold), single, selected);
                points.Add(new CurvePoint(
                    threshold,
                    observed,
                    RungMetrics.Compute(outcomes, single)[0].Matrix,
                    OutcomeCounts.Compute(outcomes)));
            }

            curves.Add(new RungCurve(rung, points));
        }

        return curves;
    }

    private static BooleanRule Swept(Policy policy, string ruleId)
    {
        Rule rule =
            policy.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, ruleId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The policy carries no rule '{ruleId}'.", nameof(policy));
        return rule as BooleanRule
            ?? throw new ArgumentException(
                $"Rule '{ruleId}' is a {rule.Type} rule, and only a Boolean ladder is cut by a threshold.",
                nameof(policy));
    }

    private static List<(double Threshold, bool Observed)> Candidates(
        ReplaySet set,
        string providerId,
        BooleanRule rule,
        EvidenceKind kind,
        HashSet<string> selected)
    {
        string flagged = rule.FlaggedAnswer ? "true" : "false";
        SortedSet<double> observed = [];
        foreach (ReplayRow row in set.Rows)
        {
            if (!selected.Contains(row.Row.Id)
                || !row.AttemptsByProvider.TryGetValue(providerId, out ProviderResult? result))
            {
                continue;
            }

            // The kind the operating point declares and no other: a result may carry several, and a number
            // on one scale is not a candidate cut on another. A stored result can come back with no list,
            // a null entry or a null Values; each reads as no evidence, as it does in the step function.
            Evidence? entry = result.Evidence?.FirstOrDefault(candidate =>
                candidate is not null && candidate.Kind == kind && candidate.Values is not null);
            if (entry is not null
                && EvidenceMath.WithBooleanComplement(entry).Values.TryGetValue(flagged, out double value))
            {
                observed.Add(value);
            }
        }

        List<(double Threshold, bool Observed)> candidates = [.. observed.Select(value => (value, true))];
        if (kind != EvidenceKind.Probability)
        {
            // A score or a logit is on the provider's own scale, where a fixed grid is an arbitrary set of
            // numbers; only values some attempt actually reported say anything there.
            return candidates;
        }

        // Rounded to two decimals so a grid point does not land beside an observed value it is meant to be:
        // a recorded 0.35 and 7/20 are not always the same double.
        HashSet<double> covered = [.. observed.Select(value => Math.Round(value, 2))];
        for (int step = 0; step <= _gridSteps; step++)
        {
            double value = (double)step / _gridSteps;
            if (covered.Add(Math.Round(value, 2)))
            {
                candidates.Add((value, false));
            }
        }

        candidates.Sort((left, right) => left.Threshold.CompareTo(right.Threshold));
        return candidates;
    }

    private static Policy Variant(Policy policy, BooleanRule single, int bindingIndex, double threshold)
    {
        Verdict rung = single.Ladder[0];
        List<ProviderBinding> bindings = new(policy.Bindings.Count);
        for (int index = 0; index < policy.Bindings.Count; index++)
        {
            ProviderBinding binding = policy.Bindings[index];
            List<RuleOperatingPoint> points = new(binding.OperatingPoints.Count);
            foreach (RuleOperatingPoint point in binding.OperatingPoints)
            {
                if (!string.Equals(point.RuleId, single.Id, StringComparison.Ordinal))
                {
                    points.Add(point);
                    continue;
                }

                Threshold cut = point.Thresholds.FirstOrDefault(candidate => candidate.Verdict == rung)
                    ?? throw new ArgumentException(
                        $"Binding '{binding.ProviderId}' has no {rung} threshold for rule '{single.Id}'.",
                        nameof(policy));

                // Every other binding keeps its own number and its gate: the sweep asks what moving one
                // provider's cut does to the chain, not what the chain does with one provider in it.
                points.Add(point with
                {
                    Thresholds = [index == bindingIndex ? cut with { AtOrAbove = threshold } : cut],
                });
            }

            bindings.Add(binding with { OperatingPoints = points });
        }

        return policy with
        {
            Rules =
            [
                .. policy.Rules.Select(rule =>
                    string.Equals(rule.Id, single.Id, StringComparison.Ordinal) ? single : rule),
            ],
            Bindings = bindings,
        };
    }

    private static List<RowOutcome> Outcomes(
        ReplaySet set,
        Policy variant,
        BooleanRule single,
        HashSet<string> selected)
    {
        List<RowOutcome> outcomes = [];
        foreach (EvaluatedRow row in set.Evaluate(variant))
        {
            if (selected.Contains(row.Row.Id))
            {
                outcomes.Add(RowCounting.Classify(row, single));
            }
        }

        return outcomes;
    }
}
