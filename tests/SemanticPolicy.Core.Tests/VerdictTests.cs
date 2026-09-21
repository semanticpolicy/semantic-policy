namespace SemanticPolicy.Core.Tests;

public sealed class VerdictTests
{
    // Aggregation takes the numeric maximum, so a reordered enum would change every policy verdict
    // without a compile error. This pins the declared order.
    [Fact]
    public void Verdict_Severity_Orders_Deny_Over_Escalate_Over_Abstain_Over_Warn_Over_Allow()
    {
        Verdict[] ascending = [Verdict.Allow, Verdict.Warn, Verdict.Abstain, Verdict.Escalate, Verdict.Deny];

        ascending.Should().BeInAscendingOrder();
        Enum.GetValues<Verdict>().Should().Equal(ascending);
        ((int)Verdict.Allow).Should().Be(0);
        ((int)Verdict.Deny).Should().Be(4);
    }
}
