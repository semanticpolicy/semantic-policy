using System.Globalization;
using System.Text.Json;

namespace SemanticPolicy.Mcp.Gateway;

/// <summary>
/// Reads the gateway file: where the providers file is, and for each screening point its policy file and the action it
/// takes on each verdict. Paths are resolved against the gateway file's own directory; nothing they name is read here.
/// </summary>
internal static class GatewayFile
{
    private const string _providers = "providers";
    private const string _results = "results";
    private const string _definitions = "definitions";
    private const string _policy = "policy";
    private const string _allow = "allow";
    private const string _action = "action";
    private const string _message = "message";
    private const string _withheld = "withheld";
    private const string _fallback = "fallback";

    // In the order a point's mapping is checked, so the first one missing is the one a refusal names.
    private static readonly string[] _verdicts = ["warn", "escalate", "deny", "abstain"];

    private static readonly Dictionary<string, GatewayAction> _actions = new(StringComparer.Ordinal)
    {
        ["pass"] = GatewayAction.Pass,
        ["annotate"] = GatewayAction.Annotate,
        ["withhold"] = GatewayAction.Withhold,
        ["hide"] = GatewayAction.Hide,
        ["ask"] = GatewayAction.Ask,
    };

    // A tool result can be annotated, withheld or asked about, never hidden; a tool definition can only be hidden.
    private static readonly Dictionary<string, GatewayAction[]> _takes = new(StringComparer.Ordinal)
    {
        [_results] = [GatewayAction.Pass, GatewayAction.Annotate, GatewayAction.Withhold, GatewayAction.Ask],
        [_definitions] = [GatewayAction.Pass, GatewayAction.Hide],
    };

    // What an ask does when the host cannot ask: any result action but another ask.
    private static readonly GatewayAction[] _fallbacks = [GatewayAction.Pass, GatewayAction.Annotate, GatewayAction.Withhold];

    /// <summary>Reads and checks the file's shape and its mapping.</summary>
    /// <param name="path">The gateway file, as given on the command line.</param>
    /// <exception cref="GatewayException">
    /// The file cannot be read, is not JSON, has a property it does not know or gives one twice, or a point's policy
    /// or mapping is missing or wrong. The message names the file, the point and the key, never a value.
    /// </exception>
    public static GatewayFileContent Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GatewayException($"Gateway file '{path}' cannot be read: {e.Message}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            // Never the exception's message: it can quote the text around the error.
            throw new GatewayException($"Gateway file '{path}' is not valid JSON at {Position(e)}.");
        }

        using (document)
        {
            Where where = new(path, Path.GetDirectoryName(Path.GetFullPath(path))!);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw where.Refuse($"the file is not an object with '{_providers}', '{_results}' and '{_definitions}'.");
            }

