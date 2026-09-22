using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Sweeping;

/// <summary>
/// One candidate margin gate on the swept binding and what the rule would have concluded there: how many rows
/// the gate held back and how accurate the rule was on the rest. A gate buys accuracy with abstentions, and a
/// point shows both sides of that trade.
/// </summary>
/// <param name="Below">The gate, or <see langword="null"/> for the binding with no gate at all.</param>
/// <param name="Abstained">Rows the chain ran out of bindings on under the gate.</param>
/// <param name="AbstentionRate">Abstained rows over every row of the selection.</param>
/// <param name="Decided">Rows a provider's answer decided.</param>
/// <param name="Accuracy">
/// Accuracy on the decided rows: for a Boolean rule, its lowest ladder rung at the policy's thresholds; for a
/// Choice or Score rule, the share answered as labelled. <see langword="null"/> when nothing was decided.
/// </param>
public sealed record GatePoint(double? Below, int Abstained, double AbstentionRate, int Decided, double? Accuracy);

/// <summary>The abstention-against-accuracy curve of one binding's gate, from no gate up to the widest margin seen.</summary>
/// <param name="Points">The no-gate point first, then one point per observed margin, ascending.</param>
public sealed record GateCurve(IReadOnlyList<GatePoint> Points);

/// <summary>
/// The gate recommended for the swept binding, and why. Shaped like a rung's recommendation: without a
/// constraint the policy file's gate stays and the gate is reported as not swept.
/// </summary>
/// <param name="Constraints">The <c>--gate</c> constraints, in the order they were given.</param>
/// <param name="Swept">Whether any gate constraint was given.</param>
/// <param name="Feasible">Whether some candidate satisfies every constraint; <see langword="true"/> when not swept.</param>
/// <param name="Below">
/// The recommended gate, or the file's when not swept. <see langword="null"/> means no gate — recommended, or
/// the file's — or, when <paramref name="Feasible"/> is <see langword="false"/>, that nothing is recommended.
/// </param>
/// <param name="Chosen">The curve point the recommendation stands on, when there is one.</param>
/// <param name="Nearest">
/// When the constraints cannot be met, the point that misses them by the least, summed over the constraints.
/// </param>
public sealed record GateRecommendation(
    IReadOnlyList<GateConstraint> Constraints,
    bool Swept,
    bool Feasible,
    double? Below,
    GatePoint? Chosen,
    GatePoint? Nearest);

/// <summary>
/// Sweeps one binding's margin gate. Candidates are read from the binding's recorded evidence through the
/// library's own margin arithmetic; what a row counts as at each candidate comes only from replaying the policy
/// with that gate, so the fallback to the next binding and the abstention at the end of the chain happen here
/// exactly as they would at run time.
/// </summary>
public static class GateSweep
{
    /// <summary>
    /// Computes the gate curve. The margin is read on the operating point's own evidence kind: the thresholds'
    /// kind for a Boolean rule, the file gate's kind for a Choice or Score rule. The gate is the only thing the
    /// variants change; thresholds, the other bindings and the failure behaviour stay as the policy has them.
    /// </summary>
    /// <param name="set">The replay set, loaded on the rule to sweep.</param>
    /// <param name="policy">The policy the sweep varies.</param>
    /// <param name="bindingIndex">The binding in <paramref name="policy"/> whose gate moves.</param>
    /// <param name="rows">The rows to score; the replay is filtered to them.</param>
    /// <exception cref="EvalsException">
    /// The rule is a Choice or Score rule and the binding carries no gate for it in the policy file, so no evidence
    /// kind is declared to read a margin on; the message names the rule and the provider.
    /// </exception>
    public static GateCurve Compute(ReplaySet set, Policy policy, int bindingIndex, IReadOnlyCollection<DatasetRow> rows)
    {
        (Rule rule, ProviderBinding binding, HashSet<string> selected) = Resolve(set, policy, bindingIndex, rows);
        EvidenceKind kind = KindOf(rule, binding);
        SortedSet<double> margins = [];
        foreach (ReplayRow row in set.Rows)
        {
            if (!selected.Contains(row.Row.Id)
                || !row.AttemptsByProvider.TryGetValue(binding.ProviderId, out ProviderResult? result))
            {
                continue;
            }

            // The declared kind and no other, read the way the step function reads it: a null list, a null entry
            // or a null Values is no evidence. A margin of zero is not a gate a policy may carry, and a gate that
            // holds back nothing is the no-gate point already on the curve.
            Evidence? entry = result?.Evidence?.FirstOrDefault(candidate =>
                candidate is not null && candidate.Kind == kind && candidate.Values is not null);
            if (entry is not null && EvidenceMath.Margin(entry) is { } margin && double.IsFinite(margin) && margin > 0)
            {
                margins.Add(margin);
            }
        }

        List<GatePoint> points = new(margins.Count + 1) { Point(set, policy, bindingIndex, rule, null, selected) };
        foreach (double margin in margins)
        {
            points.Add(Point(set, policy, bindingIndex, rule, new MarginGate(kind, margin), selected));
        }

        return new GateCurve(points);
    }

