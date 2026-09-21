using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// What a policy is: its rules, the ordered chain of providers that answer them with each provider's
/// operating points, the mode, what happens when a provider does not decide, and an optional time
/// budget over the whole chain. A policy is a value — immutable, serializable, passed to the evaluator
/// or registered by id — and it is not enforcement: evaluation ends with a verdict, and acting on it
/// is the application's code. The constructor holds what it is given; <see cref="Validate"/> is what
/// decides whether the policy can be evaluated.
/// </summary>
/// <param name="Id">The id the policy is registered and reported under.</param>
/// <param name="Mode">Whether the verdict is the policy's decision or only recorded.</param>
/// <param name="Rules">The questions the policy asks: at least one, with distinct ids.</param>
/// <param name="Bindings">
/// The providers that answer them, in the order they are tried: at least one, with distinct provider ids.
/// </param>
/// <param name="OnFailure">What the policy does when a provider does not decide.</param>
/// <param name="Budget">
/// The time the whole chain may take, or <see langword="null"/> for no budget. Expiry counts as a
/// provider failure and goes through <paramref name="OnFailure"/>.
/// </param>
public sealed record Policy(
    string Id,
    PolicyMode Mode,
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<ProviderBinding> Bindings,
    FailureBehavior OnFailure,
    TimeSpan? Budget = null)
{
    /// <summary>The id the policy is registered and reported under.</summary>
    public string Id { get; init; } = Id ?? throw new ArgumentNullException(nameof(Id));

    /// <summary>The questions the policy asks: at least one, with distinct ids.</summary>
    public IReadOnlyList<Rule> Rules { get; init; } = Rules ?? throw new ArgumentNullException(nameof(Rules));

    /// <summary>
    /// The providers that answer them, in the order they are tried: at least one, with distinct provider ids.
    /// </summary>
    public IReadOnlyList<ProviderBinding> Bindings { get; init; } =
        Bindings ?? throw new ArgumentNullException(nameof(Bindings));

    /// <summary>What the policy does when a provider does not decide.</summary>
    public FailureBehavior OnFailure { get; init; } = OnFailure ?? throw new ArgumentNullException(nameof(OnFailure));

    /// <summary>Starts a rule: <c>Policy.Rule("id").Boolean("...").WhenTrue(Verdict.Warn, Verdict.Deny)</c>.</summary>
    /// <param name="id">The rule's id, distinct within the policy it goes into.</param>
    public static RuleBuilder Rule(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new RuleBuilder(id);
    }

    /// <summary>
    /// Starts a policy:
    /// <c>Policy.Define("id").Enforce().Rule(rule).Using("provider", b => ...).OnFailure(...).Build()</c>.
    /// </summary>
    /// <param name="id">The policy's id.</param>
    public static PolicyBuilder Define(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new PolicyBuilder(id);
    }

    /// <summary>
    /// Checks that the policy can be evaluated. <see cref="PolicyBuilder.Build"/> runs it, and the
    /// runtime runs it again at first use, so a policy that was deserialized or constructed directly is
    /// held to the same rules. It reads the policy and nothing else: whether the named providers exist
    /// and declare the evidence kinds the operating points read is checked against the registry, not here.
    /// </summary>
    /// <exception cref="PolicyConfigurationException">
    /// The policy id is empty; there are no rules, or two share an id; there are no bindings, or a
    /// provider id is empty or repeated; a Fallback has no verdict for an exhausted chain, or one that is
    /// not Allow, Deny or Escalate, or another action carries one; the budget is not greater than zero.
    /// A rule has an empty id or question. A Boolean ladder is empty, holds Allow or Abstain, repeats a
    /// verdict or does not increase in severity. A Choice rule has fewer than two options, a repeated or
    /// empty key, an empty description or an Abstain verdict. A Score rule has fewer than two or more than
    /// ten levels, a repeated or empty level, no rungs, a rung on an unknown or repeated level, a rung of
    /// Allow or Abstain, or rungs whose severity does not increase along the scale. A binding has an
    /// operating point for a rule the policy does not have, or two for one rule; a Boolean rule has no
    /// operating point on it, or one with no thresholds, or thresholds that are not exactly one per ladder
    /// rung, or values that do not increase with severity; an operating point reads more than one evidence
    /// kind; a Probability threshold is outside [0, 1]; a gate is not greater than zero; a Choice or Score
    /// operating point carries thresholds. The message names the ids and the error, never a question, an
    /// option or a level.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw Fail("the policy id is empty.");
        }

        if (Rules.Count == 0)
        {
            throw Fail("the policy has no rules.");
        }

        if (Bindings.Count == 0)
        {
            throw Fail("the policy has no bindings.");
        }

        ValidateFailureBehavior();
        if (Budget is { } budget && budget <= TimeSpan.Zero)
        {
            throw Fail("the budget must be greater than zero.");
        }

        Dictionary<string, Rule> rules = new(Rules.Count, StringComparer.Ordinal);
        foreach (Rule rule in Rules)
        {
            if (rule is null)
            {
                throw Fail("a rule is null.");
            }

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                throw Fail("a rule has an empty id.");
            }

            if (!rules.TryAdd(rule.Id, rule))
            {
                throw Fail("the rule id is repeated.", rule.Id);
            }

            ValidateRule(rule);
        }

        HashSet<string> providers = new(StringComparer.Ordinal);
        foreach (ProviderBinding binding in Bindings)
        {
            if (binding is null)
            {
                throw Fail("a binding is null.");
            }

            if (string.IsNullOrWhiteSpace(binding.ProviderId))
            {
                throw Fail("a binding has an empty provider id.");
            }

            if (!providers.Add(binding.ProviderId))
            {
                throw Fail("the provider id is repeated in the chain.", providerId: binding.ProviderId);
            }

            ValidateBinding(binding, rules);
        }
    }

    private void ValidateFailureBehavior()
    {
        if (OnFailure.Action == FailureAction.Fallback)
        {
            if (OnFailure.Then is null)
            {
                throw Fail("Fallback needs a verdict for an exhausted chain: Allow, Deny or Escalate.");
            }

            if (OnFailure.Then is not (Verdict.Allow or Verdict.Deny or Verdict.Escalate))
            {
                throw Fail("the Fallback verdict must be Allow, Deny or Escalate.");
            }
        }
        else if (OnFailure.Then is not null)
        {
            throw Fail("only Fallback carries a verdict for an exhausted chain.");
        }
    }

    private void ValidateRule(Rule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Question))
        {
            throw Fail("the question is empty.", rule.Id);
        }

        switch (rule)
        {
            case BooleanRule boolean:
                ValidateLadder(boolean);
                break;
            case ChoiceRule choice:
                ValidateOptions(choice);
                break;
            case ScoreRule score:
                ValidateScale(score);
                break;
            default:
                throw Fail("the rule is not a Boolean, Choice or Score rule.", rule.Id);
        }
    }

    private void ValidateLadder(BooleanRule rule)
    {
        IReadOnlyList<Verdict> ladder = rule.Ladder;
        if (ladder.Count == 0)
        {
            throw Fail("the ladder is empty.", rule.Id);
        }

        for (int i = 0; i < ladder.Count; i++)
        {
            if (ladder[i] is Verdict.Allow or Verdict.Abstain)
            {
                throw Fail("a ladder rung must be Warn, Escalate or Deny.", rule.Id);
            }

            if (i > 0 && ladder[i] == ladder[i - 1])
            {
                throw Fail("the ladder repeats a verdict.", rule.Id);
            }

            if (i > 0 && ladder[i] < ladder[i - 1])
            {
                throw Fail("the ladder must increase in severity, least severe first.", rule.Id);
            }
        }
    }

    private void ValidateOptions(ChoiceRule rule)
    {
        if (rule.Options.Count < 2)
        {
            throw Fail("a Choice rule needs at least two options.", rule.Id);
        }

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (ChoiceOption option in rule.Options)
        {
            if (option is null)
            {
                throw Fail("an option is null.", rule.Id);
            }

            if (string.IsNullOrWhiteSpace(option.Key))
            {
                throw Fail("an option has an empty key.", rule.Id);
            }

            if (string.IsNullOrWhiteSpace(option.Description))
            {
                throw Fail("an option has an empty description.", rule.Id);
            }

            if (option.Verdict == Verdict.Abstain)
            {
                throw Fail("an option verdict must be Allow, Warn, Escalate or Deny.", rule.Id);
            }

            if (!keys.Add(option.Key))
            {
                throw Fail("an option key is repeated.", rule.Id);
            }
        }
    }

    private void ValidateScale(ScoreRule rule)
    {
        IReadOnlyList<string> levels = rule.Levels;
        if (levels.Count is < 2 or > 10)
        {
            throw Fail("a Score rule needs two to ten levels.", rule.Id);
        }

        Dictionary<string, int> indexOf = new(levels.Count, StringComparer.Ordinal);
        for (int i = 0; i < levels.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(levels[i]))
            {
                throw Fail("a level is empty.", rule.Id);
            }

            if (!indexOf.TryAdd(levels[i], i))
            {
                throw Fail("a level is repeated.", rule.Id);
            }
        }

        if (rule.Rungs.Count == 0)
        {
            throw Fail("a Score rule needs at least one rung.", rule.Id);
        }

        Verdict?[] rungAt = new Verdict?[levels.Count];
        foreach (ScoreRung rung in rule.Rungs)
        {
            if (rung is null)
            {
                throw Fail("a rung is null.", rule.Id);
            }

            if (rung.Verdict is Verdict.Allow or Verdict.Abstain)
            {
                throw Fail("a rung must be Warn, Escalate or Deny.", rule.Id);
            }

            if (rung.Level is null || !indexOf.TryGetValue(rung.Level, out int index))
            {
                throw Fail("a rung names a level the rule does not have.", rule.Id);
            }

            if (rungAt[index] is not null)
            {
                throw Fail("two rungs name one level.", rule.Id);
            }

            rungAt[index] = rung.Verdict;
        }

        // Read in scale order, each rung must outrank the one below it, or a higher level would mean less.
        Verdict reached = Verdict.Allow;
        foreach (Verdict? rung in rungAt)
        {
            if (rung is null)
            {
                continue;
            }

            if (rung <= reached)
            {
                throw Fail("rung severity must increase with the level.", rule.Id);
            }

            reached = rung.Value;
        }
    }

    private void ValidateBinding(ProviderBinding binding, Dictionary<string, Rule> rules)
    {
        string providerId = binding.ProviderId;
        HashSet<string> covered = new(StringComparer.Ordinal);
        foreach (RuleOperatingPoint point in binding.OperatingPoints)
        {
            if (point is null)
            {
                throw Fail("an operating point is null.", providerId: providerId);
            }

            if (!rules.TryGetValue(point.RuleId, out Rule? rule))
            {
                throw Fail("the operating point names a rule the policy does not have.", point.RuleId, providerId);
            }

            if (!covered.Add(point.RuleId))
            {
                throw Fail("the rule has two operating points on this binding.", point.RuleId, providerId);
            }

            ValidateOperatingPoint(point, rule, providerId);
        }

        foreach (Rule rule in Rules)
        {
            if (rule is BooleanRule && !covered.Contains(rule.Id))
            {
                throw Fail("a Boolean rule needs an operating point on every binding.", rule.Id, providerId);
            }
        }
    }

    private void ValidateOperatingPoint(RuleOperatingPoint point, Rule rule, string providerId)
    {
        string ruleId = point.RuleId;

        // Negated comparisons throughout, so that NaN fails the check instead of slipping past it.
        if (point.Gate is { } gate && !(gate.Below > 0))
        {
            throw Fail("the margin gate must be greater than zero.", ruleId, providerId);
        }

        if (rule is not BooleanRule boolean)
        {
            if (point.Thresholds.Count > 0)
            {
                throw Fail(
                    "a Choice or Score operating point carries only a gate; thresholds belong to a Boolean rule.",
                    ruleId,
                    providerId);
            }

            return;
        }

        if (point.Thresholds.Count == 0)
        {
            throw Fail("a Boolean rule needs a threshold per ladder rung.", ruleId, providerId);
        }

        // Exactly the ladder: a rung with no threshold could never be reached, and a threshold with no
        // rung would reach a verdict the rule never promised.
        HashSet<Verdict> rungs = [.. boolean.Ladder];
        HashSet<Verdict> thresholded = [];
        foreach (Threshold threshold in point.Thresholds)
        {
            if (threshold is null)
            {
                throw Fail("a threshold is null.", ruleId, providerId);
            }

            if (!rungs.Contains(threshold.Verdict) || !thresholded.Add(threshold.Verdict))
            {
                throw Fail("the thresholds must be exactly one per ladder rung.", ruleId, providerId);
            }
        }

        if (thresholded.Count != rungs.Count)
        {
            throw Fail("the thresholds must be exactly one per ladder rung.", ruleId, providerId);
        }

        EvidenceKind kind = point.Thresholds[0].Kind;
        if (point.Gate is { } g && g.Kind != kind)
        {
            throw Fail(_oneKind, ruleId, providerId);
        }

        foreach (Threshold threshold in point.Thresholds)
        {
            if (threshold.Kind != kind)
            {
                throw Fail(_oneKind, ruleId, providerId);
            }

            if (threshold.Kind == EvidenceKind.Probability && !(threshold.AtOrAbove is >= 0 and <= 1))
            {
                throw Fail("a Probability threshold must be in [0, 1].", ruleId, providerId);
            }
        }

        Threshold[] bySeverity = [.. point.Thresholds.OrderBy(threshold => threshold.Verdict)];
        for (int i = 1; i < bySeverity.Length; i++)
        {
            if (!(bySeverity[i].AtOrAbove > bySeverity[i - 1].AtOrAbove))
            {
                throw Fail("threshold values must increase with severity.", ruleId, providerId);
            }
        }
    }

    private const string _oneKind = "an operating point reads one evidence kind for all its thresholds and its gate.";

    private PolicyConfigurationException Fail(string error, string? ruleId = null, string? providerId = null) =>
        PolicyConfigurationException.For(Id, error, ruleId, providerId);
}
