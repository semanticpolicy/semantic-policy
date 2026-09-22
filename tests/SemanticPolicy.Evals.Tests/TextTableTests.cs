using SemanticPolicy.Evals.Output;

namespace SemanticPolicy.Evals.Tests;

public sealed class TextTableTests
{
    [Fact]
    public void TextTable_Pads_Every_Column_To_Its_Widest_Cell_And_Right_Aligns_Numbers()
    {
        TextTable table = new("rule", "rows", "rate", "note");
        table.AddRow("prompt-injection", "3", "0.500", "ok");
        table.AddRow("route", "12", "0.125", "-");
        StringWriter writer = new() { NewLine = "\n" };

        table.Write(writer);

        writer.ToString().Should().Be(
            "rule              rows   rate  note\n" +
            "----------------  ----  -----  ----\n" +
            "prompt-injection     3  0.500  ok\n" +
            "route               12  0.125  -\n");
    }

    [Fact]
    public void TextTable_Rejects_A_Row_With_The_Wrong_Number_Of_Cells()
    {
        TextTable table = new("a", "b");

        Action act = () => table.AddRow("only-one");

        act.Should().Throw<ArgumentException>();
    }
}
