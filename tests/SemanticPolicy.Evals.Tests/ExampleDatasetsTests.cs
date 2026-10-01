using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class ExampleDatasetsTests
{
    // A structural guard: the README's examples would otherwise stop parsing without any runtime symptom.
    [Theory]
    [InlineData("prompt-injection", SplitSource.Metadata, 5, 5, 1)]
    [InlineData("agent-router", SplitSource.None, 10, 10, 0)]
    [InlineData("harm-severity", SplitSource.None, 10, 10, 0)]
    public void Shipped_Example_Datasets_Parse_Against_Their_Policies(
        string name,
        SplitSource source,
        int tuneRows,
        int testRows,
        int ambiguousRows)
    {
        string examples = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "examples");
        InputSelection selection = new(
            Path.Combine(examples, $"{name}.policy.json"),
            RuleId: null,
            Path.Combine(examples, $"{name}.jsonl"),
            TunePath: null,
            TestPath: null,
            new SplitNames(),
            []);

        LoadedInputs loaded = Inputs.Load(selection);

        loaded.Selected.Should().HaveCount(10);
        loaded.Splits.Source.Should().Be(source);
        loaded.Splits.Tune.Should().HaveCount(tuneRows);
        loaded.Splits.Test.Should().HaveCount(testRows);
        loaded.Selected.Count(row => row.Label.Kind == RowLabelKind.Ambiguous).Should().Be(ambiguousRows);
        loaded.Policy.Bindings.Select(binding => binding.ProviderId).Should().Equal("local", "jev");
    }

    // A structural guard: the README's examples run on this file, and a set that stopped parsing, or lost its
    // test rows so that every sweep example chose and reported on the same data, would show no other symptom.
    [Fact]
    public void Shipped_Smoke_Set_Parses_Has_About_A_Hundred_Rows_Unique_Ids_Both_Splits_And_Both_Labels()
    {
        string smoke = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");
        InputSelection selection = new(
            Path.Combine(smoke, "prompt-injection.policy.json"),
            RuleId: null,
            Path.Combine(smoke, "prompt-injection.smoke.jsonl"),
            TunePath: null,
            TestPath: null,
            new SplitNames(),
            []);

        LoadedInputs loaded = Inputs.Load(selection);

        loaded.Selected.Count.Should().BeInRange(90, 110);
        loaded.Selected.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        loaded.Splits.Source.Should().Be(SplitSource.Metadata);
        foreach (IReadOnlyList<DatasetRow> split in new[] { loaded.Splits.Tune, loaded.Splits.Test })
        {
            split.Select(row => row.Label.Answer).Should().Contain(["true", "false"]);
        }

        loaded.Selected.Should().OnlyContain(row =>
            row.Metadata.ContainsKey("set") && row.Metadata["set"].GetString() == "smoke, not a benchmark");
        loaded.Policy.Bindings.Select(binding => binding.ProviderId).Should().Equal("local", "jev");
    }

    // A structural guard: the router set is meant to be recorded like the smoke set, and a label naming no team,
    // a split left empty or a binding dropped would show up only when a run cannot be made or sweeps over nothing.
    [Fact]
    public void Shipped_Router_Set_Parses_With_Four_Teams_Both_Splits_And_Ambiguous_Rows()
    {
        string smoke = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");
        InputSelection selection = new(
            Path.Combine(smoke, "support-router.policy.json"),
            RuleId: null,
            Path.Combine(smoke, "support-router.smoke.jsonl"),
            TunePath: null,
            TestPath: null,
            new SplitNames(),
            []);

        LoadedInputs loaded = Inputs.Load(selection);

        string[] teams = ["billing", "technical", "account", "sales"];
        loaded.Policy.Rules.Should().ContainSingle().Which.Should().BeOfType<ChoiceRule>()
            .Which.Options.Select(option => option.Key).Should().Equal(teams);
        loaded.Selected.Count.Should().BeInRange(70, 90);
        loaded.Selected.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        loaded.Splits.Source.Should().Be(SplitSource.Metadata);
        loaded.Splits.Tune.Should().NotBeEmpty();
        loaded.Splits.Test.Should().NotBeEmpty();
        loaded.Selected.Where(row => row.Label.Kind != RowLabelKind.Ambiguous).Select(row => row.Label.Answer)
            .Distinct().Should().BeEquivalentTo(teams);
        loaded.Selected.Should().Contain(row => row.Label.Kind == RowLabelKind.Ambiguous);
        loaded.Policy.Bindings.Select(binding => binding.ProviderId).Should().Equal("local", "jev");
    }

    // A structural guard: the set measures the category rule of examples/SupportTicketForm on the parts that
    // example's context delegate builds. Parts that drifted from the delegate would measure a different input,
    // and a lost split would let compare choose and report on the same rows, with no other symptom.
    [Fact]
    public void Shipped_Support_Ticket_Set_Parses_With_Two_Parts_Both_Splits_And_Both_Labels()
    {
        string examples = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "examples");
        InputSelection selection = new(
            Path.Combine(examples, "support-ticket.policy.json"),
            RuleId: null,
            Path.Combine(examples, "support-ticket.jsonl"),
            TunePath: null,
            TestPath: null,
            new SplitNames(),
            []);

        LoadedInputs loaded = Inputs.Load(selection);

        loaded.Policy.Rules.Should().ContainSingle().Which.Should().BeOfType<BooleanRule>()
            .Which.Id.Should().Be("ticket-category");
        loaded.Selected.Count.Should().BeInRange(40, 60);
        loaded.Selected.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        loaded.Splits.Source.Should().Be(SplitSource.Metadata);
        foreach (IReadOnlyList<DatasetRow> split in new[] { loaded.Splits.Tune, loaded.Splits.Test })
        {
            split.Select(row => row.Label.Answer).Should().Contain(["true", "false"]);
        }

        loaded.Selected.Should().Contain(row => row.Label.Kind == RowLabelKind.Ambiguous);
        loaded.Selected.Should().OnlyContain(row =>
            row.Input.Parts.Select(part => part.Name).SequenceEqual(new[] { "category", "description" }));
        loaded.Policy.Bindings.Select(binding => binding.ProviderId).Should().Equal("local", "jev");
    }

    // A structural guard: the README quotes this recording, and a reader replays it without a key. A dataset whose
    // bytes changed, line endings included, a rule that changed or a run cut short would stop it fitting with no
    // other symptom. No --force, which would skip the very digest check this is here for.
    [Fact]
    public async Task Committed_Smoke_Recording_Replays_Against_The_Committed_Dataset_And_Policy()
    {
        await ReplayCommittedRecordingAsync("prompt-injection", 100);
    }

    // The same guard for the router set, whose recording the README's compare section quotes.
    [Fact]
    public async Task Committed_Router_Recording_Replays_Against_The_Committed_Dataset_And_Policy()
    {
        await ReplayCommittedRecordingAsync("support-router", 80);
    }

    // The calibrated example is calibrate's output, never an edit, so a fresh run on the committed files writes it
    // again. Parsed rather than byte for byte: the slope, the intercept and the converted thresholds go through the C
    // runtime's exp and log, whose last digits may differ between the machine that wrote the file and the one
    // running this, and a round-trip double prints that difference.
    [Fact]
    public async Task Committed_Calibrated_Smoke_Policy_Is_What_Calibrate_Writes_From_The_Committed_Recording()
    {
        string smoke = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");
        string committed = Path.Combine(smoke, "prompt-injection.calibrated.policy.json");
        using TempFile written = TempFile.Write("", ".json");

        CliRun run = await CliFixture.InvokeAsync(
        [
            "calibrate",
            "--policy", Path.Combine(smoke, "prompt-injection.policy.json"),
            "--dataset", Path.Combine(smoke, "prompt-injection.smoke.jsonl"),
            "--recording", Path.Combine(smoke, "prompt-injection.recording.jsonl"),
            "--provider", "local",
            "--out-policy", written.Path,
        ]);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        File.Exists(committed).Should().BeTrue("calibrate's output on the committed smoke files is committed beside them");
        SameUpToComputedDigits(
            JsonNode.Parse(await File.ReadAllTextAsync(committed, TestContext.Current.CancellationToken)),
            JsonNode.Parse(await File.ReadAllTextAsync(written.Path, TestContext.Current.CancellationToken)),
            "$",
            member: null);
    }

    // The calibrated example replays like the policy it was written from and decides every row as that policy does:
    // the map moved the numbers local's thresholds compare, not which rows they flag.
    [Fact]
    public async Task Committed_Calibrated_Smoke_Policy_Replays_And_Gives_Every_Row_The_Smoke_Policys_Verdict()
    {
        (LoadedInputs calibrated, ReplaySet replay) =
            await ReplayCommittedRecordingAsync("prompt-injection", 100, "prompt-injection.calibrated.policy.json");
        (_, ReplaySet original) = await ReplayCommittedRecordingAsync("prompt-injection", 100);

        ProviderBinding local = calibrated.Policy.Bindings.Single(binding => binding.ProviderId == "local");
        local.OperatingPoints.Should().ContainSingle().Which.Calibration.Should().NotBeNull();
        calibrated.Policy.Bindings.Where(binding => binding.ProviderId != "local")
            .SelectMany(binding => binding.OperatingPoints)
            .Should().OnlyContain(point => point.Calibration == null);
        EvaluatedRow[] alone = [.. replay.Evaluate(calibrated.Policy with { Bindings = [local] })];
        alone.Should().OnlyContain(row => row.Verdict.Attempts.Single().CalibratedEvidence != null);
        Calibration.Compute([.. alone.Select(row => RowCounting.Classify(row, calibrated.Rule))], calibrated.Rule)
            .Applicable.Should().BeTrue("local's thresholds read the calibrated probability, which is what is measured");
        replay.Evaluate().Select(row => (row.Row.Id, row.Verdict.Verdict))
            .Should().Equal(original.Evaluate().Select(row => (row.Row.Id, row.Verdict.Verdict)));
    }

    // Replays with report, which calls no provider, so it passes with no key and no server. The header must name
    // every bound provider with the model the README quotes, and every row must have an answer from each binding
    // that the library still accepts on replay: a row the local server or Jev failed on, or a success read as
    // malformed, would quietly leave that provider's side of the README's comparison short. Each binding is
    // replayed alone, because under the whole chain a malformed answer moves on to the next binding and no count
    // shows it. The header must list no resumption: a committed recording is made in one run and never rewritten,
    // and a resumed one would replay like any other, so nothing else would notice it.
    private static async Task<(LoadedInputs Inputs, ReplaySet Replay)> ReplayCommittedRecordingAsync(
        string set,
        int rowCount,
        string? policyFile = null)
    {
        string smoke = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");
        string policy = Path.Combine(smoke, policyFile ?? $"{set}.policy.json");
        string dataset = Path.Combine(smoke, $"{set}.smoke.jsonl");
        string recordingPath = Path.Combine(smoke, $"{set}.recording.jsonl");

        CliRun report = await CliFixture.InvokeAsync(
            ["report", "--policy", policy, "--dataset", dataset, "--recording", recordingPath]);

        report.ExitCode.Should().Be(ExitCodes.Success, report.Error);
        LoadedInputs loaded = Inputs.Load(
            new InputSelection(policy, RuleId: null, dataset, TunePath: null, TestPath: null, new SplitNames(), []));
        Recording recording = RecordingReader.Read(recordingPath);
        recording.Rows.Select(row => row.Id).Should().HaveCount(rowCount)
            .And.Equal(loaded.Selected.Select(row => row.Id));
        recording.Header.Providers.Select(provider => provider.Name)
            .Should().Equal(loaded.Policy.Bindings.Select(binding => binding.ProviderId));
        recording.Header.Providers.Should().OnlyContain(provider => !string.IsNullOrEmpty(provider.Model));
        recording.Header.Resumptions.Should().BeNullOrEmpty();

        ReplaySet replay = ReplaySet.Load(recording, loaded, force: false);
        foreach (ProviderBinding binding in loaded.Policy.Bindings)
        {
            replay.Evaluate(loaded.Policy with { Bindings = [binding] })
                .Select(row => row.Verdict.Attempts.Single().EffectiveOutcome.Status)
                .Should().NotContain(OutcomeStatus.Failure, "no row may have failed on {0}", binding.ProviderId);
        }

        return (loaded, replay);
    }

    // Every member equal in name, order and value, except the three whose values go through exp and log: those agree
    // within a relative 1e-9, far above a last-digit difference and far below any change in the fit.
    private static void SameUpToComputedDigits(JsonNode? expected, JsonNode? actual, string path, string? member)
    {
        switch (expected)
        {
            case JsonObject members:
                JsonObject actualMembers = actual.Should().BeOfType<JsonObject>(path).Subject;
                actualMembers.Select(entry => entry.Key).Should().Equal(members.Select(entry => entry.Key), path);
                foreach ((string name, JsonNode? value) in members)
                {
                    SameUpToComputedDigits(value, actualMembers[name], $"{path}.{name}", name);
                }

                break;
            case JsonArray items:
                JsonArray actualItems = actual.Should().BeOfType<JsonArray>(path).Subject;
                actualItems.Should().HaveCount(items.Count, path);
                for (int index = 0; index < items.Count; index++)
                {
                    SameUpToComputedDigits(items[index], actualItems[index], $"{path}[{index}]", member: null);
                }

                break;
            case JsonValue value when member is "slope" or "intercept" or "atOrAbove":
                double wanted = value.GetValue<double>();
                actual!.GetValue<double>().Should().BeApproximately(wanted, 1e-9 * Math.Abs(wanted), path);
                break;
            default:
                (actual?.ToJsonString()).Should().Be(expected?.ToJsonString(), path);
                break;
        }
    }

    // A structural guard: a real address, key or endpoint committed in a dataset is a content-policy breach with
    // no runtime symptom, so every file under datasets/ is scanned for the shapes one would take.
    [Fact]
    public void Shipped_Datasets_Contain_No_Url_Email_Or_Key_Shaped_Token()
    {
        string datasets = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets");
        Regex[] forbidden =
        [
            new(@"\b[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase),
            new(@"\bwww\.", RegexOptions.IgnoreCase),
            new(@"\b[a-z0-9-]+\.(?:com|net|org|io|dev|ai|app|co|uk|pl|de|eu)\b", RegexOptions.IgnoreCase),
            new(@"[a-z0-9._%+-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)+", RegexOptions.IgnoreCase),
            new(@"\b(?:sk|pk|rk)[-_][A-Za-z0-9_-]{16,}"),
            new(@"\bAKIA[0-9A-Z]{16}\b"),
            new(@"\bgh[pousr]_[A-Za-z0-9]{20,}"),
            new(@"\bxox[abpr]-"),
            new(@"\beyJ[A-Za-z0-9_-]{10,}\."),
            new("-----BEGIN"),
        ];

        // Any long run of token characters, for a key of no known shape. Not applied to a recording: it carries no
        // input by design, and its header holds each dataset's SHA-256 and a tool version ending in a commit hash.
        // A calibrated policy names the dataset it was fitted on by the same SHA-256, so that one value is left out.
        Regex longToken = new(@"(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{40,}");

        // By extension: on a case-insensitive file system this folder is also the one that holds the dataset
        // reader's source files.
        string[] files =
        [
            .. Directory.GetFiles(datasets, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file) is ".jsonl" or ".json"),
        ];

        files.Should().NotBeEmpty();
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            List<(Regex Pattern, string Text)> scans = [.. forbidden.Select(pattern => (pattern, text))];
            if (!file.EndsWith(".recording.jsonl", StringComparison.Ordinal))
            {
                scans.Add((longToken, Path.GetExtension(file) == ".json" ? WithoutDatasetDigest(text) : text));
            }

            foreach ((Regex pattern, string scanned) in scans)
            {
                Match match = pattern.Match(scanned);
                match.Success.Should().BeFalse(
                    $"'{Path.GetRelativePath(datasets, file)}' holds '{match.Value}', which matches {pattern}");
            }
        }
    }

    // The exemption is the one value and nothing that merely looks like it: the same digest under another member,
    // or a string member that happens to be called provenance, is still a long token.
    [Fact]
    public void Key_Scan_Leaves_Out_Only_The_Dataset_Digest_Of_A_Calibrations_Provenance()
    {
        string digest = new('a', 64);
        string json = $$$"""
            {"calibration":{"provenance":{"model":"m","datasetDigest":"{{{digest}}}"}},
             "datasetDigest":"{{{digest}}}","other":{"provenance":"{{{digest}}}"},"list":[{"datasetDigest":"{{{digest}}}"}]}
            """;

        string scanned = WithoutDatasetDigest(json);

        scanned.Should().HaveLength(json.Length);
        Regex.Matches(scanned, digest).Should().HaveCount(3);
        scanned.Should().Contain("\"datasetDigest\":\"" + new string(' ', 64) + "\"}}");
    }

    // The file with the value of provenance.datasetDigest blanked to spaces, byte for byte in place, so a match
    // elsewhere still points at its own text. Read as JSON rather than matched as text, so only that member counts.
    private static string WithoutDatasetDigest(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        List<(int Start, int Length)> digests = [];
        Stack<string?> parents = new();
        string? member = null;
        Utf8JsonReader reader = new(bytes);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    parents.Push(member);
                    member = null;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    parents.Pop();
                    member = null;
                    break;
                case JsonTokenType.PropertyName:
                    member = reader.GetString();
                    break;
                case JsonTokenType.String when member == "datasetDigest" && parents.Peek() == "provenance":
                    digests.Add(((int)reader.TokenStartIndex + 1, reader.ValueSpan.Length));
                    member = null;
                    break;
                default:
                    member = null;
                    break;
            }
        }

        foreach ((int start, int length) in digests)
        {
            bytes.AsSpan(start, length).Fill((byte)' ');
        }

        return Encoding.UTF8.GetString(bytes);
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticPolicy.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("No SemanticPolicy.slnx above the test output directory.");
    }
}
