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

    [Fact]
    public async Task Curve_Reads_A_Result_With_No_Usable_Evidence_As_No_Candidate_And_Counts_It_Failed()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Ladder(EvidenceKind.Score, 0.6, 0.9);

        // Three stored successes carry nothing to cut on: no list at all, a null entry, and an entry with no
        // values. Replay reads each as no evidence and hands the row to the failure behaviour, so the sweep
        // has to read them the same way rather than stop on them.
        ProviderResult answered = Answer(EvidenceKind.Score, 0.5);
        ProviderResult[] unusable =
        [
            answered with { Evidence = null! },
            answered with { Evidence = [null!] },
            answered with { Evidence = [new Evidence(EvidenceKind.Score, null!)] },
        ];
        (ReplaySet set, IReadOnlyList<DatasetRow> rows) = await LoadAsync(
            file,
            policy,
            [
                .. _values.Select((value, index) => (
                    _labels[index],
                    new[] { index < unusable.Length ? unusable[index] : Answer(EvidenceKind.Score, value) })),
            ]);

        IReadOnlyList<RungCurve> curves = ThresholdCurve.Compute(set, policy, 0, rows);

        curves.Should().AllSatisfy(curve =>
        {
            curve.Points.Select(point => point.Threshold).Should().Equal(_values[unusable.Length..]);
            curve.Points.Should().AllSatisfy(point => point.Outcomes.Failed.Should().Be(unusable.Length));
        });
    }

    [Theory]
    [InlineData(EvidenceKind.Score)]
    [InlineData(EvidenceKind.Logit)]
    [InlineData(EvidenceKind.Probability)]
    public async Task Curve_On_Hundreds_Of_Distinct_Values_Has_At_Most_101_Points_Per_Rung(EvidenceKind kind)
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Ladder(kind, 0.6, 0.9);

        // Unrounded evidence, a value of its own on every row, crowded towards zero. Near one they are under a
        // hundredth apart, so each 0.05 grid value has an observed value rounding to it; thinned by rank they are
        // not, and the grid values up there have to come back as grid points.
        double[] values = [.. Enumerable.Range(0, 300).Select(index => Math.Pow((index + 0.5) / 300, 2))];
        (ReplaySet set, IReadOnlyList<DatasetRow> rows) = await LoadAsync(file, policy, Rows(kind, values));

        IReadOnlyList<RungCurve> curves = ThresholdCurve.Compute(set, policy, 0, rows);

        curves.Select(curve => curve.Rung).Should().Equal(Verdict.Warn, Verdict.Deny);
        foreach (RungCurve curve in curves)
        {
            curve.Points.Should().HaveCountLessThanOrEqualTo(101);
            curve.Points.Select(point => point.Threshold).Should().BeInAscendingOrder();
            if (kind == EvidenceKind.Probability)
            {
                // Thinning may drop the observed value that stood for a grid value; the grid value then has to
                // come back as a grid point rather than go missing.
                foreach (double grid in Enumerable.Range(0, 21).Select(step => step / 20.0))
                {
                    curve.Points.Should().Contain(
                        point => Math.Round(point.Threshold, 2) == grid,
                        $"grid value {grid} has to stay on the {curve.Rung} curve");
                }
            }
        }
    }

    [Fact]
    public async Task Curve_Thinned_On_A_Score_Kind_Keeps_Only_Reported_Values_With_Both_Ends()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Ladder(EvidenceKind.Score, 0.6, 0.9);

        // Crowded towards zero, so a candidate spaced evenly on the scale would be a number nobody reported.
        double[] values = [.. Enumerable.Range(1, 300).Select(index => Math.Pow(index / 301.0, 3))];
        (ReplaySet set, IReadOnlyList<DatasetRow> rows) =
            await LoadAsync(file, policy, Rows(EvidenceKind.Score, values));

        IReadOnlyList<RungCurve> curves = ThresholdCurve.Compute(set, policy, 0, rows);

        curves.Should().AllSatisfy(curve =>
        {
            curve.Points.Should().HaveCountLessThanOrEqualTo(101);
            curve.Points.Should().AllSatisfy(point => point.Observed.Should().BeTrue());
            curve.Points.Select(point => point.Threshold).Should().BeSubsetOf(values);
            curve.Points.Select(point => point.Threshold).Should().Contain([values.Min(), values.Max()]);
        });
    }

    [Fact]
    public async Task Curve_Thinned_Over_A_Rare_Label_Keeps_Its_Values_And_The_Areas()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Ladder(EvidenceKind.Score, 0.6, 0.9);

        // A perfect ranking with the three highest of three hundred scores flagged. Spread by rank over every row,
        // the kept values would skip two of those three, and the areas would bridge the gap with a straight line.
        double[] values = [.. Enumerable.Range(1, 300).Select(index => index / 301.0)];
        (ReplaySet set, IReadOnlyList<DatasetRow> rows) = await LoadAsync(
            file,
            policy,
            [.. values.Select((value, index) => (index >= 297 ? "true" : "false", new[] { Answer(EvidenceKind.Score, value) }))]);

        IReadOnlyList<RungCurve> curves = ThresholdCurve.Compute(set, policy, 0, rows);

        curves.Should().AllSatisfy(curve =>
        {
            curve.Points.Should().HaveCountLessThanOrEqualTo(101);
            curve.Points.Select(point => point.Threshold).Should().Contain(values[^3..]);
            Discrimination discrimination = Discrimination.FromCurve(curve);
            discrimination.RocAuc.Should().BeApproximately(1.0, 1e-9);
            discrimination.PrAuc.Should().BeApproximately(1.0, 1e-9);
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
        [.. values.Select((value, index) => (_labels[index % _labels.Length], new[] { Answer(kind, value) }))];

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
