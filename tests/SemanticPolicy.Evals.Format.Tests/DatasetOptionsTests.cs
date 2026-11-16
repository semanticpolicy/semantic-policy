using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Format.Tests;

public sealed class DatasetOptionsTests
{
    private const string _secret = "secret-description";

    [Fact]
    public async Task Dataset_Row_Options_Round_Trip_Through_The_Writer_And_The_Reader()
    {
        using TempFile file = TempFile.Write("");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Dictionary<string, IReadOnlyList<ChoiceOption>> options = new(StringComparer.Ordinal)
        {
            ["route"] =
            [
                new ChoiceOption("zeta", "description-z", Verdict.Deny),
                new ChoiceOption("alpha", "description-a", Verdict.Allow),
                new ChoiceOption("mid", "description-m", Verdict.Escalate),
            ],
            ["topic"] =
            [
                new ChoiceOption("one", "description-1", Verdict.Warn),
                new ChoiceOption("two", "description-2", Verdict.Allow),
            ],
        };
        DatasetRow[] written =
        [
            Samples.Row("a", "alpha") with { Options = options },
            Samples.Row("b", "allow"),
        ];

        await using (DatasetWriter writer = await DatasetWriter.CreateAsync(file.Path, cancellation))
        {
            foreach (DatasetRow row in written)
            {
                await writer.WriteAsync(row, cancellation);
            }

            await writer.CompleteAsync(cancellation);
        }

        Dataset dataset = DatasetReader.Read(file.Path);
        DatasetRow first = dataset.Rows[0];
        first.Options.Should().NotBeNull();
        first.Options!.Keys.Should().Equal("route", "topic");
        first.Options["route"].Should().Equal(options["route"]);
        first.Options["topic"].Should().Equal(options["topic"]);
        dataset.Rows[1].Options.Should().BeNull();
        File.ReadAllText(file.Path).Should().Contain("\"verdict\":\"escalate\"");
    }

    [Theory]
    [InlineData("""[]""", null, null, "'options' is an object")]
    [InlineData("""{"route": {"key": "a"}}""", "route", null, "array")]
    [InlineData("""{"route": [{"key": "a", "description": "secret-description", "verdict": "allow"}, "b"]}""", "route", 1, "object")]
    [InlineData("""{"route": [{"description": "secret-description", "verdict": "allow"}]}""", "route", 0, "'key'")]
    [InlineData("""{"route": [{"key": "a", "verdict": "allow"}]}""", "route", 0, "'description'")]
    [InlineData("""{"route": [{"key": "a", "description": "secret-description"}]}""", "route", 0, "'verdict'")]
    [InlineData("""{"route": [{"key": "a", "description": "secret-description", "verdict": "maybe"}]}""", "route", 0, "'verdict'")]
    [InlineData("""{"route": [{"key": "a", "description": "secret-description", "verdict": 1}]}""", "route", 0, "'verdict'")]
    [InlineData("""{"route": [{"key": "a", "description": "secret-description", "verdict": "allow", "note": "x"}]}""", "route", 0, "'note'")]
    public void Reader_Rejects_Malformed_Options_Naming_Line_Rule_And_Option_Index(
        string options,
        string? rule,
        int? index,
        string names)
    {
        using TempFile file = TempFile.Write(
            """{"id": "r0", "input": "x", "label": "a"}""" + "\n" +
            $$"""{"id": "r1", "input": "x", "label": "a", "options": {{options}}}""" + "\n");

        Action act = () => DatasetReader.Read(file.Path);

        string message = act.Should().Throw<EvalsFormatException>().Which.Message;
        message.Should().Contain(file.Path).And.Contain("line 2").And.Contain("'r1'").And.Contain(names).And.NotContain(_secret);
        if (rule is not null)
        {
            message.Should().Contain($"rule '{rule}'");
        }

        if (index is { } at)
        {
            message.Should().Contain($"option at index {at}");
        }
    }

    [Fact]
    public void Reader_Lists_Options_Among_The_Properties_A_Row_Has()
    {
        using TempFile file = TempFile.Write("""{"id": "r1", "input": "x", "label": "a", "choices": []}""" + "\n");

        Action act = () => DatasetReader.Read(file.Path);

        act.Should().Throw<EvalsFormatException>().Which.Message.Should()
            .Contain("'choices'").And.Contain("options");
    }

