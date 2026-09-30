using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Evals.Sweeping;

namespace SemanticPolicy.Evals.Gating;

/// <summary>Which number of the report a requirement bounds.</summary>
public enum RequirementKind
{
    /// <summary><c>&lt;rung&gt;.min-precision=v</c>: that rung's precision is at least v.</summary>
    MinPrecision,

    /// <summary><c>&lt;rung&gt;.min-recall=v</c>: that rung's recall is at least v.</summary>
    MinRecall,

    /// <summary><c>&lt;rung&gt;.max-fpr=v</c>: that rung's false-positive rate is at most v.</summary>
    MaxFpr,

    /// <summary><c>min-accuracy=v</c>: a Choice or Score rule's accuracy is at least v.</summary>
    MinAccuracy,

    /// <summary><c>min-macro-f1=v</c>: a Choice or Score rule's macro-F1 is at least v.</summary>
    MinMacroF1,

    /// <summary><c>max-abstain=v</c>: the abstention rate over every row is at most v.</summary>
    MaxAbstain,

    /// <summary><c>max-failure-rate=v</c>: the failure rate over every row is at most v.</summary>
    MaxFailureRate,
}

/// <summary>
/// A number the report must reach for the verb to exit 0, as given to <c>--require</c>. Unlike a sweep's constraint it
/// chooses nothing: it reads the report at the policy file's own numbers, so a change to the policy that makes the
/// result on a committed recording worse fails the build that replays it.
/// </summary>
/// <param name="Text">The requirement as it was typed, which is how every line and message names it.</param>
/// <param name="Rung">The ladder rung it reads; <see langword="null"/> for one about the whole rule.</param>
/// <param name="Kind">The number it bounds.</param>
/// <param name="Goal">The bound, in [0, 1].</param>
public sealed record Requirement(string Text, Verdict? Rung, RequirementKind Kind, double Goal)
{
    private const string _option = "--require";
    private const string _rungs = "the rungs are warn, escalate and deny";
    private const string _expected = "<rung>.min-precision=<v>, <rung>.min-recall=<v>, <rung>.max-fpr=<v>, min-accuracy=<v>, "
        + "min-macro-f1=<v>, max-abstain=<v> or max-failure-rate=<v>";

    private static readonly Dictionary<string, RequirementKind> _kinds = new(StringComparer.Ordinal)
    {
        ["min-precision"] = RequirementKind.MinPrecision,
        ["min-recall"] = RequirementKind.MinRecall,
        ["max-fpr"] = RequirementKind.MaxFpr,
        ["min-accuracy"] = RequirementKind.MinAccuracy,
        ["min-macro-f1"] = RequirementKind.MinMacroF1,
        ["max-abstain"] = RequirementKind.MaxAbstain,
        ["max-failure-rate"] = RequirementKind.MaxFailureRate,
    };

    /// <summary>Reads one token of <c>--require</c>, before any file is opened.</summary>
    /// <param name="token">The token, <c>[&lt;rung&gt;.]name=value</c>.</param>
    /// <exception cref="EvalsException">
    /// The name is unknown, the value is not a number in [0, 1], a rung's rate has no rung, a rule's has one, or the
    /// prefix is not a rung; the message quotes the token and says which.
    /// </exception>
    public static Requirement Parse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        (string qualified, double goal) = ConstraintToken.Split(_option, token, _expected, "requirement");
        int dot = qualified.IndexOf('.', StringComparison.Ordinal);
        string name = qualified[(dot + 1)..];
        if (!_kinds.TryGetValue(name, out RequirementKind kind))
        {
            throw ConstraintToken.Malformed(_option, token, _expected, "requirement");
        }

        Requirement requirement = new(token, null, kind, goal);
        if (dot < 0)
        {
            return requirement.AtRung
                ? throw new EvalsException($"{_option} '{token}': {name} needs a rung, as in deny.{name}; {_rungs}.")
                : requirement;
        }

        if (!requirement.AtRung)
        {
            throw new EvalsException($"{_option} '{token}': {name} takes no rung; it is measured on the rule as a whole.");
        }