    /// <summary>
    /// The one point of the curve at a given gate, on any rows: how a gate chosen on one split does on another,
    /// where the gate need not be a margin that split ever produced.
    /// </summary>
    /// <param name="set">The replay set, loaded on the rule to sweep.</param>
    /// <param name="policy">The policy the gate is set on.</param>
    /// <param name="bindingIndex">The binding in <paramref name="policy"/> whose gate is set.</param>
    /// <param name="rows">The rows to score.</param>
    /// <param name="below">The gate, or <see langword="null"/> for none.</param>
    /// <exception cref="EvalsException">A gate was given for a Choice or Score binding that declares no kind.</exception>
    public static GatePoint At(
        ReplaySet set,
        Policy policy,
        int bindingIndex,
        IReadOnlyCollection<DatasetRow> rows,
        double? below)
    {
        (Rule rule, ProviderBinding binding, HashSet<string> selected) = Resolve(set, policy, bindingIndex, rows);
        return Point(set, policy, bindingIndex, rule, Gate(rule, binding, below), selected);
    }

    /// <summary>What became of every row at a given gate, failures included; the rest of the policy as it is.</summary>
    /// <param name="set">The replay set, loaded on the rule to sweep.</param>
    /// <param name="policy">The policy the gate is set on.</param>
    /// <param name="bindingIndex">The binding in <paramref name="policy"/> whose gate is set.</param>
    /// <param name="rows">The rows to score.</param>
    /// <param name="below">The gate, or <see langword="null"/> for none.</param>
    /// <exception cref="EvalsException">A gate was given for a Choice or Score binding that declares no kind.</exception>
    public static OutcomeCounts OutcomesAt(
        ReplaySet set,
        Policy policy,
        int bindingIndex,
        IReadOnlyCollection<DatasetRow> rows,
        double? below)
    {
        (Rule rule, ProviderBinding binding, HashSet<string> selected) = Resolve(set, policy, bindingIndex, rows);
        return OutcomeCounts.Compute(Outcomes(set, Variant(policy, bindingIndex, rule.Id, Gate(rule, binding, below)), rule, selected));
    }

    /// <summary>
    /// Intersects the feasible sets of the gate constraints and takes the end the first-named one asks for: the
    /// highest gate for <c>max-abstain</c>, the lowest for <c>min-accuracy</c>. No gate is the lowest gate there
    /// is. An undefined accuracy — nothing decided — satisfies nothing.
    /// </summary>
    /// <param name="curve">The gate curve, as <see cref="Compute"/> computed it on the rows to choose on.</param>
    /// <param name="constraints">The <c>--gate</c> constraints.</param>
    /// <param name="fileBelow">The policy file's gate, kept when no constraint is given.</param>
    public static GateRecommendation Recommend(
        GateCurve curve,
        IReadOnlyList<GateConstraint> constraints,
        double? fileBelow = null)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(constraints);
        if (constraints.Count == 0)
        {
            return new GateRecommendation(constraints, Swept: false, Feasible: true, fileBelow, null, null);
        }

        IOrderedEnumerable<GatePoint> ascending =
            curve.Points.OrderBy(point => point.Below.HasValue).ThenBy(point => point.Below);
        GatePoint[] preferred = constraints[0].Kind == GateConstraintKind.MaxAbstain
            ? [.. ascending.Reverse()]
            : [.. ascending];

        GatePoint? chosen = preferred.FirstOrDefault(point => constraints.All(constraint => Satisfies(point, constraint)));
        if (chosen is not null)
        {
            return new GateRecommendation(constraints, Swept: true, Feasible: true, chosen.Below, chosen, null);
        }

        GatePoint? nearest = null;
        double least = double.PositiveInfinity;
        foreach (GatePoint point in preferred)
        {
            if (TotalViolation(point, constraints) is { } violation && violation < least)
            {
                least = violation;
                nearest = point;
            }
        }

