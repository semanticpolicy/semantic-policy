using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.Http;

/// <summary>
/// Reads what came back from a protocol v0 server: a body onto a v0 result when it is one, and a
/// status onto a message that quotes nothing the server wrote. The server's text stays in the body,
/// which the result keeps only in its raw element.
/// </summary>
internal static class HttpProviderResponse
{
    // The protocol's settings, read as the protocol spells them: member names in camel case only and
    // numbers only as JSON numbers. The web defaults behind SemanticPolicyJson.Options would take
    // "Evidence" for evidence, past the checks below, and the string "NaN" for a number.
    private static readonly JsonSerializerOptions _wire = new(SemanticPolicyJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
    };

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
    /// without a kind would otherwise arrive as a probability the server never claimed. Member names
    /// match only in their protocol case, and a number written as a string is not a number. Never
    /// throws.
    /// </summary>
    public static ProviderResult? ReadResult(JsonElement body)
    {
        if (!IsResult(body))
        {
            return null;
        }

        try
        {
            ProviderResult? result = body.Deserialize<ProviderResult>(_wire);

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
    /// What is wrong with the evidence a result would relay, or <see langword="null"/> when nothing
    /// is. Every key must be one of the request's answers: <c>true</c> or <c>false</c>, an option or a
    /// level. A key is text the server wrote, so one outside the request would reach the result's JSON
    /// and its ToString, and a threshold would find nothing under the key it reads. Every value must be
    /// finite: a number too large for a double reads as infinity, and NaN passes no threshold, so it
    /// would read as allowed. A probability must be in [0, 1], as protocol v0 defines it. A kind comes
    /// once, because a threshold reads only the first entry of its kind.
    /// </summary>
    public static string? EvidenceDeviation(DecisionRequest request, IEnumerable<Evidence> evidence)
    {
        HashSet<EvidenceKind> kinds = [];
        foreach (Evidence entry in evidence)
        {
            if (!kinds.Add(entry.Kind))
            {
                return "two evidence entries of one kind";
            }

            foreach ((string key, double value) in entry.Values)
            {
                if (!IsAnswer(request, key))
                {
                    return "evidence keyed outside the request's answers";
                }

                if (!double.IsFinite(value))
                {
                    return "a value that is not a finite number";
                }

                if (entry.Kind == EvidenceKind.Probability && value is < 0 or > 1)
                {
                    return "a probability outside [0, 1]";
                }
            }
        }

        return null;
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

    private static bool IsAnswer(DecisionRequest request, string key) =>
        request.Type switch
        {
            DecisionType.Boolean => key is "true" or "false",
            DecisionType.Choice => request.Options!.ContainsKey(key),
            DecisionType.Score => request.Levels!.Contains(key, StringComparer.Ordinal),
            _ => false,
        };

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
