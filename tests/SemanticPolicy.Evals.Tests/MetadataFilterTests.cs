using SemanticPolicy.Evals.Datasets;

namespace SemanticPolicy.Evals.Tests;

public sealed class MetadataFilterTests
{
    [Fact]
    public void Filter_Keeps_Only_Rows_Whose_Metadata_Equals_Every_Given_Value()
    {
        using TempFile file = TempFile.Write(
            """{"id": "a", "input": "x", "label": true, "metadata": {"source": "synthetic", "turn": 1}}""" + "\n" +
            """{"id": "b", "input": "x", "label": true, "metadata": {"source": "synthetic", "turn": 2}}""" + "\n" +
            """{"id": "c", "input": "x", "label": true, "metadata": {"source": "collected", "turn": 1}}""" + "\n" +
            """{"id": "d", "input": "x", "label": true}""" + "\n");
        IReadOnlyList<DatasetRow> rows = DatasetReader.Read(file.Path).Rows;
        MetadataFilter[] filters =
        [
            MetadataFilter.Parse("metadata.source=synthetic"),
            MetadataFilter.Parse("metadata.turn=1"),
        ];

        IReadOnlyList<DatasetRow> kept = MetadataFilter.Apply(filters, rows);

        kept.Select(row => row.Id).Should().Equal("a");
        filters[0].Should().Be(new MetadataFilter("source", "synthetic"));
    }

    [Theory]
    [InlineData("source=synthetic")]
    [InlineData("metadata.source")]
    [InlineData("metadata.=synthetic")]
    public void Filter_Rejects_A_Malformed_Token(string token)
    {
        Action act = () => MetadataFilter.Parse(token);

        act.Should().Throw<EvalsException>().Which.ExitCode.Should().Be(ExitCodes.UsageOrData);
    }
}
