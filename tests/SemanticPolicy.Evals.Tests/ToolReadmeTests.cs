using System.CommandLine;
using System.Text.RegularExpressions;
using SemanticPolicy.Evals.Cli;

namespace SemanticPolicy.Evals.Tests;

public sealed partial class ToolReadmeTests
{
    // Every documented invocation starts with this, and its --project belongs to `dotnet run`, not to the tool.
    private const string _invocation = "dotnet run --project tools/SemanticPolicy.Evals --";

    // Added by the command-line library to every command; the README names it once for all of them.
    private const string _help = "--help";

    private static readonly string[] _verbs = [.. EvalsCli.Build().Subcommands.Select(command => command.Name)];

    public static TheoryData<string> Verbs { get; } = new(_verbs);

    // A structural guard: the README is written by hand against the verbs' definitions, so an option renamed,
    // added or removed on either side would leave the README describing a tool that does not exist.
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task Every_Option_The_Tool_Readme_Names_Exists_In_Help_And_Every_Help_Option_Is_In_The_Readme(string verb)
    {
        string readme = await File.ReadAllTextAsync(
            Path.Combine(ExampleDatasetsTests.RepositoryRoot(), "tools", "SemanticPolicy.Evals", "README.md"),
            TestContext.Current.CancellationToken);
        readme = readme.Replace(_invocation, string.Empty, StringComparison.Ordinal);
        HashSet<string> everyHelpOption = [.. Options(await HelpAsync())];
        foreach (string other in _verbs)
        {
            everyHelpOption.UnionWith(Options(await HelpAsync(other)));
        }

        IEnumerable<string> helpOptions = Options(await HelpAsync(verb)).Where(option => option != _help);
        IEnumerable<string> sectionOptions = Options(Section(readme, verb)).Where(option => option != _help);

        sectionOptions.Should().BeEquivalentTo(helpOptions, "the README's '{0}' section documents that verb's options", verb);
        Options(readme).Should().Contain(_help).And.BeSubsetOf(everyHelpOption);
    }

    private static async Task<string> HelpAsync(params string[] verb)
    {
        StringWriter output = new();
        StringWriter error = new();
        RootCommand root = EvalsCli.Build(new CliIo(output, error));

        int exitCode = await root.Parse([.. verb, _help]).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error },
            TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.Success);
        return output.ToString();
    }

    // From the verb's own heading to the next heading at the same level or above.
    private static string Section(string readme, string verb)
    {
        string[] lines = readme.ReplaceLineEndings("\n").Split('\n');
        int start = Array.IndexOf(lines, $"### `{verb}`");
        start.Should().BeGreaterThanOrEqualTo(0, "the README has a '### `{0}`' section", verb);
        int end = Array.FindIndex(lines, start + 1, line => SectionHeading().IsMatch(line));
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    private static string[] Options(string text) =>
        [.. OptionToken().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z-]*[a-z]")]
    private static partial Regex OptionToken();

    [GeneratedRegex("^#{1,3} ")]
    private static partial Regex SectionHeading();
}