        // Only the three verdicts a ladder can hold: allow and abstain are verdicts too, but never a rung.
        string prefix = qualified[..dot];
        Verdict rung = prefix switch
        {
            "warn" => Verdict.Warn,
            "escalate" => Verdict.Escalate,
            "deny" => Verdict.Deny,
            _ => throw new EvalsException($"{_option} '{token}': '{prefix}' is not a rung; {_rungs}."),
        };
        return requirement with { Rung = rung };
    }

    /// <summary>
    /// Checks that the rule has what this requirement reads. It runs once the policy is loaded, and in <c>run</c>
    /// before any provider is called, so a requirement that can never be measured costs nothing.
    /// </summary>
    /// <param name="rule">The rule the report is about.</param>
    /// <exception cref="EvalsException">
    /// A rung's rate on a Choice or Score rule, a rung the ladder lacks, or a Choice or Score rule's rate on a Boolean
    /// rule; the message quotes the requirement and names the rule.
    /// </exception>
    public void EnsureFits(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        string type = Names.Camel(rule.Type);
        if (AtRung && rule is not BooleanRule)
        {
            throw new EvalsException($"{_option} '{Text}': {Name} is for a Boolean rule's rung, and rule '{rule.Id}' is {type}.");
        }

        if (rule is BooleanRule boolean && Rung is { } rung && !boolean.Ladder.Contains(rung))
        {
            throw new EvalsException(
                $"{_option} '{Text}': rule '{rule.Id}' has no {Names.Camel(rung)} rung; its ladder is "
                + $"{string.Join(", ", boolean.Ladder.Select(verdict => Names.Camel(verdict)))}.");
        }

        if (Kind is RequirementKind.MinAccuracy or RequirementKind.MinMacroF1 && rule is BooleanRule)
        {
            throw new EvalsException($"{_option} '{Text}': {Name} is for a Choice or Score rule, and rule '{rule.Id}' is {type}.");
        }
    }

    /// <summary>
    /// Judges the requirement on the report it follows, which is measured on the rows after <c>--where</c>, the rule
    /// <c>--rule</c> selects and the chain's verdicts.
    /// </summary>
    /// <param name="report">The report, of a rule <see cref="EnsureFits"/> accepted.</param>
    /// <returns>The value, its interval where it has one, and whether it passed and warned.</returns>
    public RequirementResult Judge(ReportSection report)
    {
        ArgumentNullException.ThrowIfNull(report);
        (double? value, Interval? interval, (int, int)? counts) = Measure(report);
        bool minimum = Kind is RequirementKind.MinPrecision or RequirementKind.MinRecall
            or RequirementKind.MinAccuracy or RequirementKind.MinMacroF1;
        bool passed = value is { } measured && (minimum ? measured >= Goal : measured <= Goal);
        bool warned = passed && interval is { } bounds && (minimum ? bounds.Lower < Goal : bounds.Upper > Goal);
        return new RequirementResult(Text, Goal, value, interval, passed, warned) { Counts = counts, Minimum = minimum };
    }

    private (double? Value, Interval? Interval, (int Successes, int Trials)? Counts) Measure(ReportSection report)
    {
        switch (Kind)
        {
            case RequirementKind.MinPrecision:
                BinaryConfusion precision = Matrix(report);
                return (precision.Precision, precision.PrecisionInterval,
                    (precision.TruePositives, precision.TruePositives + precision.FalsePositives));
            case RequirementKind.MinRecall:
                BinaryConfusion recall = Matrix(report);
                return (recall.Recall, recall.RecallInterval, (recall.TruePositives, recall.TruePositives + recall.FalseNegatives));
            case RequirementKind.MaxFpr:
                BinaryConfusion fpr = Matrix(report);
                return (fpr.FalsePositiveRate, fpr.FalsePositiveRateInterval, (fpr.FalsePositives, fpr.FalsePositives + fpr.TrueNegatives));
            case RequirementKind.MinAccuracy:
                MulticlassConfusion accuracy = Classes(report);
                return (accuracy.Accuracy, accuracy.AccuracyInterval, (accuracy.Correct, accuracy.Counted));
            case RequirementKind.MinMacroF1:
                return (Classes(report).MacroF1, null, null);
            case RequirementKind.MaxAbstain:
                OutcomeCounts abstained = report.Outcomes;
                return (abstained.AbstentionRate, abstained.AbstentionRateInterval, (abstained.Abstained, abstained.Rows));
            default:
                OutcomeCounts failed = report.Outcomes;
                return (failed.FailureRate, failed.FailureRateInterval, (failed.Failed, failed.Rows));
        }
    }

    private BinaryConfusion Matrix(ReportSection report) =>
        report.Rungs?.FirstOrDefault(rung => rung.Rung == Rung)?.Matrix
        ?? throw new InvalidOperationException($"The report has no {Rung} rung, which EnsureFits refuses before any report.");

    private static MulticlassConfusion Classes(ReportSection report) =>
        report.Classes ?? throw new InvalidOperationException("The report has no class table, which EnsureFits refuses before any report.");

    private bool AtRung => Kind is RequirementKind.MinPrecision or RequirementKind.MinRecall or RequirementKind.MaxFpr;

    // The name as it is typed after the rung, if any.
    private string Name => _kinds.First(pair => pair.Value == Kind).Key;
}
