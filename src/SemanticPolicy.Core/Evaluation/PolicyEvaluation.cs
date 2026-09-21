using System.Diagnostics;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evaluation;

/// <summary>
/// The step function that turns a policy and the provider results gathered so far into either a
/// verdict or the attempts still required. It is pure — no provider, no clock, no state — so the same
/// policy and results always yield the same step, and the live evaluator and an offline replay of
/// stored results run the same code. It never asks for an attempt it holds, and it asks for at most
/// one attempt per rule at a time, because which binding comes next depends on how the previous one
/// ended.
/// </summary>
public static class PolicyEvaluation
{
    /// <summary>
    /// Evaluates the policy against the attempts held. Every rule is walked along the chain from the
    /// first binding: an attempt whose answer decides ends the rule; one that failed, abstained or broke
    /// the contract goes through the policy's failure behaviour; one whose evidence falls under the
    /// margin gate sends the rule to the next binding; a missing one is required. Once every rule has a
    /// verdict, the policy's is the most severe of them.
    /// </summary>
    /// <param name="policy">
    /// The policy. It is validated here again, so a deserialized or directly constructed policy is held
    /// to the same rules as a built one.
    /// </param>
    /// <param name="attempts">The provider results gathered so far, by rule and binding.</param>
    /// <exception cref="PolicyConfigurationException">The policy cannot be evaluated as written.</exception>
    /// <exception cref="ArgumentException">
    /// An attempt key names a rule the policy does not have or a binding outside its chain, or holds no
    /// result.
    /// </exception>
    public static EvaluationStep Evaluate(Policy policy, IReadOnlyDictionary<AttemptKey, ProviderResult> attempts)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(attempts);
        policy.Validate();
        RejectStrayKeys(policy, attempts);

        List<RuleVerdict> verdicts = new(policy.Rules.Count);
        List<AttemptKey> required = [];
        foreach (Rule rule in policy.Rules)
        {
            RuleVerdict? verdict = Walk(policy, rule, attempts, out AttemptKey missing);
            if (verdict is null)
            {
                required.Add(missing);
            }
            else
            {
                verdicts.Add(verdict);
            }
        }

        if (required.Count > 0)
        {
            return new EvaluationStep(Verdict: null, required);
        }

        Verdict evaluated = Verdict.Allow;
        foreach (RuleVerdict verdict in verdicts)
        {
            if (verdict.Verdict > evaluated)
            {
                evaluated = verdict.Verdict;
            }
        }

