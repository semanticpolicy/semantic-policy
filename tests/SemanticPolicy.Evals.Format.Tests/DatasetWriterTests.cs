using System.Security.Cryptography;
using System.Text.Json;

namespace SemanticPolicy.Evals.Format.Tests;

public sealed class DatasetWriterTests
{
    [Fact]
    public async Task DatasetWriter_Digest_Equals_The_Readers_Digest_Of_The_Written_File()
    {
        using TempFile file = TempFile.Write("");
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        string sha256;
        await using (DatasetWriter writer = await DatasetWriter.CreateAsync(file.Path, cancellation))
        {
            await writer.WriteAsync(Row("a", Text("zażółć \"gęślą\" jaźń + 1 < 2"), "true"), cancellation);
            await writer.WriteAsync(Row("b", Parts(), "deny") with { Metadata = Metadata() }, cancellation);
            await writer.WriteAsync(Row("c", Text("line one\r\nline two"), "abstain"), cancellation);
            sha256 = await writer.CompleteAsync(cancellation);
        }

        byte[] bytes = await File.ReadAllBytesAsync(file.Path, cancellation);
        sha256.Should().Be(DatasetReader.Read(file.Path).Sha256)
            .And.Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));

        // The bytes do not depend on the machine: no byte-order mark, and one \n per row.
        bytes.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
        bytes.Should().NotContain((byte)'\r');
        bytes.Count(value => value == (byte)'\n').Should().Be(3);
        bytes[^1].Should().Be((byte)'\n');
    }

    [Theory]
    [InlineData("true", "text", false)]
    [InlineData("false", "parts", true)]
    [InlineData("deny", "parts", false)]
    [InlineData("review", "text", true)]
    [InlineData("ambiguous", "parts", true)]
    [InlineData("abstain", "text", false)]
    public async Task Dataset_Written_By_The_Writer_Reads_Back_Row_For_Row(string label, string input, bool withMetadata)
    {
        using TempFile file = TempFile.Write("");
        DatasetRow first = Row("a", input == "text" ? Text("sample text") : Parts(), label);
        DatasetRow[] written =
        [
            withMetadata ? first with { Metadata = Metadata() } : first,
            Row("b", Text("neighbour"), "true"),
        ];

        await WriteAsync(file.Path, written);

        Dataset dataset = DatasetReader.Read(file.Path);
        dataset.Rows.Should().HaveCount(written.Length);
        for (int index = 0; index < written.Length; index++)
        {
            DatasetRow read = dataset.Rows[index];
            read.Line.Should().Be(index + 1);
            read.Id.Should().Be(written[index].Id);
            read.Label.Should().Be(written[index].Label);
            Shape(read.Input).Should().Equal(Shape(written[index].Input));
            read.Input.ToJson().GetRawText().Should().Be(written[index].Input.ToJson().GetRawText());
            read.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value.GetRawText())
                .Should().Equal(written[index].Metadata.ToDictionary(pair => pair.Key, pair => pair.Value.GetRawText()));
        }
    }

    [Theory]
    [InlineData("an empty id", "row 2")]
    [InlineData("a white-space id", "row 2")]
    [InlineData("an id written twice", "row 2")]
    [InlineData("an answer label that is null", "'b'")]
    [InlineData("an answer label that is empty", "'b'")]
    [InlineData("an answer label 'ambiguous'", "'b'")]
    [InlineData("an answer label 'abstain'", "'b'")]
    [InlineData("an abstain label with an answer", "'b'")]
    [InlineData("an ambiguous label with an answer", "'b'")]
    [InlineData("a JSON part holding a JSON string", "'b'")]
    [InlineData("an option with a null key", "option at index 1")]
    [InlineData("an option whose verdict is not a Verdict name", "option at index 0")]
    public async Task DatasetWriter_Refuses_A_Row_That_Would_Read_Back_Differently(string fault, string names)
    {
        using TempFile file = TempFile.Write("");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        DatasetRow refused = fault switch
        {
            "an empty id" => Row("", Text("secret-content"), "true"),
            "a white-space id" => Row(" ", Text("secret-content"), "true"),
            "an id written twice" => Row("a", Text("secret-content"), "true"),
            "an answer label that is null" => Row("b", Text("secret-content"), "true") with { Label = new RowLabel(RowLabelKind.Answer, null) },
            "an answer label that is empty" => Row("b", Text("secret-content"), ""),
            "an answer label 'ambiguous'" => Row("b", Text("secret-content"), "true") with { Label = new RowLabel(RowLabelKind.Answer, "ambiguous") },
            "an answer label 'abstain'" => Row("b", Text("secret-content"), "true") with { Label = new RowLabel(RowLabelKind.Answer, "abstain") },
            "an abstain label with an answer" => Row("b", Text("secret-content"), "true") with { Label = new RowLabel(RowLabelKind.Abstain, "true") },
            "an ambiguous label with an answer" => Row("b", Text("secret-content"), "true") with { Label = new RowLabel(RowLabelKind.Ambiguous, "true") },
            "a JSON part holding a JSON string" => Row(
                "b",
                new SemanticContext([ContextPart.Text("message", "secret-content"), ContextPart.Json("payload", Json("\"secret-content\""))]),
                "true"),
            "an option with a null key" => Row("b", Text("secret-content"), "true") with { Options = RouteOptions(new ChoiceOption("allow", "secret-content", Verdict.Allow), new ChoiceOption("deny", "secret-content", Verdict.Deny) with { Key = null! }) },
            "an option whose verdict is not a Verdict name" => Row("b", Text("secret-content"), "true") with { Options = RouteOptions(new ChoiceOption("allow", "secret-content", (Verdict)42), new ChoiceOption("deny", "secret-content", Verdict.Deny)) },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        string message;
        await using (DatasetWriter writer = await DatasetWriter.CreateAsync(file.Path, cancellation))
        {
            await writer.WriteAsync(Row("a", Text("first"), "true"), cancellation);
            Func<Task> write = () => writer.WriteAsync(refused, cancellation);
            message = (await write.Should().ThrowAsync<EvalsFormatException>()).Which.Message;
            await writer.WriteAsync(Row("c", Text("after"), "false"), cancellation);
            await writer.CompleteAsync(cancellation);
        }

        message.Should().Contain(file.Path).And.Contain(names).And.NotContain("secret-content");
        if (fault == "a JSON part holding a JSON string")
        {
            message.Should().Contain("'payload'").And.Contain("text part");
        }

        if (fault.StartsWith("an option", StringComparison.Ordinal))
        {
            message.Should().Contain("'b'").And.Contain("rule 'route'");
        }

        DatasetReader.Read(file.Path).Rows.Select(row => row.Id).Should().Equal("a", "c");
    }

    [Fact]
    public async Task DatasetWriter_That_Failed_To_Write_A_Row_Takes_No_More_Rows_And_Returns_No_Digest()
    {
        using TempFile file = TempFile.Write("");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await using DatasetWriter writer = await DatasetWriter.CreateAsync(file.Path, cancellation);
        await writer.WriteAsync(Row("a", Text("first"), "true"), cancellation);

        // The bytes of a cancelled write may be partly in the file, so a digest of the rows before it would not match.
        Func<Task> cancelledWrite = () => writer.WriteAsync(Row("b", Text("second"), "false"), cancelled.Token);
        await cancelledWrite.Should().ThrowAsync<OperationCanceledException>();

        Func<Task> next = () => writer.WriteAsync(Row("c", Text("third"), "false"), cancellation);
        (await next.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(file.Path);

        Func<Task<string>> complete = () => writer.CompleteAsync(cancellation);
        await complete.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task WriteAsync(string path, IEnumerable<DatasetRow> rows)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using DatasetWriter writer = await DatasetWriter.CreateAsync(path, cancellation);
        foreach (DatasetRow row in rows)
        {
            await writer.WriteAsync(row, cancellation);
        }

        await writer.CompleteAsync(cancellation);
    }

    private static DatasetRow Row(string id, SemanticContext input, string label) => new(id, input, Samples.Label(label));

    private static Dictionary<string, IReadOnlyList<ChoiceOption>> RouteOptions(params ChoiceOption[] options) =>
        new(StringComparer.Ordinal) { ["route"] = options };

    private static SemanticContext Text(string text) => SemanticContext.FromText(text);

    // Two parts: text, and JSON that a provider reads as structure.
    private static SemanticContext Parts() =>
        new([ContextPart.Text("message", "first block"), ContextPart.Json("context", Json("""{"turn": 2, "tags": ["a", "b"]}"""))]);

    private static Dictionary<string, JsonElement> Metadata() => new(StringComparer.Ordinal)
    {
        ["source"] = Json("\"synthetic\""),
        ["weight"] = Json("2.50"),
    };

    // The part's name and kind, which is all a part prints: a text part and a JSON string part differ here.
    private static IEnumerable<string> Shape(SemanticContext context) => context.Parts.Select(part => part.ToString());

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
