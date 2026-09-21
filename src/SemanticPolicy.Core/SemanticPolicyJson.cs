using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticPolicy;

/// <summary>
/// The serializer settings that produce the protocol's JSON: camel-case names, enums as lower-camel
/// strings, absent fields omitted.
/// </summary>
public static class SemanticPolicyJson
{
    /// <summary>
    /// The settings, read-only. Pass them to <see cref="JsonSerializer"/> for anything that goes on the
    /// wire or into a log.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerOptions.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // The wire carries enum names, never their numbers, so an integer is refused on the way in.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));

        // A policy a person wrote by hand rarely puts a rule's "type" first; without this the discriminator
        // would have to be the first property of its object.
        options.AllowOutOfOrderMetadataProperties = true;
        options.MakeReadOnly();
        return options;
    }
}
