using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SemanticPolicy.Mcp.Gateway.Tests.Support;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed partial class GatewayReadmeTests
{
    // Options the README shows on the commands around the gateway, which are theirs, not its: dnx's, before a line's
    // first --, and those of Claude Code's mcp add.
    private static readonly string[] _theirOptions = ["--prerelease", "--scope", "--env"];

    // A structural guard, after the evaluation tool's README tests. The README is the package's page on nuget.org and
    // is written by hand, so a gateway file it shows that no longer loads, a link with nothing to resolve against
    // there, or an option the command line no longer has would leave a page nobody can follow while every other test
    // still passes. The sample gateway file it shows is the one the samples folder holds.
    [Fact]
    public async Task Readme_Gateway_Files_Load_And_Links_Stay_On_The_Web()
    {
        string readme = (await File.ReadAllTextAsync(
            Repository.PathOf("tools", "SemanticPolicy.Mcp.Gateway", "README.md"),
            TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");

        string[] gatewayFiles = [.. JsonBlock().Matches(readme).Select(match => match.Groups["json"].Value).Where(IsGatewayFile)];
        gatewayFiles.Should().NotBeEmpty("the README shows a gateway file");
        using Workspace workspace = Workspace.Create();
        for (int index = 0; index < gatewayFiles.Length; index++)
        {
            string path = workspace.Write($"gateway-{index}.json", gatewayFiles[index]);
            Action read = () => GatewayFile.Read(path);
            read.Should().NotThrow("gateway file {0} in the README loads", index + 1);
        }

        JsonNode sample = JsonNode.Parse(await File.ReadAllTextAsync(Repository.Sample("gateway.json"), TestContext.Current.CancellationToken))!;
        gatewayFiles.Should().Contain(json => JsonNode.DeepEquals(JsonNode.Parse(json), sample), "the README shows the sample gateway file as it is");

        string prose = FencedBlock().Replace(readme, string.Empty);
        string[] anchors = [.. Heading().Matches(prose).Select(match => Anchor(match.Groups["text"].Value))];
        string[] targets =
        [
            .. InlineLink().Matches(prose).Concat(LinkDefinition().Matches(prose)).Select(match => match.Groups["target"].Value),
        ];
        targets.Should().NotBeEmpty().And.OnlyContain(
            target => target.StartsWith("https://", StringComparison.Ordinal) || anchors.Contains(target));

        // The options of a documented `dotnet tool` command and of the evaluation tool's commands are theirs.
        string shown = EvaluationToolCommand().Replace(DotnetToolCommand().Replace(readme, string.Empty), string.Empty);
        GatewayRun help = await GatewayRun.InvokeAsync("--help");
        string[] gatewayOptions = Options(help.Output);
        Options(shown).Should().Contain("--gateway").And.OnlyContain(
            option => gatewayOptions.Contains(option) || _theirOptions.Contains(option));
    }

    // A gateway file names a point, or a providers file by its path; a providers file, a host's configuration, a log
    // line or a dataset row does neither.
    private static bool IsGatewayFile(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && (root.TryGetProperty("results", out _)
                || root.TryGetProperty("definitions", out _)
                || (root.TryGetProperty("providers", out JsonElement providers) && providers.ValueKind == JsonValueKind.String));
    }

    // The anchor GitHub and nuget.org give a heading: lower case, punctuation dropped, spaces turned into hyphens.
    private static string Anchor(string heading) =>
        "#" + Punctuation().Replace(heading.Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-');

    private static string[] Options(string text) =>
        [.. OptionToken().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z-]*[a-z]")]
    private static partial Regex OptionToken();

    // An inline link or image, `[text](target)`: the target runs to the first space or closing parenthesis.
    [GeneratedRegex(@"\]\((?<target>[^)\s]*)")]
    private static partial Regex InlineLink();

    // A reference-style link's definition, `[label]: target`, on a line of its own.
    [GeneratedRegex(@"^ {0,3}\[[^\]]+\]:[ \t]*(?<target>\S+)", RegexOptions.Multiline)]
    private static partial Regex LinkDefinition();

    [GeneratedRegex(@"^#{1,6} (?<text>.+)$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"[^\p{L}\p{N} -]")]
    private static partial Regex Punctuation();

    // A fenced block of any language: from its opening line to the closing fence.
    [GeneratedRegex(@"^```.*?^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

    // A fenced JSON block: from its opening line to the closing fence.
    [GeneratedRegex(@"^```json\n(?<json>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    // A `dotnet tool` command, in a code block or in inline code: to the end of the line or the closing backtick.
    [GeneratedRegex(@"dotnet tool [^`\n]*")]
    private static partial Regex DotnetToolCommand();

    // A command of the evaluation tool, whose command is `semantic-policy`: to the end of the line or the closing
    // backtick. The gateway's own, `semantic-policy-mcp`, has no space after the name and is kept.
    [GeneratedRegex(@"semantic-policy (?:samples|run|report|calibrate|sweep|compare)\b[^`\n]*")]
    private static partial Regex EvaluationToolCommand();
}
