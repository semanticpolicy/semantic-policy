using System.Text.Json;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

// A recording exported from stored verdicts, made the way the tool README's "Exporting production decisions"
// walks through it: the dataset through DatasetWriter, then the recording through ForExport and FromVerdict. Every
// verdict comes from Core's own step function, given only the attempts a live evaluator would have made.
public sealed class ExportReplayTests
{
    [Fact]
    public async Task Replay_Of_An_Export_That_Stopped_At_The_First_Binding_Passes_Under_Its_Own_Policy()
    {
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()]);
        using Export export = await Export.WriteAsync(
            policy,
            [
                new ExportRow("e1", "true", 0.95, policy),
                new ExportRow("e2", "true", 0.7, policy),
                new ExportRow("e3", "false", 0.2, policy),
                new ExportRow("e4", "false", 0.65, policy),
            ]);

        CliRun own = await CliFixture.InvokeAsync(export.Args("report", policy));

        own.ExitCode.Should().Be(ExitCodes.Success, own.Error);
        own.Output.Should().Contain("4 of 4 recorded rows");
        LoadedInputs inputs = Samples.Inputs(policy, DatasetReader.Read(export.DatasetPath));
        IReadOnlyList<EvaluatedRow> replayed = ReplaySet.Load(RecordingReader.Read(export.RecordingPath), inputs, force: false).Evaluate();
        replayed.Select(row => row.Verdict).Should().BeEquivalentTo(
            export.Verdicts.Select(verdict => verdict.Rules.Single()),
            options => options.WithStrictOrdering().PreferringRuntimeMemberTypes());
        replayed.Should().OnlyContain(row => row.Verdict.Attempts.Count == 1 && row.Verdict.DecidingBinding == 0);

        // A gate wider than any margin local gave sends every row on to jev, which production never asked.
        Policy gated = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()], gate: 0.95);
        CliRun variant = await CliFixture.InvokeAsync(export.Args("report", gated));

        variant.ExitCode.Should().Be(ExitCodes.UsageOrData);
        variant.Error.Should().Contain($"'{Samples.Injection}'").And.Contain("'jev'").And.Contain("'e1'");
    }

    [Fact]
    public async Task Read_Verbs_Say_How_Many_Distinct_Policies_The_Selected_Rows_Name()
    {
        // One binding, so a sweep of local asks for no attempt the export lacks.
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        Policy shadow = policy with { Mode = PolicyMode.Shadow };
        Policy gated = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()], gate: 0.1);
        using Export export = await Export.WriteAsync(
            policy,
            [
                new ExportRow("e1", "true", 0.95, policy),
                new ExportRow("e2", "false", 0.1, shadow),
                new ExportRow("e3", "true", 0.8, policy),
                new ExportRow("e4", "false", 0.2, gated, Kept: false),
                new ExportRow("e5", "false", 0.3, shadow),
            ]);
        using CliFixture run = await CliFixture.CreateAsync(
            policy,
            [FixtureRow.Of("true", null, ("local", Samples.BooleanAnswer(0.95))), FixtureRow.Of("false", null, ("local", Samples.BooleanAnswer(0.1)))]);

        CliRun plain = await run.RunAsync("report");
        CliRun all = await CliFixture.InvokeAsync(export.Args("report", policy));
        CliRun kept = await CliFixture.InvokeAsync(export.Args("report", policy, "--where", "metadata.kept=yes"));
        CliRun swept = await CliFixture.InvokeAsync(export.Args("sweep", policy, "--where", "metadata.kept=yes", "--warn", "min-recall=0.5"));

        plain.ExitCode.Should().Be(ExitCodes.Success, plain.Error);
        plain.Output.Should().NotContain("distinct polic");
        run.ReadOut().GetProperty("rows").TryGetProperty("distinctPolicies", out _).Should().BeFalse();
        all.ExitCode.Should().Be(ExitCodes.Success, all.Error);
        all.Output.Should().Contain("the rows name 3 distinct policies");
        kept.ExitCode.Should().Be(ExitCodes.Success, kept.Error);
        kept.Output.Should().Contain("the rows name 2 distinct policies");
        swept.ExitCode.Should().Be(ExitCodes.Success, swept.Error);
        swept.Output.Should().Contain("the rows name 2 distinct policies");
        export.ReadOut().GetProperty("rows").GetProperty("distinctPolicies").GetInt32().Should().Be(2);
    }

    // One exported row: its label, the probability local answered with, the policy whose evaluation produced its
    // verdict, and whether --where metadata.kept=yes keeps it.
    private sealed record ExportRow(string Id, string Label, double PTrue, Policy DecidedBy, bool Kept = true);

    private sealed class Export : IDisposable
    {
        private readonly string _directory;

        private Export(string directory, IReadOnlyList<PolicyVerdict> verdicts)
        {
            _directory = directory;
            Verdicts = verdicts;
        }

        public string DatasetPath => Path.Combine(_directory, "decisions.jsonl");

        public string RecordingPath => Path.Combine(_directory, "decisions.recording.jsonl");

        public IReadOnlyList<PolicyVerdict> Verdicts { get; }

        private string OutPath => Path.Combine(_directory, "result.json");

        public static async Task<Export> WriteAsync(Policy policy, IReadOnlyList<ExportRow> rows)
        {
            CancellationToken cancellation = TestContext.Current.CancellationToken;
            string directory = Path.Combine(Path.GetTempPath(), $"semanticpolicy-evals-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            // What the application's evaluator returned: local's answer was all it asked for.
            PolicyVerdict[] verdicts =
            [
                .. rows.Select(row => PolicyEvaluation.Evaluate(
                    row.DecidedBy,
                    new Dictionary<AttemptKey, ProviderResult> { [new AttemptKey(Samples.Injection, 0)] = Samples.BooleanAnswer(row.PTrue) })
                    .Verdict!),
            ];
            Export export = new(directory, verdicts);

            string sha256;
            await using (DatasetWriter dataset = await DatasetWriter.CreateAsync(export.DatasetPath, cancellation))
            {
                foreach (ExportRow row in rows)
                {
                    await dataset.WriteAsync(
                        new DatasetRow(row.Id, SemanticContext.FromText("context"), Samples.Label(row.Label))
                        {
                            Metadata = new Dictionary<string, JsonElement>
                            {
                                ["kept"] = JsonSerializer.SerializeToElement(row.Kept ? "yes" : "no"),
                            },
                        },
                        cancellation);
                }

                sha256 = await dataset.CompleteAsync(cancellation);
            }

            RecordingHeader header = RecordingHeader.ForExport(
                policy,
                [new RecordedDataset(export.DatasetPath, sha256, Split: null)],
                [.. policy.Bindings.Select(binding => new RecordedProvider(binding.ProviderId, "model-" + binding.ProviderId))],
                "exporter",
                "2.1.0",
                new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero));
            await using (RecordingWriter recording = await RecordingWriter.CreateAsync(export.RecordingPath, header, cancellation))
            {
                for (int index = 0; index < rows.Count; index++)
                {
                    await recording.WriteAsync(RecordedRow.FromVerdict(rows[index].Id, verdicts[index]), cancellation);
                }
            }

            return export;
        }

        // The verb on the export, with the policy given written to a file of its own.
        public string[] Args(string verb, Policy policy, params string[] options)
        {
            string policyPath = Path.Combine(_directory, $"policy-{Guid.NewGuid():N}.json");
            File.WriteAllText(policyPath, JsonSerializer.Serialize(policy, SemanticPolicyJson.Options));
            return
            [
                verb,
                "--policy", policyPath,
                "--dataset", DatasetPath,
                "--recording", RecordingPath,
                "--out", OutPath,
                .. options,
            ];
        }

        public JsonElement ReadOut()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(OutPath));
            return document.RootElement.Clone();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A file the OS still holds is left for the temp directory's own cleanup.
            }
        }
    }
}
