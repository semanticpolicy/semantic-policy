using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Tests;

public sealed class IntervalTests
{
    // The smoke recording's deny rung (tp 32, fp 1, tn 47, fn 13) gives the first five: accuracy, precision,
    // recall, fpr and fnr. The last two are the ends of the scale, where the interval stops at 0 or 1.
    public static TheoryData<int, int, double, double> PublishedFigures => new()
    {
        { 79, 93, 0.763, 0.908 },
        { 32, 33, 0.847, 0.995 },
        { 32, 45, 0.566, 0.823 },
        { 1, 48, 0.004, 0.109 },
        { 13, 45, 0.177, 0.434 },
        { 30, 30, 0.886, 1 },
        { 0, 100, 0, 0.037 },
    };

    [Theory]
    [MemberData(nameof(PublishedFigures))]
    public void Wilson_Interval_Matches_The_Published_Figures(int successes, int trials, double lower, double upper)
    {
        Interval interval = Interval.Wilson(successes, trials)!;

        Math.Round(interval.Lower, 3).Should().Be(lower);
        Math.Round(interval.Upper, 3).Should().Be(upper);
    }

    [Fact]
    public void Wilson_Interval_Ends_Exactly_At_0_And_1_And_Is_Undefined_Over_No_Rows()
    {
        // The formula reaches 0 and 1 only up to rounding; a bound a hair below 0 would print as -0.000.
        Interval.Wilson(0, 100)!.Lower.Should().Be(0);
        Interval.Wilson(30, 30)!.Upper.Should().Be(1);
        Interval.Wilson(0, 0).Should().BeNull();
    }
}
