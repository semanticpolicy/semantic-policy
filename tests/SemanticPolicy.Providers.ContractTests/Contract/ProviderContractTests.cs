using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.ContractTests.Contract;

/// <summary>
/// What every provider must hold, whatever its wire: each failure kind comes back as a failure result
/// and never as an exception, each declared type is answered in that type's shape with declared
/// evidence only, the raw response is there exactly when the capabilities say so, and the caller's
/// cancellation is the caller's. An adapter subclasses this with its harness and inherits every test.
/// </summary>
public abstract class ProviderContractTests
{
    public static TheoryData<FailureKind> FailureKinds => new(Enum.GetValues<FailureKind>());

    public static TheoryData<DecisionType> DecisionTypes => new(Enum.GetValues<DecisionType>());

    protected abstract ProviderHarness CreateHarness();

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task Provider_Reports_Every_Failure_Kind_As_A_Failure_Result_Without_Throwing(FailureKind kind)
    {
        ProviderHarness harness = CreateHarness();
        DecisionRequest request = harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker);
        harness.ScriptFailure(kind);

        ProviderResult result = await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(kind);
        result.Value.Should().BeNull();
        result.Evidence.Should().BeEmpty();
        result.Type.Should().Be(request.Type);
        result.Provider.Id.Should().Be(harness.Provider.Id);
        result.Provider.LatencyMs.Should().BePositive();
        harness.RequestCount.Should().Be(1, "a failed call is reported, never retried");
    }

    [Theory]
    [MemberData(nameof(DecisionTypes))]
    public async Task Provider_Answers_Every_Declared_Type_With_A_Value_Of_That_Type(DecisionType type)
    {
        ProviderHarness harness = CreateHarness();
        ProviderCapabilities capabilities = harness.Provider.Capabilities;
        Assert.SkipUnless(capabilities.Types.Contains(type), $"the provider does not declare {type}.");
        DecisionRequest request = harness.CreateRequest(type, ProviderHarness.Marker);
        harness.ScriptSuccess(type);

        ProviderResult result = await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Protocol.Should().Be(ProtocolVersion.V0);
        result.Type.Should().Be(type);
        switch (type)
        {
            case DecisionType.Boolean:
                result.Value.Should().BeOfType<BooleanValue>();
                break;
            case DecisionType.Choice:
                result.Value.Should().BeOfType<ChoiceValue>().Which.Option.Should().BeOneOf(request.Options!.Keys);
                break;
            case DecisionType.Score:
                ScoreValue score = result.Value.Should().BeOfType<ScoreValue>().Which;
                score.Index.Should().BeInRange(0, request.Levels!.Count - 1);
                score.Level.Should().Be(request.Levels[score.Index]);
                break;
        }

        result.Evidence.Should().NotBeEmpty();
        foreach (Evidence evidence in result.Evidence)
        {
            capabilities.Evidence.Should().Contain(evidence.Kind);
            if (evidence.Kind == EvidenceKind.Probability)
            {
                IReadOnlyDictionary<string, double> values = EvidenceMath.WithBooleanComplement(evidence).Values;
                values.Values.Should().AllSatisfy(value => value.Should().BeInRange(0, 1));
                values.Values.Sum().Should().BeApproximately(1, 0.02);
            }
            else if (type == DecisionType.Boolean)
            {
                evidence.Values.Keys.Should().Contain(
                    ["true", "false"],
                    "only a probability has a complement the runtime may derive, so {0} evidence carries both ends",
                    evidence.Kind);
            }
        }

        (result.Raw is not null).Should().Be(capabilities.RawOutput);
        result.Provider.Model.Should().NotBeNull();
        result.Provider.LatencyMs.Should().BePositive();
    }

    [Fact]
    public async Task Caller_Cancellation_During_A_Hang_Throws_OperationCanceledException()
    {
        ProviderHarness harness = CreateHarness();
        DecisionRequest request = harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker);
        harness.ScriptHang();
        using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => harness.Provider.DecideAsync(request, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task Failure_Message_And_ToString_Never_Contain_The_Marker(FailureKind kind)
    {
        ProviderHarness harness = CreateHarness();
        DecisionRequest request = harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker);
        harness.ScriptFailure(kind);

        ProviderResult result = await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Message.Should().NotBeNullOrWhiteSpace().And.NotContain(ProviderHarness.Marker);
        result.ToString().Should().NotContain(ProviderHarness.Marker);
    }
}
