using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class ThresholdCurveTests
{
    // Ten flagged-answer values, two decimals each, as the hosted provider reports them.
    private static readonly double[] _values = [0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85, 0.95];

    private static readonly string[] _labels =
        ["false", "false", "false", "true", "false", "true", "false", "true", "true", "true"];

    [Fact]
    public async Task Curve_Has_One_Point_Per_Observed_Flagged_Value_And_The_Grid_Points_For_Probability()
    {
        using TempFile probabilityFile = TempFile.Write("");
        using TempFile scoreFile = TempFile.Write("");
        Policy onProbability = Ladder(EvidenceKind.Probability, 0.6, 0.9);
        Policy onScore = Ladder(EvidenceKind.Score, 0.6, 0.9);
        (ReplaySet probabilitySet, IReadOnlyList<DatasetRow> rows) =
            await LoadAsync(probabilityFile, onProbability, Rows(EvidenceKind.Probability, _values));
        (ReplaySet scoreSet, IReadOnlyList<DatasetRow> scoreRows) =
            await LoadAsync(scoreFile, onScore, Rows(EvidenceKind.Score, _values));

        IReadOnlyList<RungCurve> probability = ThresholdCurve.Compute(probabilitySet, onProbability, 0, rows);
        IReadOnlyList<RungCurve> score = ThresholdCurve.Compute(scoreSet, onScore, 0, scoreRows);

        probability.Select(curve => curve.Rung).Should().Equal(Verdict.Warn, Verdict.Deny);
        foreach (RungCurve curve in probability)
        {
            // The ten observed values are themselves on the 0.05 grid, so the grid adds the other eleven.
            curve.Points.Should().HaveCount(21);
            curve.Points.Select(point => point.Threshold).Should().BeInAscendingOrder();
            curve.Points.Where(point => point.Observed).Select(point => point.Threshold).Should().Equal(_values);
            curve.Points.Where(point => !point.Observed).Select(point => point.Threshold).Should()
                .Equal(0.00, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 1.00);
        }

        // A score is on the provider's own scale, where a 0.05 grid means nothing, so only what was seen
        // is a candidate.
        foreach (RungCurve curve in score)
        {
            curve.Points.Should().HaveCount(10);
            curve.Points.Should().AllSatisfy(point => point.Observed.Should().BeTrue());
            curve.Points.Select(point => point.Threshold).Should().Equal(_values);
        }
    }

    [Fact]
    public async Task Curve_Points_Come_From_Replaying_Variants_So_A_Gated_Row_Stays_Gated_At_Every_Threshold()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Ladder(EvidenceKind.Probability, 0.6, 0.9, gate: 0.2);

        // r04 sits at 0.52, a margin of 0.04 under a gate of 0.2, and it is the last binding, so the rule
        // Every other value keeps a margin well clear of the gate; 0.40 would not, because 0.6 - 0.4 is
        // 0.19999999999999996 in binary floating point and the gate would take that row too.
        // abstains on it however the ladder is cut.
        double[] values = [0.05, 0.15, 0.25, 0.35, 0.52, 0.30, 0.65, 0.75, 0.85, 0.95];
        (ReplaySet set, IReadOnlyList<DatasetRow> rows) =
            await LoadAsync(file, policy, Rows(EvidenceKind.Probability, values));

        IReadOnlyList<RungCurve> curves = ThresholdCurve.Compute(set, policy, 0, rows);

        curves.SelectMany(curve => curve.Points).Should().AllSatisfy(point =>
        {
            BinaryConfusion matrix = point.Matrix;
            int counted = matrix.TruePositives + matrix.FalsePositives + matrix.TrueNegatives + matrix.FalseNegatives;
            counted.Should().Be(9);
            point.Outcomes.Rows.Should().Be(10);
            point.Outcomes.Classified.Should().Be(9);
            point.Outcomes.Abstained.Should().Be(1);
        });
    }

    [Fact]
    public async Task Curve_Variants_Keep_Every_Binding_So_A_Row_Moved_By_The_Gate_Is_Decided_By_The_Next_Binding()
    {
        using TempFile file = TempFile.Write("");
        BooleanRule rule = Samples.Flagged();
        Threshold[] ladder =
            [new(Verdict.Warn, EvidenceKind.Probability, 0.6), new(Verdict.Deny, EvidenceKind.Probability, 0.9)];
        Policy policy = new(
            "guard",
            PolicyMode.Enforce,
            [rule],
            [
                new ProviderBinding(
                    "local",
                    [new RuleOperatingPoint(rule.Id, ladder, new MarginGate(EvidenceKind.Probability, 0.2))]),
                new ProviderBinding("hosted", [new RuleOperatingPoint(rule.Id, ladder)]),
            ],
            FailureBehavior.Deny);

        // r00 is under the local gate and moves to the hosted binding, which denies it at the file's 0.9
        // whatever the sweep does to local. The other nine decide locally and never reach hosted.
        double[] local = [0.52, 0.05, 0.15, 0.25, 0.35, 0.30, 0.65, 0.75, 0.85, 0.95];
        string[] labels =
            ["true", "false", "false", "false", "true", "false", "true", "false", "true", "true"];
        (ReplaySet set, IReadOnlyList<DatasetRow> rows) = await LoadAsync(
            file,
            policy,
            [
                .. local.Select((value, index) => (
                    labels[index],
                    new[] { Answer(EvidenceKind.Probability, value), Answer(EvidenceKind.Probability, 0.95, "hosted") })),
            ]);

        IReadOnlyList<RungCurve> curves = ThresholdCurve.Compute(set, policy, 0, rows);

        CurvePoint everythingDenies = curves[1].Points.Single(point => point.Threshold == 0.00);
        CurvePoint nothingLocalDenies = curves[1].Points.Single(point => point.Threshold == 1.00);

        // At a local cut of 0, every locally decided row denies and r00 still denies through hosted.
        (everythingDenies.Matrix.TruePositives + everythingDenies.Matrix.FalsePositives).Should().Be(10);

        // At a local cut of 1, no local row reaches it, and the one row hosted decides is the only positive
        // prediction left. It is labelled true, so it is the single true positive.
        nothingLocalDenies.Matrix.TruePositives.Should().Be(1);
        nothingLocalDenies.Matrix.FalsePositives.Should().Be(0);
        curves.SelectMany(curve => curve.Points).Should()
            .AllSatisfy(point => point.Outcomes.Abstained.Should().Be(0));
    }

    [Fact]
    public async Task Curve_Candidates_Are_Read_On_The_Binding_Declared_Kind_Only()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Ladder(EvidenceKind.Score, 0.6, 0.9);

        // Every attempt carries a logit beside its score. The operating point declares score, so the logits
        // are numbers this sweep has no business cutting on.
        double[] logits = [1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5, 9.5, 10.5];
        (ReplaySet set, IReadOnlyList<DatasetRow> rows) = await LoadAsync(
            file,
            policy,
            [
                .. _values.Select((value, index) => (
                    _labels[index],
                    new[]
                    {
                        Samples.Answer(
                            new BooleanValue(value >= 0.5),
                            "local",
                            Two(EvidenceKind.Score, value),
                            Two(EvidenceKind.Logit, logits[index])),
                    })),
            ]);

        IReadOnlyList<RungCurve> curves = ThresholdCurve.Compute(set, policy, 0, rows);

        curves.Should().AllSatisfy(curve =>
        {
            curve.Points.Select(point => point.Threshold).Should().Equal(_values);
            curve.Points.Select(point => point.Threshold).Should().NotIntersectWith(logits);
        });
    }

    private static Policy Ladder(EvidenceKind kind, double warn, double deny, double? gate = null)
    {
        BooleanRule rule = Samples.Flagged();
        RuleOperatingPoint point = new(
            rule.Id,
            [new Threshold(Verdict.Warn, kind, warn), new Threshold(Verdict.Deny, kind, deny)],
            gate is { } below ? new MarginGate(kind, below) : null);
        return new Policy(
            "guard",
            PolicyMode.Enforce,
            [rule],
            [new ProviderBinding("local", [point])],
            FailureBehavior.Deny);
    }

    private static (string Label, ProviderResult[] Attempts)[] Rows(EvidenceKind kind, double[] values) =>
        [.. values.Select((value, index) => (_labels[index], new[] { Answer(kind, value) }))];

    private static ProviderResult Answer(EvidenceKind kind, double flagged, string provider = "local") =>
        Samples.Answer(new BooleanValue(flagged >= 0.5), provider, Two(kind, flagged));

    // Both answers in every entry: Core completes only a one-sided probability and calls a one-sided score
    // or logit malformed, which would bucket the whole binding as a failure instead of a decision.
    private static Evidence Two(EvidenceKind kind, double flagged) =>
        new(kind, new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["true"] = flagged,
            ["false"] = 1 - flagged,
        });

    private static async Task<(ReplaySet Set, IReadOnlyList<DatasetRow> Rows)> LoadAsync(
        TempFile file,
        Policy policy,
        (string Label, ProviderResult[] Attempts)[] rows)
    {
        string ruleId = policy.Rules[0].Id;
        DatasetRow[] dataset = [.. rows.Select((row, index) => Samples.Row($"r{index:00}", row.Label, index + 1))];
        RecordedRow[] recorded =
        [
            .. rows.Select((row, index) => Samples.Recorded(
                dataset[index].Id,
                [.. row.Attempts.Select((result, binding) => (ruleId, policy.Bindings[binding].ProviderId, result))])),
        ];
        Recording recording = await Samples.RecordAsync(file.Path, Samples.Header(policy), recorded);
        return (ReplaySet.Load(recording, Samples.Inputs(policy, Samples.Dataset(dataset)), force: false), dataset);
    }
}
