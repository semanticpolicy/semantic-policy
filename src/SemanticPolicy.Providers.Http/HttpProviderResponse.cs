using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.Http;

/// <summary>
/// Reads what came back from a protocol v0 server: a body onto a v0 result when it is one, and a
/// status onto a message that quotes nothing the server wrote. The server's text stays in the body,
/// which the result keeps only in its raw element.
/// </summary>
internal static class HttpProviderResponse
{
    /// <summary>
    /// The kind a v0 failure body names, or <see langword="null"/> when the body is not one, in which
    /// case the status decides. Only a whole v0 result counts, so a proxy's or a framework's own error
    /// object cannot steer the kind.
    /// </summary>
    public static FailureKind? FailureKindOf(JsonElement? body) =>
        body is { } json && ReadResult(json) is { Outcome: { Status: OutcomeStatus.Failure, Kind: { } kind } }
            ? kind
            : null;

    /// <summary>A failure kind as the wire spells it, such as <c>rejectedInput</c>.</summary>
    public static string WireName(FailureKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    /// <summary>
    /// The body as a v0 result, or <see langword="null"/> when it is not one. Every member the protocol
    /// requires is checked on the JSON before the record is trusted: the record fills its protocol from
    /// an initializer and an absent enum member with the enum's first value, so an evidence entry
    /// without a kind would otherwise arrive as a probability the server never claimed. Never throws.
    /// </summary>
    public static ProviderResult? ReadResult(JsonElement body)
    {
        if (!IsResult(body))
        {
            return null;
        }

        try
        {
            ProviderResult? result = body.Deserialize<ProviderResult>(SemanticPolicyJson.Options);

            // Absent evidence is no evidence, as an empty array is.
            return result is null ? null : result with { Evidence = result.Evidence ?? [] };
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            // A string an enum does not name, such as an unknown failure kind, or a member of the wrong
            // shape: not a v0 result, whatever else the body carries.
            return null;
        }
    }

    /// <summary>
    /// The status, what the client made of the body when there is something to say, and the body's
    /// length: <c>HTTP 503, kind unavailable, body 187 bytes</c>. Nothing the server wrote appears.
    /// </summary>
    public static string Describe(HttpStatusCode status, int length, string? reading = null)
    {
        StringBuilder text = new StringBuilder("HTTP ").Append((int)status);
        if (reading is not null)
        {
            text.Append(", ").Append(reading);
        }

        return text.Append(", body ").Append(length.ToString(CultureInfo.InvariantCulture)).Append(" bytes").ToString();
    }

    private static bool IsResult(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("protocol", out JsonElement protocol)
        && protocol.ValueKind == JsonValueKind.String
        && protocol.ValueEquals(ProtocolVersion.V0)
        && Has(body, "type")
        && body.TryGetProperty("outcome", out JsonElement outcome)
        && outcome.ValueKind == JsonValueKind.Object
        && Has(outcome, "status")
        && body.TryGetProperty("provider", out JsonElement provider)
        && provider.ValueKind == JsonValueKind.Object
        && EvidenceEntriesAreWhole(body);

    private static bool EvidenceEntriesAreWhole(JsonElement body)
    {
        if (!body.TryGetProperty("evidence", out JsonElement evidence) || evidence.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (evidence.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement entry in evidence.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !Has(entry, "kind") || !Has(entry, "values"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Has(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement member) && member.ValueKind != JsonValueKind.Null;
}
