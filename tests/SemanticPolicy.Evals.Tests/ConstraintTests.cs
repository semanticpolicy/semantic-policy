using SemanticPolicy.Evals.Sweeping;

namespace SemanticPolicy.Evals.Tests;

public sealed class ConstraintTests
{
    [Theory]
    [InlineData("min-recall=0.9", "MinRecall", 0.9)]
    [InlineData("max-fpr=0.1", "MaxFpr", 0.1)]
    [InlineData("min-precision=0.8", "MinPrecision", 0.8)]
    [InlineData("max-abstain=0.2", "MaxAbstain", 0.2)]
    [InlineData("min-accuracy=0.75", "MinAccuracy", 0.75)]
    [InlineData("recall=0.9", null, null)]
    [InlineData("min-recall=1.5", null, null)]
    [InlineData("min-recall", null, null)]
    public void Constraint_Parses_Name_Equals_Value_And_Rejects_Unknown_Names_Or_Values_Outside_0_1(
        string token,
        string? kind,
        double? value)
    {
        Func<RungConstraint> asRung = () => RungConstraint.Parse(Verdict.Deny, token);
        Func<GateConstraint> asGate = () => GateConstraint.Parse(token);

        if (Enum.TryParse(kind, out ConstraintKind rungKind))
        {
            asRung().Should().Be(new RungConstraint(Verdict.Deny, rungKind, value!.Value));
            asGate.Should().Throw<EvalsException>().WithMessage("*--gate*");
        }
        else if (Enum.TryParse(kind, out GateConstraintKind gateKind))
        {
            asGate().Should().Be(new GateConstraint(gateKind, value!.Value));
            asRung.Should().Throw<EvalsException>().WithMessage("*--deny*");
        }
        else
        {
            // The message names the option the token came from, so a user with three rung options and a gate
            // option on one command line knows which one to fix.
            asRung.Should().Throw<EvalsException>().WithMessage("*--deny*");
            asGate.Should().Throw<EvalsException>().WithMessage("*--gate*");
        }
    }
}
