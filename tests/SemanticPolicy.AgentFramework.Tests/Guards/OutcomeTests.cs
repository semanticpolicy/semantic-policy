namespace SemanticPolicy.AgentFramework.Tests.Guards;

public sealed class OutcomeTests
{
    [Theory]
    [InlineData("PreModelOutcome.Stop", null)]
    [InlineData("PreModelOutcome.Stop", "")]
    [InlineData("PreModelOutcome.Stop", " ")]
    [InlineData("PreToolOutcome.Refuse", null)]
    [InlineData("PreToolOutcome.Refuse", "")]
    [InlineData("PreToolOutcome.Refuse", " ")]
    [InlineData("PreToolOutcome.Stop", null)]
    [InlineData("PreToolOutcome.Stop", "")]
    [InlineData("PreToolOutcome.Stop", " ")]
    [InlineData("PostToolOutcome.Stop", null)]
    [InlineData("PostToolOutcome.Stop", "")]
    [InlineData("PostToolOutcome.Stop", " ")]
    public void Outcome_Factories_Reject_A_Blank_Message(string factory, string? message)
    {
        Action create = factory switch
        {
            "PreModelOutcome.Stop" => () => PreModelOutcome.Stop(message!),
            "PreToolOutcome.Refuse" => () => PreToolOutcome.Refuse(message!),
            "PreToolOutcome.Stop" => () => PreToolOutcome.Stop(message!),
            "PostToolOutcome.Stop" => () => PostToolOutcome.Stop(message!),
            _ => throw new ArgumentOutOfRangeException(nameof(factory)),
        };

        create.Should().Throw<ArgumentException>().WithParameterName("message");
    }

    [Fact]
    public void Outcome_Factories_Expose_The_Kind_And_The_Payload()
    {
        PreModelOutcome.Proceed.Kind.Should().Be(PreModelOutcomeKind.Proceed);
        PreModelOutcome.Stop("stop-1").Should().BeEquivalentTo(new { Kind = PreModelOutcomeKind.Stop, Message = "stop-1" });

        PreToolOutcome.Proceed.Kind.Should().Be(PreToolOutcomeKind.Proceed);
        PreToolOutcome.Refuse("refuse-1").Should().BeEquivalentTo(new { Kind = PreToolOutcomeKind.Refuse, Message = "refuse-1" });
        PreToolOutcome.Stop("stop-2").Should().BeEquivalentTo(new { Kind = PreToolOutcomeKind.Stop, Message = "stop-2" });

        PostToolOutcome.Proceed.Kind.Should().Be(PostToolOutcomeKind.Proceed);
        PostToolOutcome.Replace(42).Should().BeEquivalentTo(new { Kind = PostToolOutcomeKind.Replace, Result = 42 });
        PostToolOutcome.Replace(null).Should().BeEquivalentTo(new { Kind = PostToolOutcomeKind.Replace, Result = (object?)null });
        PostToolOutcome.Stop("stop-3").Should().BeEquivalentTo(new { Kind = PostToolOutcomeKind.Stop, Message = "stop-3" });
    }
}
