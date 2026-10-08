using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SemanticPolicy.Providers.Http;
using SemanticPolicy.Providers.SystemOne;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Providers;

/// <summary>
/// Reads a providers file. Each entry maps a registration name to an adapter's kind and to that adapter's own options
/// class, and is registered the way an application registers the adapter.
/// </summary>
/// <remarks>
/// Compiled into each tool that reads the file as a linked file rather than shipped as a public type, so the tools
/// accept the same files without an API of their own to version. A tool wraps the refusal in its own exception and
/// exit code.
/// </remarks>
internal static class ProvidersFileReader
{
    private const string _providers = "providers";
    private const string _kind = "kind";
    private const string _options = "options";
    private const string _keyVariable = "apiKeyVariable";

    // Listed in a refusal in this order, so kept alphabetical.
    private static readonly Dictionary<string, Type> _kinds = new(StringComparer.Ordinal)
    {
        ["http"] = typeof(HttpProviderOptions),
        ["systemone"] = typeof(SystemOneOptions),
        ["typesafe-jev"] = typeof(TypeSafeJevOptions),
    };

    private static readonly JsonSerializerOptions _json = CreateJsonOptions();

    /// <summary>
    /// Reads and checks the whole file, and returns the hook that registers each of its entries with the key
    /// variables they name. Nothing is registered and no provider is built here; the hook registers every entry, bound
    /// or not.
    /// </summary>
    /// <param name="path">The providers file.</param>
    /// <param name="timeout">
    /// The timer the tool sets on every entry, or <see langword="null"/> to leave each adapter's default.
    /// </param>
    /// <param name="timeoutRefusal">
    /// Why the file may not set <c>timeout</c> itself, in the tool's words; it ends the refusal's message.
    /// </param>
    /// <exception cref="ProvidersFileException">
    /// The file cannot be read, is not JSON, has a shape or an option the tool does not know, sets what the tool owns,
    /// gives a name twice, puts in a key variable something that is not a variable's name, or has a Jev route that
    /// names no key variable. The message names the file, the entry and the property, never a value. The hook throws
    /// it too, with the adapter's own message, when an adapter refuses its options.
    /// </exception>
    public static ProviderRegistrations Read(string path, TimeSpan? timeout, string timeoutRefusal)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutRefusal);
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ProvidersFileException($"Providers file '{path}' cannot be read: {e.Message}");
        }

        // A byte-order mark is skipped, as the dataset and policy readers skip it; the parser refuses one in bytes.
        ReadOnlyMemory<byte> json = content.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? content.AsMemory(Encoding.UTF8.Preamble.Length)
            : content;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            // Never the exception's message: it can quote the text around the error, and the text can hold a key.
            throw new ProvidersFileException($"Providers file '{path}' is not valid JSON at {Position(e)}.");
        }

        // The settings the tool sets on every entry itself, in any casing, since the options bind names
        // case-insensitively. They are refused on the raw document: each is a member of the options class, so the
        // serializer would bind it.
        Dictionary<string, string> owned = new(StringComparer.OrdinalIgnoreCase)
        {
            ["apiKey"] = "a key is never written in the file; name the environment variable that holds it in apiKeyVariable",
            ["timeout"] = timeoutRefusal,
            ["id"] = "the registration name is the provider's id",
        };

        using (document)
        {
            List<(string Name, Action<ISemanticPolicyBuilder> Register)> entries = [];
            List<string> keyVariables = [];
            foreach (JsonProperty entry in ProvidersObject(path, document.RootElement).EnumerateObject())
            {
                if (entries.Exists(read => read.Name == entry.Name))
                {
                    throw new ProvidersFileException($"Providers file '{path}': provider '{entry.Name}' is given twice.");
                }

                object bound = ReadEntry(new Entry(path, entry.Name), entry.Value, owned);
                entries.Add((entry.Name, Registration(new Entry(path, entry.Name), bound, timeout)));
                keyVariables.AddRange(KeyVariables(bound).Where(variable => !keyVariables.Contains(variable, StringComparer.Ordinal)));
            }

            return new ProviderRegistrations(
                builder =>
                {
                    foreach ((string name, Action<ISemanticPolicyBuilder> register) in entries)
                    {
                        try
                        {
                            register(builder);
                        }
                        catch (ArgumentException failure)
                        {
                            // The adapter checks its own settings at the call and names the one it refused, not its value.
                            throw new ProvidersFileException($"Providers file '{path}': provider '{name}': {failure.Message}");
                        }
                    }
                },
                keyVariables);
        }
    }

    private static JsonElement ProvidersObject(string path, JsonElement root)
    {
        JsonElement? providers = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name != _providers)
                {
                    throw new ProvidersFileException(
                        $"Providers file '{path}': top-level property '{property.Name}' is not known; the file holds '{_providers}' alone.");
                }

                providers = providers is null
                    ? property.Value
                    : throw new ProvidersFileException($"Providers file '{path}': '{_providers}' is given twice.");
            }
        }

        return providers is { ValueKind: JsonValueKind.Object } found
            ? found
            : throw new ProvidersFileException($"Providers file '{path}' has no top-level '{_providers}' object.");
    }

    // Returns the entry's options, bound to the class its kind names.
    private static object ReadEntry(Entry where, JsonElement entry, IReadOnlyDictionary<string, string> owned)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw where.Refuse($"the entry is not an object with '{_kind}' and '{_options}'.");
        }

        JsonElement? kind = null;
        JsonElement? options = null;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case _kind when kind is null:
                    kind = property.Value;
                    break;
                case _options when options is null:
                    options = property.Value;
                    break;
                case _kind or _options:
                    throw where.Refuse($"'{property.Name}' is given twice.");
                default:
                    throw where.Refuse($"'{property.Name}' is not an entry property; an entry has '{_kind}' and '{_options}'.");
            }
        }

        string known = string.Join(", ", _kinds.Keys);
        if (kind is null)
        {
            throw where.Refuse($"the entry has no '{_kind}'; known kinds: {known}.");
        }

        if (kind.Value.ValueKind != JsonValueKind.String)
        {
            throw where.Refuse($"'{_kind}' is not a string; known kinds: {known}.");
        }

        string kindName = kind.Value.GetString()!;
        if (!_kinds.TryGetValue(kindName, out Type? optionsType))
        {
            throw where.Refuse($"'{_kind}' names a kind that is not known; known kinds: {known}.");
        }

        if (options is not { ValueKind: JsonValueKind.Object } optionsObject)
        {
            throw where.Refuse($"the entry has no '{_options}' object.");
        }

        Check(where, optionsObject, _json.GetTypeInfo(optionsType), _options, owned);
        try
        {
            return optionsObject.Deserialize(optionsType, _json)!;
        }
        catch (JsonException e)
        {
            // Unknown and repeated names were refused above, so what is left is a value its property cannot take.
            throw where.Refuse($"{_options}{e.Path?.TrimStart('$')} holds a value its property cannot take.");
        }
    }

    // The tool owns the id of every entry, which is the registration's name and what a verdict or a recording calls
    // the provider. It owns the timer as well when it gives one; without one, the adapter's default stands.
    private static Action<ISemanticPolicyBuilder> Registration(Entry where, object bound, TimeSpan? timeout)
    {
        string name = where.Name;
        switch (bound)
        {
            // A key is optional here, as it is for System One: an unset variable sends no Authorization header.
            case HttpProviderOptions http:
                return builder => builder.AddHttpProvider(name, configured =>
                {
                    CopySettable(http, configured);
                    configured.Timeout = timeout ?? configured.Timeout;
                    configured.Id = name;
                });
            case SystemOneOptions systemOne:
                return builder => builder.AddSystemOne(name, configured =>
                {
                    CopySettable(systemOne, configured);
                    configured.Timeout = timeout ?? configured.Timeout;
                    configured.Id = name;
                });
            case TypeSafeJevOptions jev:
                // The adapter reads the variable only when a bound provider is built, and a missing name then fails
                // with no provider named. A variable is the only way the file names a key, so the tool asks for one.
                if (jev.Route is not null && (jev.ApiKeyVariable ?? jev.Route.ApiKeyVariable) is null)
                {
                    throw where.Refuse(
                        $"{_options}.route.apiKeyVariable is missing; name the environment variable that holds the key there or in {_options}.apiKeyVariable.");
                }

                return builder => builder.AddTypeSafeJev(name, configured =>
                {
                    CopySettable(jev, configured);
                    configured.Timeout = timeout ?? configured.Timeout;
                    configured.Id = name;
                });
            default:
                throw new InvalidOperationException($"No registration for {bound.GetType().Name}.");
        }
    }

    // Every variable the entry names as holding a key, the route's beside the entry's own: a variable the adapter
    // would not read while the other is set still names a key.
    private static IEnumerable<string> KeyVariables(object bound)
    {
        string?[] named = bound switch
        {
            HttpProviderOptions http => [http.ApiKeyVariable],
            SystemOneOptions systemOne => [systemOne.ApiKeyVariable],
            TypeSafeJevOptions jev => [jev.ApiKeyVariable, jev.Route?.ApiKeyVariable],
            _ => [],
        };
        return named.OfType<string>().Where(variable => variable.Length > 0);
    }

    // Walks the object as its class declares it, into every nested object the class declares too, such as a Jev
    // route. A name is matched as the serializer matches it, ignoring case, so a name given twice in two casings is
    // caught as well.
    private static void Check(
        Entry where,
        JsonElement value,
        JsonTypeInfo type,
        string path,
        IReadOnlyDictionary<string, string> owned)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            string at = $"{path}.{property.Name}";
            if (owned.TryGetValue(property.Name, out string? reason))
            {
                throw where.Refuse($"{at} is refused: {reason}.");
            }

            JsonPropertyInfo member = type.Properties.FirstOrDefault(
                    candidate => string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw where.Refuse($"{at} is not a property of {type.Type.Name}.");
            if (!seen.Add(member.Name))
            {
                throw where.Refuse($"{at} is given twice.");
            }

            // A key pasted where its variable's name belongs would reach the Jev adapter, whose message for an unset
            // variable names the variable. Refused here in any kind, so the file holds names alone.
            if (string.Equals(property.Name, _keyVariable, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && !IsVariableName(property.Value.GetString()!))
            {
                throw where.Refuse(
                    $"{at} is not the name of an environment variable; name the variable that holds the key, never the key.");
            }

            if (property.Value.ValueKind == JsonValueKind.Object
                && _json.GetTypeInfo(member.PropertyType) is { Kind: JsonTypeInfoKind.Object } nested)
            {
                Check(where, property.Value, nested, at, owned);
            }
        }
    }

    // Every public settable property rather than a list kept here, so an option an adapter adds later reaches it
    // from the file without a change to the tool.
    private static void CopySettable<T>(T source, T target)
    {
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(target, property.GetValue(source));
            }
        }
    }

    // The portable shape every shell can export. A key with a dash, a dot or a slash in it fails it; one made of
    // letters, digits and underscores alone passes, because nothing tells it apart from a name.
    private static bool IsVariableName(string value) =>
        value.Length > 0
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string Position(JsonException e)
    {
        string position = string.Create(
            CultureInfo.InvariantCulture,
            $"line {e.LineNumber + 1 ?? 1}, byte {e.BytePositionInLine + 1 ?? 1}");
        return e.Path is null or "$" ? position : $"{position}, in {e.Path}";
    }

    // A copy, because the shared settings are read-only and belong to every other reader in the process. The walk
    // refuses unknown and repeated names in every object it reaches; the serializer refuses them anywhere else, such
    // as in an object inside a list an adapter might add.
    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(SemanticPolicyJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
        };
        options.MakeReadOnly();
        return options;
    }

    // Every message about an entry starts the same way. It never quotes a value: the file is where a key could have
    // been pasted, and these messages land in terminals and CI logs.
    private readonly record struct Entry(string File, string Name)
    {
        public ProvidersFileException Refuse(string detail) => new($"Providers file '{File}': provider '{Name}': {detail}");
    }
}

/// <summary>What <see cref="ProvidersFileReader.Read"/> found in a providers file.</summary>
/// <param name="Register">Registers every entry of the file on the builder it is given, bound or not.</param>
/// <param name="KeyVariables">
/// Every environment variable an entry names as holding its key, each once, in the order the file names them.
/// </param>
internal sealed record ProviderRegistrations(Action<ISemanticPolicyBuilder> Register, IReadOnlyList<string> KeyVariables);

/// <summary>
/// A providers file the reader refuses, or an entry its adapter refuses. The message names the file, the entry and the
/// property, never a value from the file.
/// </summary>
/// <param name="message">What is wrong, without any value from the file.</param>
internal sealed class ProvidersFileException(string message) : Exception(message);