        return new GateRecommendation(constraints, Swept: true, Feasible: false, null, null, nearest);
    }

    /// <summary>
    /// The evidence kind a gate on this binding is read on: the thresholds' kind for a Boolean rule, the file
    /// gate's kind for a Choice or Score rule.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="binding">The binding.</param>
    /// <exception cref="EvalsException">A Choice or Score binding carries no gate for the rule in the policy file.</exception>
    public static EvidenceKind KindOf(Rule rule, ProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(binding);
        RuleOperatingPoint? point = binding.OperatingPoints.FirstOrDefault(candidate =>
            string.Equals(candidate.RuleId, rule.Id, StringComparison.Ordinal));
        if (rule is BooleanRule)
        {
            return point is { Thresholds: [{ } first, ..] }
                ? first.Kind
                : throw new ArgumentException(
                    $"Binding '{binding.ProviderId}' carries no thresholds for rule '{rule.Id}'.",
                    nameof(binding));
        }

        // A Choice or Score operating point declares an evidence kind only through its gate. Guessing one from what
        // the provider happened to return would sweep a number the policy never said it reads.
        return point?.Gate?.Kind
            ?? throw new EvalsException(
                $"Rule '{rule.Id}' has no margin gate on provider '{binding.ProviderId}' in the policy file, so no "
                + "evidence kind says what a margin is read on. Give that binding a gate for the rule in the policy "
                + "file, on the kind to sweep, and run the sweep again.");
    }

    private static (Rule Rule, ProviderBinding Binding, HashSet<string> Selected) Resolve(
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
        Rule rule =
            policy.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, set.Rule.Id, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The policy carries no rule '{set.Rule.Id}'.", nameof(policy));
        return (rule, policy.Bindings[bindingIndex], new HashSet<string>(rows.Select(row => row.Id), StringComparer.Ordinal));
    }

    private static MarginGate? Gate(Rule rule, ProviderBinding binding, double? below) =>
        below is { } value ? new MarginGate(KindOf(rule, binding), value) : null;

    private static GatePoint Point(
        ReplaySet set,
        Policy policy,
        int bindingIndex,
        Rule rule,
        MarginGate? gate,
        HashSet<string> selected)
    {
        List<RowOutcome> outcomes = Outcomes(set, Variant(policy, bindingIndex, rule.Id, gate), rule, selected);
        OutcomeCounts counts = OutcomeCounts.Compute(outcomes);
        return new GatePoint(gate?.Below, counts.Abstained, counts.AbstentionRate ?? 0, counts.Classified, Accuracy(outcomes, rule));
    }

    private static Policy Variant(Policy policy, int bindingIndex, string ruleId, MarginGate? gate)
    {
        List<ProviderBinding> bindings = [.. policy.Bindings];
        ProviderBinding binding = bindings[bindingIndex];
        bindings[bindingIndex] = binding with
        {
            OperatingPoints =
            [
                .. binding.OperatingPoints.Select(point =>
                    string.Equals(point.RuleId, ruleId, StringComparison.Ordinal) ? point with { Gate = gate } : point),
            ],
        };
        return policy with { Bindings = bindings };
    }

    private static List<RowOutcome> Outcomes(ReplaySet set, Policy variant, Rule rule, HashSet<string> selected)
    {
        List<RowOutcome> outcomes = [];
        foreach (EvaluatedRow row in set.Evaluate(variant))
        {
            if (selected.Contains(row.Row.Id))
            {
                outcomes.Add(RowCounting.Classify(row, rule));
            }
        }

        return outcomes;
    }

    private static double? Accuracy(IReadOnlyList<RowOutcome> outcomes, Rule rule) => rule switch
    {
        BooleanRule boolean => RungMetrics.Compute(outcomes, boolean)[0].Matrix.Accuracy,
        ChoiceRule choice => MulticlassConfusion.Compute(outcomes, choice).Accuracy,
        ScoreRule score => MulticlassConfusion.Compute(outcomes, score).Accuracy,
        _ => throw new ArgumentException($"Rule '{rule.Id}' is not a Boolean, Choice or Score rule.", nameof(rule)),
    };

    private static bool Satisfies(GatePoint point, GateConstraint constraint) =>
        constraint.Kind == GateConstraintKind.MaxAbstain
            ? point.AbstentionRate <= constraint.Value
            : point.Accuracy is { } accuracy && accuracy >= constraint.Value;

    private static double? TotalViolation(GatePoint point, IEnumerable<GateConstraint> constraints)
    {
        double total = 0;
        foreach (GateConstraint constraint in constraints)
        {
            if (constraint.Kind == GateConstraintKind.MaxAbstain)
            {
                total += Math.Max(0, point.AbstentionRate - constraint.Value);
            }
            else if (point.Accuracy is { } accuracy)
            {
                total += Math.Max(0, constraint.Value - accuracy);
            }
            else
            {
                return null;
            }
        }

        return total;
    }
}
