using System.Security.Cryptography;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class DatasetReaderTests
{
    [Fact]
    public void Reader_Parses_A_String_Input_Into_A_Single_Text_Part()
    {
        using TempFile file = TempFile.Write("""{"id": "r1", "input": "sample text", "label": true}""" + "\n");

        Dataset dataset = DatasetReader.Read(file.Path);

        DatasetRow row = dataset.Rows.Should().ContainSingle().Which;
        row.Id.Should().Be("r1");
        row.Line.Should().Be(1);
        row.Input.Parts.Should().ContainSingle().Which.Name.Should().Be("text");
        row.Input.ToCanonicalText().Should().Be("sample text");
        dataset.Path.Should().Be(file.Path);
    }

    [Fact]
    public void Reader_Parses_An_Object_Input_Into_One_Part_Per_Property_In_Declared_Order()
    {
        using TempFile file = TempFile.Write(
            """{"id": "r1", "input": {"message": "first block", "context": {"turn": 2}}, "label": "answer"}""" + "\n");

        Dataset dataset = DatasetReader.Read(file.Path);

        DatasetRow row = dataset.Rows.Should().ContainSingle().Which;
        row.Input.Parts.Select(part => part.Name).Should().Equal("message", "context");
        row.Input.ToCanonicalText().Should().Be("message:\nfirst block\n\ncontext:\n{\"turn\":2}");
    }

    [Theory]
    [InlineData("""{"input": "secret-content", "label": true}""", "id")]
    [InlineData("""{"id": "r1", "label": true}""", "input")]
    [InlineData("""{"id": "r1", "input": "secret-content"}""", "label")]
    [InlineData("""{"id": "r1", "input": 5, "label": true}""", "input")]
    [InlineData("""{"id": "r1", "input": "secret-content", "label": tru""", "JSON")]
    public void Reader_Rejects_A_Row_Missing_A_Required_Field_Naming_The_Line_Not_The_Content(string row, string field)
    {
        using TempFile file = TempFile.Write(
            """{"id": "r0", "input": "secret-content", "label": false}""" + "\n" + row + "\n");

        Action act = () => DatasetReader.Read(file.Path);

        EvalsException failure = act.Should().Throw<EvalsException>().Which;
        failure.ExitCode.Should().Be(ExitCodes.UsageOrData);
        failure.Message.Should().Contain("line 2").And.Contain(field).And.NotContain("secret-content");
    }

    [Fact]
    public void Reader_Rejects_A_Duplicate_Id_Naming_Both_Lines()
    {
        using TempFile file = TempFile.Write(
            """{"id": "r1", "input": "a", "label": true}""" + "\n" +
            """{"id": "r2", "input": "b", "label": true}""" + "\n" +
            """{"id": "r1", "input": "c", "label": false}""" + "\n");

        Action act = () => DatasetReader.Read(file.Path);

        act.Should().Throw<EvalsException>().Which.Message.Should()
            .Contain("line 3").And.Contain("line 1").And.Contain("r1");
    }

    [Fact]
    public void Reader_Accepts_Ambiguous_And_Abstain_Labels_And_Passes_Unknown_Metadata_Through()
    {
        using TempFile file = TempFile.Write(
            """{"id": "a", "input": "x", "label": "ambiguous", "metadata": {"final_label": "true", "weight": 2}}""" + "\n" +
            """{"id": "b", "input": "x", "label": "abstain"}""" + "\n");

        Dataset dataset = DatasetReader.Read(file.Path);

        dataset.Rows.Should().HaveCount(2);
        dataset.Rows[0].Label.Should().Be(new RowLabel(RowLabelKind.Ambiguous, null));
        dataset.Rows[1].Label.Should().Be(new RowLabel(RowLabelKind.Abstain, null));
        dataset.Rows[0].Metadata["final_label"].GetString().Should().Be("true");
        dataset.Rows[0].Metadata["weight"].GetInt32().Should().Be(2);
        dataset.Rows[1].Metadata.Should().BeEmpty();
    }

    [Fact]
    public void Reader_Reports_The_Sha256_Of_The_File_Bytes()
    {
        using TempFile file = TempFile.Write("""{"id": "r1", "input": "x", "label": true}""" + "\n");
        string expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.Path)));

        Dataset dataset = DatasetReader.Read(file.Path);

        dataset.Sha256.Should().Be(expected).And.HaveLength(64);
    }

    [Theory]
    [InlineData("boolean", "maybe")]
    [InlineData("choice", "unknown-key")]
    [InlineData("score", "unknown-level")]
    public void Binding_Rows_To_A_Rule_Rejects_A_Label_Outside_The_Rule_Vocabulary(string kind, string label)
    {
        Rule rule = RuleOfKind(kind);
        using TempFile file = TempFile.Write($$"""{"id": "r1", "input": "x", "label": "{{label}}"}""" + "\n");
        Dataset dataset = DatasetReader.Read(file.Path);

        Action act = () => dataset.ForRule(rule);

        act.Should().Throw<EvalsException>().Which.Message.Should()
            .Contain("line 1").And.Contain(rule.Id).And.Contain(label);
    }

    [Fact]
    public void Binding_Rows_To_A_Boolean_Rule_Accepts_Json_Booleans_And_Their_String_Forms()
    {
        using TempFile file = TempFile.Write(
            """{"id": "a", "input": "x", "label": true}""" + "\n" +
            """{"id": "b", "input": "x", "label": "true"}""" + "\n" +
            """{"id": "c", "input": "x", "label": false}""" + "\n" +
            """{"id": "d", "input": "x", "label": "false"}""" + "\n");

        IReadOnlyList<DatasetRow> rows = DatasetReader.Read(file.Path).ForRule(RuleOfKind("boolean"));

        rows.Select(row => row.Label).Should().Equal(
            new RowLabel(RowLabelKind.Answer, "true"),
            new RowLabel(RowLabelKind.Answer, "true"),
            new RowLabel(RowLabelKind.Answer, "false"),
            new RowLabel(RowLabelKind.Answer, "false"));
    }

    private static Rule RuleOfKind(string kind) => kind switch
    {
        "boolean" => Policy.Rule("flag").Boolean("question-b").WhenTrue(Verdict.Deny),
        "choice" => Policy.Rule("route").Choice("question-c")
            .Option("answer", "description-a", Verdict.Allow)
            .Option("refuse", "description-b", Verdict.Deny)
            .Build(),
        "score" => Policy.Rule("harm").Score("question-s", "low", "high").DenyAtOrAbove("high").Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