            JsonElement? providers = null;
            JsonElement? results = null;
            JsonElement? definitions = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case _providers when providers is null:
                        providers = property.Value;
                        break;
                    case _results when results is null:
                        results = property.Value;
                        break;
                    case _definitions when definitions is null:
                        definitions = property.Value;
                        break;
                    case _providers or _results or _definitions:
                        throw where.Refuse($"'{property.Name}' is given twice.");
                    default:
                        throw where.Refuse(
                            $"top-level property '{property.Name}' is not known; the file holds '{_providers}', '{_results}' and '{_definitions}'.");
                }
            }

            PointEntry? resultsPoint = results is { } resultsValue ? ReadPoint(where, _results, resultsValue) : null;
            PointEntry? definitionsPoint = definitions is { } definitionsValue ? ReadPoint(where, _definitions, definitionsValue) : null;

            // With no point there is nothing to evaluate, so a file without providers is still a gateway: one that
            // screens nothing.
            string? providersPath = providers is { } providersValue
                ? where.Resolve(_providers, providersValue)
                : resultsPoint is null && definitionsPoint is null
                    ? null
                    : throw where.Refuse($"'{_providers}' is missing; a point's policy binds providers it registers.");
            return new GatewayFileContent(providersPath, resultsPoint, definitionsPoint);
        }
    }

    private static PointEntry ReadPoint(Where where, string name, JsonElement point)
    {
        if (point.ValueKind != JsonValueKind.Object)
        {
            throw where.Refuse($"{name} is not an object with '{_policy}' and an action for each verdict.");
        }

        JsonElement? policy = null;
        Dictionary<string, JsonElement> entries = new(StringComparer.Ordinal);
        foreach (JsonProperty property in point.EnumerateObject())
        {
            string at = $"{name}.{property.Name}";
            if (property.Name == _policy)
            {
                policy = policy is null ? property.Value : throw where.Refuse($"{at} is given twice.");
            }
            else if (property.Name == _allow)
            {
                throw where.Refuse($"{at} is refused: Allow always passes, so a point maps {Listed(_verdicts)} alone.");
            }
            else if (_verdicts.Contains(property.Name))
            {
                if (!entries.TryAdd(property.Name, property.Value))
                {
                    throw where.Refuse($"{at} is given twice.");
                }
            }
            else
            {
                throw where.Refuse($"{at} is not a property of a point; a point has '{_policy}' and {Listed(_verdicts)}.");
            }
        }

        string policyPath = policy is { } policyValue
            ? where.Resolve($"{name}.{_policy}", policyValue)
            : throw where.Refuse($"{name}.{_policy} is missing.");
        MappedAction[] mapped = [.. _verdicts.Select(verdict => entries.TryGetValue(verdict, out JsonElement entry)
            ? ReadAction(where, name, $"{name}.{verdict}", entry, isFallback: false)
            : throw where.Refuse($"{name}.{verdict} is missing."))];
        return new PointEntry(name, policyPath, new VerdictMapping(mapped[0], mapped[1], mapped[2], mapped[3]));
    }

    // A verdict's entry, or an ask's fallback when isFallback is set. at is the key a refusal names.
    private static MappedAction ReadAction(Where where, string point, string at, JsonElement entry, bool isFallback)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw where.Refuse($"{at} is not an object with '{_action}' and '{_message}'.");
        }

        JsonElement? action = null;
        JsonElement? message = null;
        JsonElement? withheld = null;
        JsonElement? fallback = null;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case _action when action is null:
                    action = property.Value;
                    break;
                case _message when message is null:
                    message = property.Value;
                    break;
                case _withheld when withheld is null:
                    withheld = property.Value;
                    break;
                case _fallback when fallback is null:
                    fallback = property.Value;
                    break;
                case _action or _message or _withheld or _fallback:
                    throw where.Refuse($"{at}.{property.Name} is given twice.");
                default:
                    throw where.Refuse(
                        $"{at}.{property.Name} is not a property of an action; an action has '{_action}' and '{_message}', and {Name(GatewayAction.Ask)} also '{_withheld}' and '{_fallback}'.");
            }
        }

        if (action is not { } actionValue)
        {
            throw where.Refuse($"{at}.{_action} is missing.");
        }

        GatewayAction[] takes = isFallback ? _fallbacks : _takes[point];
        GatewayAction? named = actionValue.ValueKind == JsonValueKind.String && _actions.TryGetValue(actionValue.GetString()!, out GatewayAction known)
            ? known
            : null;
        if (named == GatewayAction.Ask && isFallback)
        {
            throw where.Refuse(
                $"{at}.{_action} is refused: a fallback is what happens when the host cannot ask; it takes {Listed(takes.Select(Name))}.");
        }

        if (named == GatewayAction.Ask && !takes.Contains(GatewayAction.Ask))
        {
            throw where.Refuse(
                $"{at}.{_action} is refused: the protocol lets a server ask the person during a tool call, never while tools are listed; the {point} point takes {Listed(takes.Select(Name))}.");
        }

        if (named is not { } taken || !takes.Contains(taken))
        {
            throw where.Refuse(isFallback
                ? $"{at}.{_action} is not an action a fallback takes; it takes {Listed(takes.Select(Name))}."
                : $"{at}.{_action} is not an action the {point} point takes; it takes {Listed(takes.Select(Name))}.");
        }

        if (taken != GatewayAction.Ask && (withheld is not null ? _withheld : fallback is not null ? _fallback : null) is { } askOnly)
        {
            throw where.Refuse($"{at}.{askOnly} is refused: only {Name(GatewayAction.Ask)} takes it.");
        }

        if (taken == GatewayAction.Pass)
        {
            return message is null
                ? new MappedAction(taken, null)
                : throw where.Refuse($"{at}.{_message} is refused: {Name(taken)} shows nothing.");
        }

        // The model and the person are shown the operator's text and nothing of the gateway's own, so an action that
        // shows something has to be given it.
        if (taken != GatewayAction.Ask)
        {
            return new MappedAction(taken, Text(where, at, _message, message, $"{Name(taken)} shows it to the model"));
        }

        string dialog = Text(where, at, _message, message, $"{Name(taken)} shows it to the person");
        string withheldText = Text(where, at, _withheld, withheld, $"{Name(taken)} shows it to the model when the person does not accept");
        MappedAction instead = fallback is { } fallbackValue
            ? ReadAction(where, point, $"{at}.{_fallback}", fallbackValue, isFallback: true)
            : throw where.Refuse($"{at}.{_fallback} is missing; {Name(taken)} takes it when the host cannot ask.");
        return new MappedAction(taken, dialog) { Withheld = withheldText, Fallback = instead };
    }

    // One of the operator's texts: given, a string, and not blank.
    private static string Text(Where where, string at, string key, JsonElement? value, string shownBy) =>
        value is not { } given
            ? throw where.Refuse($"{at}.{key} is missing; {shownBy}.")
            : given.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(given.GetString())
                ? given.GetString()!
                : throw where.Refuse($"{at}.{key} is not a text; {shownBy}.");

    private static string Name(GatewayAction action) => _actions.First(known => known.Value == action).Key;

    private static string Listed(IEnumerable<string> names)
    {
        string[] all = [.. names];
        return all.Length == 1 ? all[0] : $"{string.Join(", ", all[..^1])} and {all[^1]}";
    }

    private static string Position(JsonException e)
    {
        string position = string.Create(
            CultureInfo.InvariantCulture,
            $"line {e.LineNumber + 1 ?? 1}, byte {e.BytePositionInLine + 1 ?? 1}");
        return e.Path is null or "$" ? position : $"{position}, in {e.Path}";
    }

    // Every refusal starts the same way. It never quotes a value: a message is the operator's text, a path can be
    // anything, and these messages land in the host's log.
    private readonly record struct Where(string File, string BaseDirectory)
    {
        public GatewayException Refuse(string detail) => new($"Gateway file '{File}': {detail}");

        public string Resolve(string at, JsonElement value)
        {
            string? resolved = null;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                try
                {
                    resolved = Path.GetFullPath(value.GetString()!, BaseDirectory);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A path the system cannot take is refused below as no path at all.
                    resolved = null;
                }
            }

            return resolved ?? throw Refuse($"{at} is not a path to a file.");
        }
    }
}

/// <summary>What the gateway file names, its paths resolved and its mapping checked.</summary>
/// <param name="ProvidersPath">
/// The providers file, or <see langword="null"/> when the file names none, which only a file with no point may do.
/// </param>
/// <param name="Results">The point that screens tool results, when the file configures it.</param>
/// <param name="Definitions">The point that screens tool definitions, when the file configures it.</param>
internal sealed record GatewayFileContent(string? ProvidersPath, PointEntry? Results, PointEntry? Definitions);

/// <summary>A point as the gateway file gives it, before its policy file is read.</summary>
/// <param name="Name"><c>results</c> or <c>definitions</c>.</param>
/// <param name="PolicyPath">The policy file, resolved against the gateway file's directory.</param>
/// <param name="Mapping">The action for each verdict.</param>
internal sealed record PointEntry(string Name, string PolicyPath, VerdictMapping Mapping);