    [Theory]
    [InlineData("one option", "at least two options")]
    [InlineData("a repeated key", "key is repeated")]
    [InlineData("an empty description", "empty description")]
    [InlineData("an abstain verdict", "option verdict")]
    public void ForPolicy_Rejects_Row_Options_Core_Would_Reject_Naming_Line_And_Rule(string fault, string coreSays)
    {
        string options = fault switch
        {
            "one option" => """[{"key": "a", "description": "secret-description", "verdict": "allow"}]""",
            "a repeated key" => """[{"key": "a", "description": "secret-description", "verdict": "allow"}, {"key": "a", "description": "secret-description", "verdict": "deny"}]""",
            "an empty description" => """[{"key": "a", "description": "secret-description", "verdict": "allow"}, {"key": "b", "description": " ", "verdict": "deny"}]""",
            "an abstain verdict" => """[{"key": "a", "description": "secret-description", "verdict": "allow"}, {"key": "b", "description": "secret-description", "verdict": "abstain"}]""",
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        using TempFile file = TempFile.Write(
            """{"id": "r0", "input": "x", "label": "allow"}""" + "\n" +
            $$$"""{"id": "r1", "input": "x", "label": "a", "options": {"route": {{{options}}}}}""" + "\n");
        Dataset dataset = DatasetReader.Read(file.Path);

        Action act = () => dataset.ForPolicy(Policy(), Samples.Route());

        string message = act.Should().Throw<EvalsFormatException>().Which.Message;
        message.Should().Contain(file.Path).And.Contain("line 2").And.Contain("'r1'").And.Contain("rule 'route'")
            .And.Contain(coreSays).And.NotContain(_secret);
    }

    [Theory]
    [InlineData("ghost", "has no rule")]
    [InlineData(Samples.Injection, "not a Choice rule")]
    public void ForPolicy_Rejects_Options_For_A_Rule_The_Policy_Lacks_Or_That_Is_Not_A_Choice_Rule(string ruleId, string says)
    {
        using TempFile file = TempFile.Write(
            $$$"""{"id": "r1", "input": "x", "label": "allow", "options": {"{{{ruleId}}}": [{"key": "a", "description": "secret-description", "verdict": "allow"}, {"key": "b", "description": "secret-description", "verdict": "deny"}]}}""" + "\n");
        Dataset dataset = DatasetReader.Read(file.Path);

        Action act = () => dataset.ForPolicy(Policy(), Samples.Route());

        act.Should().Throw<EvalsFormatException>().Which.Message.Should()
            .Contain(file.Path).And.Contain("line 1").And.Contain("'r1'").And.Contain($"rule '{ruleId}'")
            .And.Contain(says).And.NotContain(_secret);
    }

    [Theory]
    [InlineData("only-mine", true)]
    [InlineData("review", false)]
    public void ForPolicy_Checks_A_Choice_Label_Against_The_Rows_Own_Keys(string label, bool accepted)
    {
        using TempFile file = TempFile.Write(
            """{"id": "r0", "input": "x", "label": "review"}""" + "\n" +
            $$$"""{"id": "r1", "input": "x", "label": "{{{label}}}", "options": {"route": [{"key": "only-mine", "description": "secret-description", "verdict": "allow"}, {"key": "deny", "description": "secret-description", "verdict": "deny"}]}}""" + "\n");
        Dataset dataset = DatasetReader.Read(file.Path);

        Func<IReadOnlyList<DatasetRow>> act = () => dataset.ForPolicy(Policy(), Samples.Route());

        if (accepted)
        {
            act.Should().NotThrow().Which.Select(row => row.Id).Should().Equal("r0", "r1");
        }
        else
        {
            act.Should().Throw<EvalsFormatException>().Which.Message.Should()
                .Contain("line 2").And.Contain("'r1'").And.Contain("rule 'route'").And.Contain($"'{label}'");
        }
    }

    // A Boolean rule and the router, so a row's options can name each.
    private static Policy Policy() => Samples.Guard(["local"], [Samples.Flagged(), Samples.Route()]);
}
