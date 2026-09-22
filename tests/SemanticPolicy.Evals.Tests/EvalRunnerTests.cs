using System.Collections.ObjectModel;
using System.Text.Json;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Running;
using SemanticPolicy.Evals.Tests.Support;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Evals.Tests;

public sealed class EvalRunnerTests
{
    [Fact]
    public async Task Runner_Sends_Every_Row_Times_Every_Rule_Times_Every_Binding_Once()
    {
        ScriptedProvider local = new ScriptedProvider("local").Returns(_ => Samples.BooleanAnswer(0.8, "local"));
        ScriptedProvider jev = new ScriptedProvider("jev").Returns(_ => Samples.BooleanAnswer(0.4, "jev"));
        Policy policy = Samples.Guard(
            FailureBehavior.Deny,
            ["local", "jev"],
            [Samples.Flagged("first"), Samples.Flagged("second", question: "question-e")]);
        DatasetRow[] rows = [.. Enumerable.Range(1, 10).Select(index => Row($"r{index:00}", "context"))];

        (RunSummary summary, Recording recording, _) =
            await RunAsync(policy, rows, Registered(("local", local), ("jev", jev)));

        local.Calls.Should().Be(20);
        jev.Calls.Should().Be(20);
        recording.Rows.Select(row => row.Id).Should().Equal(rows.Select(row => row.Id));
        recording.Rows.SelectMany(row => row.Attempts.Values).Sum(byProvider => byProvider.Count).Should().Be(40);
        recording.Rows.Should().AllSatisfy(row =>
        {
            row.Attempts.Keys.Should().Equal("first", "second");
            row.Attempts.Values.Should().AllSatisfy(byProvider => byProvider.Keys.Should().Equal("local", "jev"));
        });
        summary.Rows.Should().Be(10);
        summary.Attempts.Should().Be(40);
        summary.FailuresByKind.Should().BeEmpty();
    }

    [Fact]
    public async Task Runner_Records_A_Timeout_As_A_Failure_With_The_Binding_Provider_Name_And_Elapsed_Latency()
    {
        // Registered as "local" while its own id is "scripted": the recording names the binding's provider.
        ScriptedProvider slow = new ScriptedProvider("scripted")
            .Delays(TimeSpan.FromSeconds(30))
            .Returns(_ => Samples.BooleanAnswer(0.8));
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);

        (RunSummary summary, Recording recording, _) = await RunAsync(
            policy,
            [Row("r01", "context")],
            Registered(("local", slow)),
            timeout: TimeSpan.FromMilliseconds(100));

        ProviderResult result = recording.Rows.Single().Attempts[Samples.Injection]["local"];
        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Timeout);
        result.Provider.Id.Should().Be("local");
        result.Provider.Model.Should().BeNull();
        result.Provider.LatencyMs.Should().BeInRange(80, 10_000);
        summary.FailuresByKind.Should().Equal(new Dictionary<string, int> { ["timeout"] = 1 });
    }

    [Fact]
    public async Task Runner_Records_A_Throwing_Provider_As_A_Failure_Whose_Message_Names_The_Exception_Not_The_Row()
    {
        const string text = "row-text-4c1e";
        ScriptedProvider broken = new ScriptedProvider().Throws(new InvalidOperationException("failed on " + text));
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);

        (RunSummary summary, Recording recording, string file) =
            await RunAsync(policy, [Row("r01", text)], Registered(("local", broken)));

        ProviderResult result = recording.Rows.Single().Attempts[Samples.Injection]["local"];
        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Unknown);
        result.Outcome.Message.Should().Contain(nameof(InvalidOperationException)).And.NotContain(text);
        result.Provider.Id.Should().Be("local");
        file.Should().NotContain(text);
        summary.FailuresByKind.Should().Equal(new Dictionary<string, int> { ["unknown"] = 1 });
    }

    [Fact]
    public async Task Runner_Never_Has_More_Than_Parallel_Attempts_In_Flight()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Delays(TimeSpan.FromMilliseconds(20))
            .Returns(_ => Samples.BooleanAnswer(0.8));
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        DatasetRow[] rows = [.. Enumerable.Range(1, 40).Select(index => Row($"r{index:00}", "context"))];

        await RunAsync(policy, rows, Registered(("local", provider)), parallel: 3);

        provider.Calls.Should().Be(40);

        // At least two at once, or the run was sequential and the bound was never tested.
        provider.MaxInFlight.Should().BeInRange(2, 3);
    }

    [Fact]
    public async Task Runner_Writes_Rows_In_Dataset_Order_Even_When_Later_Rows_Finish_First()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Delays(request => ScriptedProvider.TextOf(request) == "slow" ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero)
            .Returns(_ => Samples.BooleanAnswer(0.8));
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        DatasetRow[] rows = [Row("r1", "slow"), Row("r2", "fast"), Row("r3", "fast"), Row("r4", "fast"), Row("r5", "fast")];

        (_, Recording recording, _) = await RunAsync(policy, rows, Registered(("local", provider)), parallel: 4);

        recording.Rows.Select(row => row.Id).Should().Equal("r1", "r2", "r3", "r4", "r5");
    }

    [Fact]
    public async Task Runner_Sends_Ambiguous_And_Abstain_Rows_Like_Any_Other()
    {
        ScriptedProvider provider = new ScriptedProvider().Returns(_ => Samples.BooleanAnswer(0.8));
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        DatasetRow[] rows =
        [
            Row("r1", "context", "true"),
            Row("r2", "context", "ambiguous"),
            Row("r3", "context", "abstain"),
            Row("r4", "context", "false"),
        ];

        (RunSummary summary, Recording recording, _) = await RunAsync(policy, rows, Registered(("local", provider)));

        provider.Calls.Should().Be(4);
        recording.Rows.Select(row => row.Id).Should().Equal("r1", "r2", "r3", "r4");
        summary.Attempts.Should().Be(4);
    }

    private static DatasetRow Row(string id, string text, string label = "true") =>
        new(id, 1, SemanticContext.FromText(text), Samples.Label(label), ReadOnlyDictionary<string, JsonElement>.Empty);

    private static Dictionary<string, IDecisionProvider> Registered(params (string Name, IDecisionProvider Provider)[] providers) =>
        providers.ToDictionary(entry => entry.Name, entry => entry.Provider, StringComparer.Ordinal);

    private static async Task<(RunSummary Summary, Recording Recording, string File)> RunAsync(
        Policy policy,
        IReadOnlyList<DatasetRow> rows,
        IReadOnlyDictionary<string, IDecisionProvider> providers,
        int parallel = 4,
        TimeSpan? timeout = null)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using TempFile file = TempFile.Write("");
        EvalRunner runner = new(providers, parallel, timeout ?? TimeSpan.FromSeconds(30));
        RunSummary summary;
        await using (RecordingWriter writer = await RecordingWriter.CreateAsync(file.Path, Samples.Header(policy), cancellation))
        {
            summary = await runner.RunAsync(policy, rows, writer, progress: null, cancellation);
        }

        return (summary, RecordingReader.Read(file.Path), File.ReadAllText(file.Path));
    }
}
