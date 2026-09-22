using System.Collections.ObjectModel;
using System.Text.Json;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

// What a recording and replay test builds inline. Every policy here is "guard": the rules given, the
// providers given in chain order, and on each provider Warn at 0.6 / Deny at 0.9 on probability for every
// Boolean rule plus the gate when one is asked for. A Choice or Score rule takes an operating point only
// when there is a gate, because Core allows a binding to carry none for it.
internal static class Samples
{
    public const string DatasetPath = "rows.jsonl";
    public const string DatasetSha = "6f4b1d2a9c0e8b7f3d5a1c2e4b6d8f0a2c4e6b8d0f1a3c5e7b9d1f3a5c7e9b0d";
    public const string Injection = "prompt-injection";

    public static Policy Guard(
        FailureBehavior onFailure,
        string[] providers,
        Rule[] rules,
        PolicyMode mode = PolicyMode.Enforce,
        double? gate = null,
        TimeSpan? budget = null)
    {
        List<ProviderBinding> bindings = [];
        foreach (string provider in providers)
        {
            List<RuleOperatingPoint> points = [];
            foreach (Rule rule in rules)
            {
                MarginGate? margin = gate is { } below ? new MarginGate(EvidenceKind.Probability, below) : null;
                if (rule is BooleanRule boolean)
                {
                    points.Add(new RuleOperatingPoint(rule.Id, [.. Ladder(boolean)], margin));
                }
                else if (margin is not null)
                {
                    points.Add(new RuleOperatingPoint(rule.Id, [], margin));
                }
            }

            bindings.Add(new ProviderBinding(provider, points));
        }

        return new Policy("guard", mode, rules, bindings, onFailure, budget);
    }

    private static IEnumerable<Threshold> Ladder(BooleanRule rule) =>
        rule.Ladder.Select(rung => new Threshold(rung, EvidenceKind.Probability, rung == Verdict.Deny ? 0.9 : 0.6));

    public static BooleanRule Flagged(string id = Injection, bool answer = true, string question = "question-b") =>
        new(id, question, answer, [Verdict.Warn, Verdict.Deny]);

    public static ChoiceRule Route(string id = "route", string question = "question-c") =>
        new(id, question, [
            new ChoiceOption("allow", "description-a", Verdict.Allow),
            new ChoiceOption("review", "description-b", Verdict.Escalate),
            new ChoiceOption("deny", "description-c", Verdict.Deny)]);

    public static ScoreRule Severity(string id = "severity", string question = "question-d") =>
        new(id, question, ["harmless", "moderate", "serious"], [new ScoreRung("serious", Verdict.Deny)]);

    public static ProviderResult BooleanAnswer(double pTrue, string provider = "local") =>
        Answer(new BooleanValue(pTrue >= 0.5), provider, Probability(("true", pTrue), ("false", 1 - pTrue)));

    public static ProviderResult Answer(DecisionValue value, string provider = "local", params Evidence[] evidence)
    {
        DecisionType type = value switch
        {
            BooleanValue => DecisionType.Boolean,
            ChoiceValue => DecisionType.Choice,
            _ => DecisionType.Score,
        };
        return new ProviderResult(type, ProviderOutcome.Success, value, evidence, Metadata(provider));
    }

    public static ProviderResult Failed(
        FailureKind kind,
        string provider = "local",
        DecisionType type = DecisionType.Boolean) =>
        ProviderResult.Failed(type, kind, "no answer", Metadata(provider));

    public static ProviderMetadata Metadata(string provider) => new(provider, "model-" + provider, 5);

    public static Evidence Probability(params (string Key, double Value)[] values) =>
        new(EvidenceKind.Probability, values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static DatasetRow Row(string id, string label, int line = 1) =>
        new(id, line, SemanticContext.FromText("context"), Label(label), ReadOnlyDictionary<string, JsonElement>.Empty);

    public static RowLabel Label(string text) => text switch
    {
        "ambiguous" => new RowLabel(RowLabelKind.Ambiguous, null),
        "abstain" => new RowLabel(RowLabelKind.Abstain, null),
        _ => new RowLabel(RowLabelKind.Answer, text),
    };

    public static Dataset Dataset(params DatasetRow[] rows) => new(DatasetPath, DatasetSha, rows);

    public static LoadedInputs Inputs(Policy policy, Dataset dataset, string? ruleId = null)
    {
        Rule rule = ruleId is null ? policy.Rules[0] : policy.Rules.Single(candidate => candidate.Id == ruleId);
        return new LoadedInputs(
            policy,
            rule,
            [dataset],
            dataset.Rows,
            dataset.Rows.Count,
            [],
            new SplitSelection(dataset.Rows, dataset.Rows, SplitSource.None));
    }

    public static RecordingHeader Header(Policy policy, string sha = DatasetSha) =>
        new(
            RecordingHeader.FormatV0,
            policy,
            [new RecordedDataset(DatasetPath, sha, Split: null)],
            [.. policy.Bindings.Select(binding => new RecordedProvider(binding.ProviderId, "model-" + binding.ProviderId))],
            RecordingHeader.CurrentToolVersion,
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
            Parallel: 4,
            TimeSpan.FromSeconds(30));

    public static RecordedRow Recorded(string id, params (string Rule, string Provider, ProviderResult Result)[] attempts)
    {
        Dictionary<string, IReadOnlyDictionary<string, ProviderResult>> byRule = new(StringComparer.Ordinal);
        foreach (var rule in attempts.GroupBy(attempt => attempt.Rule, StringComparer.Ordinal))
        {
            byRule[rule.Key] = rule.ToDictionary(
                attempt => attempt.Provider,
                attempt => attempt.Result,
                StringComparer.Ordinal);
        }

        return new RecordedRow(id, byRule);
    }

    public static async Task<Recording> RecordAsync(string path, RecordingHeader header, params RecordedRow[] rows)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using (RecordingWriter writer = await RecordingWriter.CreateAsync(path, header, cancellation))
        {
            foreach (RecordedRow row in rows)
            {
                await writer.WriteAsync(row, cancellation);
            }
        }

        return RecordingReader.Read(path);
    }
}
