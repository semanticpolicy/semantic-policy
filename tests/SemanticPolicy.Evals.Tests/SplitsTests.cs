using SemanticPolicy.Evals.Datasets;

namespace SemanticPolicy.Evals.Tests;

public sealed class SplitsTests
{
    [Theory]
    [InlineData("tune", "test", true)]
    [InlineData("dev", "holdout", false)]
    public void Splits_Read_Metadata_Split_With_Default_And_Overridden_Names(string tune, string test, bool byDefault)
    {
        SplitNames names = byDefault ? new SplitNames() : new SplitNames(tune, test);
        IReadOnlyList<DatasetRow> rows = Rows(
            $$$"""{"id": "a", "input": "x", "label": true, "metadata": {"split": "{{{tune}}}"}}""",
            $$$"""{"id": "b", "input": "x", "label": true, "metadata": {"split": "{{{test}}}"}}""",
            $$$"""{"id": "c", "input": "x", "label": true, "metadata": {"split": "{{{tune}}}"}}""");

        SplitSelection selection = Splits.FromMetadata(rows, names);

        selection.Source.Should().Be(SplitSource.Metadata);
        selection.Tune.Select(row => row.Id).Should().Equal("a", "c");
        selection.Test.Select(row => row.Id).Should().Equal("b");
    }

    [Fact]
    public void Splits_Reject_A_Dataset_Where_Only_Some_Rows_Carry_A_Split()
    {
        IReadOnlyList<DatasetRow> rows = Rows(
            """{"id": "a", "input": "x", "label": true, "metadata": {"split": "tune"}}""",
            """{"id": "b", "input": "x", "label": true}""");

        Action act = () => Splits.FromMetadata(rows, new SplitNames());

        act.Should().Throw<EvalsException>().Which.Message.Should().Contain("line 2").And.Contain("'b'");
    }

    [Fact]
    public void Splits_Reject_A_Split_Value_That_Is_Neither_Name()
    {
        IReadOnlyList<DatasetRow> rows = Rows(
            """{"id": "a", "input": "x", "label": true, "metadata": {"split": "tune"}}""",
            """{"id": "b", "input": "x", "label": true, "metadata": {"split": "validation"}}""");

        Action act = () => Splits.FromMetadata(rows, new SplitNames());

        act.Should().Throw<EvalsException>().Which.Message.Should().Contain("line 2").And.Contain("'b'");
    }

    [Fact]
    public void Splits_From_Two_Files_Assign_By_File_And_Reject_Rows_That_Also_Carry_Metadata_Split()
    {
        IReadOnlyList<DatasetRow> tuneRows = Rows(
            """{"id": "a", "input": "x", "label": true}""",
            """{"id": "b", "input": "x", "label": true}""");
        IReadOnlyList<DatasetRow> testRows = Rows("""{"id": "c", "input": "x", "label": true}""");

        SplitSelection selection = Splits.FromFiles(tuneRows, testRows);

        selection.Source.Should().Be(SplitSource.Files);
        selection.Tune.Select(row => row.Id).Should().Equal("a", "b");
        selection.Test.Select(row => row.Id).Should().Equal("c");

        IReadOnlyList<DatasetRow> tainted = Rows(
            """{"id": "d", "input": "x", "label": true, "metadata": {"split": "tune"}}""");
        Action act = () => Splits.FromFiles(tainted, testRows);

        act.Should().Throw<EvalsException>().Which.Message.Should().Contain("'d'").And.Contain("metadata.split");
    }

    [Fact]
    public void Splits_From_Two_Files_Reject_An_Id_Present_In_Both()
    {
        IReadOnlyList<DatasetRow> tuneRows = Rows("""{"id": "a", "input": "x", "label": true}""");
        IReadOnlyList<DatasetRow> testRows = Rows(
            """{"id": "b", "input": "x", "label": true}""",
            """{"id": "a", "input": "x", "label": true}""");

        Action act = () => Splits.FromFiles(tuneRows, testRows);

        act.Should().Throw<EvalsException>().Which.Message.Should().Contain("'a'").And.Contain("line 2");
    }

    [Fact]
    public void Splits_Without_Any_Split_Return_The_Same_Rows_As_Tune_And_Test_And_Say_So()
    {
        IReadOnlyList<DatasetRow> rows = Rows(
            """{"id": "a", "input": "x", "label": true}""",
            """{"id": "b", "input": "x", "label": true}""");

        SplitSelection selection = Splits.FromMetadata(rows, new SplitNames());

        selection.Source.Should().Be(SplitSource.None);
        selection.Tune.Should().Equal(rows);
        selection.Test.Should().Equal(rows);
    }

    private static IReadOnlyList<DatasetRow> Rows(params string[] lines)
    {
        using TempFile file = TempFile.Write(string.Join("\n", lines) + "\n");
        return DatasetReader.Read(file.Path).Rows;
    }
}
