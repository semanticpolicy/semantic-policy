using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests.Protocol;

public sealed class EvidenceMathTests
{
    public static TheoryData<Evidence, double?> Margins => new()
    {
        { Probability(("true", 0.8), ("false", 0.2)), 0.6 },
        { Score(("a", 3.1), ("b", 1.0), ("c", 0.5)), 2.1 },
        { Probability(("true", 0.9)), 0.8 },
        { Score(("a", 2)), null },
        { Score(), null },
    };

    public static TheoryData<Evidence, Dictionary<string, double>?> Complements => new()
    {
        { Probability(("true", 0.8)), new() { ["true"] = 0.8, ["false"] = 0.2 } },
        { Probability(("false", 0.3)), new() { ["true"] = 0.7, ["false"] = 0.3 } },
        { Probability(("true", 0.8), ("false", 0.2)), null },
        { Score(("true", 0.8)), null },
        { Probability(("a", 0.8)), null },
    };

    [Theory]
    [MemberData(nameof(Margins))]
    public void Margin_Is_Top_Minus_Runner_Up_On_The_Evidence_Scale(Evidence evidence, double? expected)
    {
        double? margin = EvidenceMath.Margin(evidence);

        if (expected is null)
        {
            margin.Should().BeNull();
        }
        else
        {
            margin.Should().BeApproximately(expected.Value, 1e-12);
        }
    }

    [Theory]
    [MemberData(nameof(Complements))]
    public void Boolean_Complement_Is_Derived_Only_For_Single_Key_Probability_Evidence(
        Evidence evidence,
        Dictionary<string, double>? expectedValues)
    {
        Evidence completed = EvidenceMath.WithBooleanComplement(evidence);

        if (expectedValues is null)
        {
            completed.Should().BeSameAs(evidence);
            return;
        }

        completed.Kind.Should().Be(evidence.Kind);
        completed.Scale.Should().Be(evidence.Scale);
        completed.Values.Keys.Should().BeEquivalentTo(expectedValues.Keys);
        foreach ((string key, double value) in expectedValues)
        {
            completed.Values[key].Should().BeApproximately(value, 1e-12);
        }
    }

    private static Evidence Probability(params (string Key, double Value)[] values) =>
        new(EvidenceKind.Probability, values.ToDictionary(pair => pair.Key, pair => pair.Value), Scale: "calibrated");

    private static Evidence Score(params (string Key, double Value)[] values) =>
        new(EvidenceKind.Score, values.ToDictionary(pair => pair.Key, pair => pair.Value), Scale: "sigmoid");
}