        Verdict effective = policy.Mode == PolicyMode.Enforce ? evaluated : Verdict.Allow;
        return new EvaluationStep(new PolicyVerdict(policy.Id, policy.Mode, effective, evaluated, verdicts), []);
    }

    private static void RejectStrayKeys(Policy policy, IReadOnlyDictionary<AttemptKey, ProviderResult> attempts)
    {
        foreach ((AttemptKey key, ProviderResult result) in attempts)
        {
            if (!policy.Rules.Any(rule => string.Equals(rule.Id, key.RuleId, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Attempt key ({key.RuleId}, {key.BindingIndex}) names a rule the policy does not have.",
                    nameof(attempts));
            }

            if (key.BindingIndex < 0 || key.BindingIndex >= policy.Bindings.Count)
            {
                throw new ArgumentException(
                    $"Attempt key ({key.RuleId}, {key.BindingIndex}) names a binding outside the policy's chain of "
                    + $"{policy.Bindings.Count}.",
                    nameof(attempts));
            }

            if (result is null)
            {
                throw new ArgumentException(
                    $"Attempt key ({key.RuleId}, {key.BindingIndex}) holds no result.",
                    nameof(attempts));
            }
        }
    }

    private static RuleVerdict? Walk(
        Policy policy,
        Rule rule,
        IReadOnlyDictionary<AttemptKey, ProviderResult> attempts,
        out AttemptKey missing)
    {
        missing = default;
        List<Attempt> trace = [];
        int last = policy.Bindings.Count - 1;
        for (int index = 0; index <= last; index++)
        {
            AttemptKey key = new(rule.Id, index);
            if (!attempts.TryGetValue(key, out ProviderResult? result))
            {
                missing = key;
                return null;
            }

            ProviderBinding binding = policy.Bindings[index];
            RuleOperatingPoint? point = OperatingPointFor(binding, rule.Id);
            ProviderOutcome effective = result.Outcome;
            Reading reading = default;
            if (effective.Status == OutcomeStatus.Success)
            {
                string? broken = Read(rule, point, result, out reading);
                if (broken is not null)
                {
                    effective = ProviderOutcome.Failure(FailureKind.Malformed, broken);
                }
            }

            Attempt Record(AttemptDisposition disposition, double? margin = null) =>
                new(index, binding.ProviderId, result, effective, margin, disposition);

            if (effective.Status != OutcomeStatus.Success)
            {
                FailureBehavior behaviour = policy.OnFailure;
                if (behaviour.Action == FailureAction.Fallback && index < last)
                {
                    trace.Add(Record(AttemptDisposition.MovedOnByFailure));
                    continue;
                }

                trace.Add(Record(AttemptDisposition.TerminatedByFailure));
                Verdict declared = behaviour.Action switch
                {
                    FailureAction.Allow => Verdict.Allow,
                    FailureAction.Deny => Verdict.Deny,
                    FailureAction.Escalate => Verdict.Escalate,
                    // Validate() guarantees a Fallback carries its verdict for an exhausted chain.
                    _ => behaviour.Then!.Value,
                };
                return Undecided(rule, declared, VerdictSource.FailureBehavior, trace);
            }

            if (point?.Gate is { } gate && reading.Margin < gate.Below)
            {
                if (index < last)
                {
                    trace.Add(Record(AttemptDisposition.MovedOnByGate, reading.Margin));
                    continue;
                }

                trace.Add(Record(AttemptDisposition.ExhaustedByGate, reading.Margin));
                return Undecided(rule, Verdict.Abstain, VerdictSource.UncertaintyExhausted, trace);
            }

            trace.Add(Record(AttemptDisposition.Decided, reading.Margin));
            return Decide(rule, point, reading, index, trace);
        }

        // Validate() guarantees at least one binding, and every pass through the loop either returns or
        // moves to the next binding.
        throw new UnreachableException();
    }

    // Every check runs before anything in a successful result is acted on, so a result that broke its
    // contract in one place is not trusted in another. What comes back is the note for the log when the
    // result is malformed: it names what was wrong, never the value, option or level the provider sent.
    private static string? Read(Rule rule, RuleOperatingPoint? point, ProviderResult result, out Reading reading)
    {
        reading = default;
        if (!string.Equals(result.Protocol, ProtocolVersion.V0, StringComparison.Ordinal))
        {
            return $"the result's protocol is not {ProtocolVersion.V0}.";
        }

        if (result.Type != rule.Type)
        {
            return $"the result answers a {result.Type} question; the rule asks a {rule.Type} one.";
        }

        if (result.Value is null)
        {
            return "a successful result carries no value.";
        }

        string? valueError = rule switch
        {
            BooleanRule => result.Value is BooleanValue ? null : "the value is not a Boolean answer.",
            ChoiceRule choice => CheckOption(choice, result.Value),
            ScoreRule score => CheckLevel(score, result.Value),
            _ => throw new UnreachableException(),
        };
        if (valueError is not null)
        {
            return valueError;
        }

        // A Boolean rule's thresholds always read evidence, and Validate() keeps the gate on the same
        // kind; a Choice or Score rule reads evidence only through its gate.
        EvidenceKind? kind = rule is BooleanRule ? point!.Thresholds[0].Kind : point?.Gate?.Kind;
        if (kind is null)
        {
            reading = new Reading(result.Value, Evidence: null, Margin: null);
            return null;
        }

        Evidence? evidence = FindEvidence(result.Evidence, kind.Value);
        if (evidence is null)
        {
            return $"the result carries no {kind} evidence, which the operating point reads.";
        }

        if (rule is BooleanRule boolean)
        {
            // A one-sided probability is completed as 1 − p. Any other kind has no complement, so with one
            // of the two keys it carries no evidence of that kind at all — even when the one key present is
            // the flagged answer's, because a score with nothing to compare against cannot be thresholded.
            evidence = EvidenceMath.WithBooleanComplement(evidence);
            bool hasTrue = evidence.Values.ContainsKey("true");
            bool hasFalse = evidence.Values.ContainsKey("false");
            if (kind != EvidenceKind.Probability && hasTrue != hasFalse)
            {
                return $"the {kind} evidence carries only one of the true and false keys.";
            }

            if (!evidence.Values.ContainsKey(FlaggedKey(boolean)))
            {
                return $"the {kind} evidence carries no value for the flagged answer.";
            }
        }

        double? margin = null;
        if (point?.Gate is not null)
        {
            margin = EvidenceMath.Margin(evidence);
            if (margin is null)
            {
                return $"the {kind} evidence has fewer than two values, so the margin gate cannot read it.";
            }
        }

        reading = new Reading(result.Value, evidence, margin);
        return null;
    }

    private static string? CheckOption(ChoiceRule rule, DecisionValue value)
    {
        if (value is not ChoiceValue choice)
        {
            return "the value is not a Choice answer.";
        }

        return rule.Options.Any(option => string.Equals(option.Key, choice.Option, StringComparison.Ordinal))
            ? null
            : "the value names an option the rule does not have.";
    }

    private static string? CheckLevel(ScoreRule rule, DecisionValue value)
    {
        if (value is not ScoreValue score)
        {
            return "the value is not a Score answer.";
        }

        int index = IndexOfLevel(rule, score.Level);
        if (index < 0)
        {
            return "the value names a level the rule does not have.";
        }

        return index == score.Index ? null : "the value's index does not match its level.";
    }

    // A verdict no provider's answer produced: nothing was read, so nothing about the reading is recorded.
    private static RuleVerdict Undecided(Rule rule, Verdict verdict, VerdictSource source, List<Attempt> trace) =>
        new(
            rule.Id,
            verdict,
            source,
            DecidingBinding: null,
            RungCrossed: null,
            EvidenceKind: null,
            EvidenceValue: null,
            trace);

    private static RuleVerdict Decide(
        Rule rule,
        RuleOperatingPoint? point,
        Reading reading,
        int index,
        List<Attempt> trace) =>
        rule switch
        {
            BooleanRule boolean => DecideBoolean(boolean, point!, reading.Evidence!, index, trace),
            ChoiceRule choice => DecideChoice(choice, (ChoiceValue)reading.Value, index, trace),
            ScoreRule score => DecideScore(score, (ScoreValue)reading.Value, index, trace),
            _ => throw new UnreachableException(),
        };

    private static RuleVerdict DecideBoolean(
        BooleanRule rule,
        RuleOperatingPoint point,
        Evidence evidence,
        int index,
        List<Attempt> trace)
    {
        double value = evidence.Values[FlaggedKey(rule)];
        Verdict verdict = Verdict.Allow;
        foreach (Threshold threshold in point.Thresholds)
        {
            if (value >= threshold.AtOrAbove && threshold.Verdict > verdict)
            {
                verdict = threshold.Verdict;
            }
        }

        return new RuleVerdict(
            rule.Id,
            verdict,
            VerdictSource.Threshold,
            index,
            RungCrossed: verdict == Verdict.Allow ? null : verdict,
            evidence.Kind,
            value,
            trace);
    }

    private static RuleVerdict DecideChoice(ChoiceRule rule, ChoiceValue value, int index, List<Attempt> trace)
    {
        Verdict verdict = rule.Options
            .First(option => string.Equals(option.Key, value.Option, StringComparison.Ordinal))
            .Verdict;
        return new RuleVerdict(
            rule.Id,
            verdict,
            VerdictSource.OptionMap,
            index,
            RungCrossed: null,
            EvidenceKind: null,
            EvidenceValue: null,
            trace);
    }

    private static RuleVerdict DecideScore(ScoreRule rule, ScoreValue value, int index, List<Attempt> trace)
    {
        Verdict verdict = Verdict.Allow;
        foreach (ScoreRung rung in rule.Rungs)
        {
            if (IndexOfLevel(rule, rung.Level) <= value.Index && rung.Verdict > verdict)
            {
                verdict = rung.Verdict;
            }
        }

        return new RuleVerdict(
            rule.Id,
            verdict,
            VerdictSource.LevelMap,
            index,
            RungCrossed: verdict == Verdict.Allow ? null : verdict,
            EvidenceKind: null,
            EvidenceValue: null,
            trace);
    }

    private static int IndexOfLevel(ScoreRule rule, string level)
    {
        for (int i = 0; i < rule.Levels.Count; i++)
        {
            if (string.Equals(rule.Levels[i], level, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static RuleOperatingPoint? OperatingPointFor(ProviderBinding binding, string ruleId) =>
        binding.OperatingPoints.FirstOrDefault(point => string.Equals(point.RuleId, ruleId, StringComparison.Ordinal));

    // A stored result can deserialize with no evidence list at all; that reads as no evidence of any
    // kind, like a null entry or a null Values does, so the breach goes through the failure behaviour.
    private static Evidence? FindEvidence(IReadOnlyList<Evidence>? evidence, EvidenceKind kind) =>
        evidence?.FirstOrDefault(entry => entry is not null && entry.Kind == kind && entry.Values is not null);

    private static string FlaggedKey(BooleanRule rule) => rule.FlaggedAnswer ? "true" : "false";

    // What a success yielded once it passed the contract: its typed value, the evidence entry the
    // operating point reads (completed, for a one-sided Boolean probability), and the margin on it
    // when a gate applies.
    private readonly record struct Reading(DecisionValue Value, Evidence? Evidence, double? Margin);
}
