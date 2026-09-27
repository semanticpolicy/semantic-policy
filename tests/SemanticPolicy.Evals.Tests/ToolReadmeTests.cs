using System.CommandLine;
using System.Text.RegularExpressions;
using SemanticPolicy.Evals.Cli;

namespace SemanticPolicy.Evals.Tests;

public sealed partial class ToolReadmeTests
{
    // Added by the command-line library to every command; the README names it once for all of them.
    private const string _help = "--help";

    // The options these verbs share are documented once, under this heading, and not repeated under each verb.
    private const string _sharedHeading = "### Options of `run`, `report`, `sweep` and `compare`";

    // The verbs the shared heading names. `samples` reads no policy, dataset or recording, and takes none of them.
    private static readonly string[] _sharedVerbs = ["run", "report", "sweep", "compare"];

    private static readonly string[] _verbs = [.. EvalsCli.Build().Subcommands.Select(command => command.Name)];

    public static TheoryData<string> Verbs { get; } = new(_verbs);

    // A structural guard: the README is written by hand against the verbs' definitions, so an option renamed,
    // added or removed on either side would leave the README describing a tool that does not exist. A verb's
    // options are those its own section names, plus those of the shared section for the verbs it names.
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task Every_Option_The_Tool_Readme_Names_Exists_In_Help_And_Every_Help_Option_Is_In_The_Readme(string verb)
    {
        // The options of a documented `dotnet tool` command, such as the install, are dotnet's, not the tool's.
        string readme = DotnetToolCommand().Replace(await ReadmeAsync(), string.Empty);
        HashSet<string> everyHelpOption = [.. Options(await HelpAsync())];
        foreach (string other in _verbs)
        {
            everyHelpOption.UnionWith(Options(await HelpAsync(other)));
        }

        bool shared = _sharedVerbs.Contains(verb);
        IEnumerable<string> helpOptions = Options(await HelpAsync(verb)).Where(option => option != _help);
        string documented = Section(readme, $"### `{verb}`") + (shared ? "\n" + Section(readme, _sharedHeading) : string.Empty);
        IEnumerable<string> sectionOptions = Options(documented).Where(option => option != _help);

        sectionOptions.Should().BeEquivalentTo(helpOptions,
            "the README documents the '{0}' verb's options in its own section{1}", verb, shared ? $" and in '{_sharedHeading}'" : string.Empty);
        Options(readme).Should().Contain(_help).And.BeSubsetOf(everyHelpOption);
    }

    // A structural guard: the README is also the package's README on nuget.org, where a link relative to the repository
    // has nothing to resolve against and renders broken, while every other test still passes.
    [Fact]
    public async Task Tool_Readme_Links_Only_To_Absolute_Urls_Or_Its_Own_Headings()
    {
        string readme = await ReadmeAsync();

        string[] targets =
        [
            .. InlineLink().Matches(readme).Concat(LinkDefinition().Matches(readme))
                .Select(match => match.Groups["target"].Value),
        ];

        targets.Should().NotBeEmpty().And.OnlyContain(
            target => target.StartsWith("https://", StringComparison.Ordinal) || target.StartsWith('#'));
    }

    private static Task<string> ReadmeAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(ExampleDatasetsTests.RepositoryRoot(), "tools", "SemanticPolicy.Evals", "README.md"),
            TestContext.Current.CancellationToken);

    private static async Task<string> HelpAsync(params string[] verb)
    {
        StringWriter output = new();
        StringWriter error = new();
        Command root = EvalsCli.Build(new CliIo(output, error));

        int exitCode = await root.Parse([.. verb, _help]).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error },
            TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.Success);
        return output.ToString();
    }

    // From the heading to the next heading at the same level or above.
    private static string Section(string readme, string heading)
    {
        string[] lines = readme.ReplaceLineEndings("\n").Split('\n');
        int start = Array.IndexOf(lines, heading);
        start.Should().BeGreaterThanOrEqualTo(0, "the README has a '{0}' section", heading);
        int end = Array.FindIndex(lines, start + 1, line => SectionHeading().IsMatch(line));
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    private static string[] Options(string text) =>
        [.. OptionToken().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z-]*[a-z]")]
    private static partial Regex OptionToken();

    [GeneratedRegex("^#{1,3} ")]
    private static partial Regex SectionHeading();

    // An inline link or image, `[text](target)`: the target runs to the first space or closing parenthesis.
    [GeneratedRegex(@"\]\((?<target>[^)\s]*)")]
    private static partial Regex InlineLink();

    // A reference-style link's definition, `[label]: target`, on a line of its own.
    [GeneratedRegex(@"^ {0,3}\[[^\]]+\]:[ \t]*(?<target>\S+)", RegexOptions.Multiline)]
    private static partial Regex LinkDefinition();

    // A `dotnet tool` command, in a code block or in inline code: to the end of the line or the closing backtick.
    [GeneratedRegex(@"dotnet tool [^`\n]*")]
    private static partial Regex DotnetToolCommand();
}
