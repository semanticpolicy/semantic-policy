using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.TypeSafe;

/// <summary>
/// Reads what came back from a Jev call: a status onto a failure kind, an error body onto a message
/// that quotes no vendor text, and a 200 body onto the protocol's value, evidence and metadata. A
/// 200 body is read strictly, because a distribution with a key missing would let a threshold read a
/// partial answer as a whole one.
/// </summary>
internal static class JevResponse
{
    /// <summary>The scale the vendor claims for its probabilities, reported as its claim and nothing more.</summary>
    private const string _calibratedScale = "calibrated";

    /// <summary>
    /// Every status other than 200 as a failure kind. 400 and 422 are the provider's verdict on the
    /// request, so they are <see cref="FailureKind.RejectedInput"/>; a 2xx that is not 200 carries no
    /// answer the adapter can read, so it is <see cref="FailureKind.Malformed"/>.
    /// </summary>
    public static FailureKind KindOf(HttpStatusCode status) =>
        (int)status switch
        {
            401 or 403 => FailureKind.Unauthorized,
            400 or 404 or 413 or 422 => FailureKind.RejectedInput,
            408 or 429 => FailureKind.Unavailable,
            >= 500 and <= 599 => FailureKind.Unavailable,
            >= 200 and <= 299 => FailureKind.Malformed,
            _ => FailureKind.Unknown,
        };

    /// <summary>The body parsed and detached from its document, or <see langword="null"/> when it is not JSON.</summary>
    public static JsonElement? TryParse(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The status, the vendor's <c>error.code</c> or <c>error_type</c> when the body carries one, and
    /// the body's length: <c>HTTP 400, code 400, body 87 bytes</c>. A vendor's free-text message quotes
    /// the request, so it never appears; the parsed body goes to the result's raw element instead.
    /// </summary>
    public static string DescribeError(HttpStatusCode status, JsonElement? body, int length)
    {
        StringBuilder text = new StringBuilder("HTTP ").Append((int)status);
        if (body is { ValueKind: JsonValueKind.Object } json)
        {
            if (json.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement code)
                && code.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                text.Append(", code ").Append(code.ToString());
            }
            else if (ErrorType(json) is { } errorType)
            {
                text.Append(", error_type ").Append(errorType);
            }
        }

        return text.Append(", body ").Append(length).Append(" bytes").ToString();
    }

    /// <summary>The <c>model</c> the body reports, or <see langword="null"/> when it reports none.</summary>
    public static string? Model(JsonElement body) => StringProperty(body, "model");

    /// <summary>The <c>id</c> the body reports, or <see langword="null"/> when it reports none.</summary>
    public static string? RequestId(JsonElement body) => StringProperty(body, "id");

    /// <summary>The body's <c>usage</c> element as the vendor shaped it, or <see langword="null"/>.</summary>
    public static JsonElement? Usage(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("usage", out JsonElement usage) ? usage : null;

    /// <summary>A 200 body onto the answer the request asked for, or the shape of what stopped it.</summary>
    public static Reading Read(DecisionRequest request, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("answers", out JsonElement answers)
            || answers.ValueKind != JsonValueKind.Object)
        {
            return Reading.Malformed("the response has no `answers`");
        }

        if (!answers.TryGetProperty(JevRequest.QuestionKey, out JsonElement answer)
            || answer.ValueKind != JsonValueKind.Object)
        {
            return Reading.Malformed("the response has no `answers.decision`");
        }

        if (StringProperty(answer, "type") != JevRequest.WireType(request.Type))
        {
            return Reading.Malformed("the answer is of another type");
        }

        Reading reading = request.Type switch
        {
            DecisionType.Boolean => ReadBoolean(answer),
            DecisionType.Choice => ReadChoice(request, answer),
            DecisionType.Score => ReadScore(request, answer),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return reading.Failure is null ? reading with { Extra = Extra(body, answer, request.Type) } : reading;
    }

    // `noul` is P(true) alone; the runtime completes the pair when it needs both sides. The half cut
    // is inclusive and is the adapter's reading of the model's estimate, not a verdict: 0.5 says the
    // model found the two answers equally likely, and the policy's threshold, not this line, decides
    // what that means.
    private static Reading ReadBoolean(JsonElement answer)
    {
        if (!answer.TryGetProperty("noul", out JsonElement noul))
        {
            return Reading.Malformed("the answer has no `noul`");
        }

        if (!Probability(noul, out double probability))
        {
            return Reading.Malformed("`noul` is not a number in [0, 1]");
        }

        Dictionary<string, double> values = new(StringComparer.Ordinal) { ["true"] = probability };
        return Reading.Success(new BooleanValue(probability >= 0.5), Calibrated(values));
    }

    // The value is the provider's `choice` as it gave it, never the argmax of the distribution: the
    // provider's pick is its answer, and the distribution is the evidence behind it.
    private static Reading ReadChoice(DecisionRequest request, JsonElement answer)
    {
        IReadOnlyDictionary<string, string> options = request.Options!;
        if (StringProperty(answer, "choice") is not { } choice)
        {
            return Reading.Malformed("the answer has no `choice`");
        }

        if (!options.ContainsKey(choice))
        {
            return Reading.Malformed("`choice` is not one of the request's options");
        }

        if (!answer.TryGetProperty("probabilities", out JsonElement probabilities)
            || probabilities.ValueKind != JsonValueKind.Object)
        {
            return Reading.Malformed("the answer has no `probabilities`");
        }

        Dictionary<string, double> values = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in probabilities.EnumerateObject())
        {
            if (!options.ContainsKey(entry.Name))
            {
                return Reading.Malformed("`probabilities` has a key that is not one of the request's options");
            }

            if (!Probability(entry.Value, out double probability))
            {
                return Reading.Malformed("a probability is not a number in [0, 1]");
            }

            if (!values.TryAdd(entry.Name, probability))
            {
                return Reading.Malformed("`probabilities` repeats a key");
            }
        }

        if (values.Count != options.Count)
        {
            return Reading.Malformed("`probabilities` does not cover every option of the request");
        }

        return Reading.Success(new ChoiceValue(choice), Calibrated(values));
    }

    // The vendor answers a Score with a distribution keyed by index and a legend from index to level;
    // the legend must be the request's levels in order, or the indices would name something else.
    // The value is the most probable level, a tie going to the lowest index; the vendor's scalar
    // `score` is an expected index and goes to Extra, never to the value or the evidence.
    private static Reading ReadScore(DecisionRequest request, JsonElement answer)
    {
        IReadOnlyList<string> levels = request.Levels!;
        if (!answer.TryGetProperty("legend", out JsonElement legend) || legend.ValueKind != JsonValueKind.Object)
        {
            return Reading.Malformed("the answer has no `legend`");
        }

        if (!LegendMatches(legend, levels))
        {
            return Reading.Malformed("`legend` does not map indices onto the request's levels in order");
        }

        if (!answer.TryGetProperty("probabilities", out JsonElement probabilities)
            || probabilities.ValueKind != JsonValueKind.Object)
        {
            return Reading.Malformed("the answer has no `probabilities`");
        }

        double[] byIndex = new double[levels.Count];
        bool[] seen = new bool[levels.Count];
        int count = 0;
        foreach (JsonProperty entry in probabilities.EnumerateObject())
        {
            if (!Index(entry.Name, levels.Count, out int index) || seen[index])
            {
                return Reading.Malformed("`probabilities` is not keyed by exactly the legend's indices");
            }

            if (!Probability(entry.Value, out double probability))
            {
                return Reading.Malformed("a probability is not a number in [0, 1]");
            }

            byIndex[index] = probability;
            seen[index] = true;
            count++;
        }

        if (count != levels.Count)
        {
            return Reading.Malformed("`probabilities` does not cover every level of the request");
        }

        int best = 0;
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        for (int i = 0; i < levels.Count; i++)
        {
            values[levels[i]] = byIndex[i];
            if (byIndex[i] > byIndex[best])
            {
                best = i;
            }
        }

        return Reading.Success(new ScoreValue(levels[best], best), Calibrated(values));
    }

    // Only the fields the answer carried, or null when it carried none: `confidence` is a projection
    // of the distribution and `score` an expected index, so both are provider-shaped extras and never
    // evidence; `provider` is what a gateway reports about who served the call.
    private static JsonElement? Extra(JsonElement body, JsonElement answer, DecisionType type)
    {
        Dictionary<string, JsonElement> extra = new(StringComparer.Ordinal);
        if (answer.TryGetProperty("confidence", out JsonElement confidence))
        {
            extra["confidence"] = confidence;
        }

        if (type == DecisionType.Score && answer.TryGetProperty("score", out JsonElement expectedIndex))
        {
            extra["expectedIndex"] = expectedIndex;
        }

        if (body.TryGetProperty("provider", out JsonElement provider))
        {
            extra["provider"] = provider;
        }

        return extra.Count == 0 ? null : JsonSerializer.SerializeToElement(extra, SemanticPolicyJson.Options);
    }

    private static bool LegendMatches(JsonElement legend, IReadOnlyList<string> levels)
    {
        bool[] seen = new bool[levels.Count];
        int count = 0;
        foreach (JsonProperty entry in legend.EnumerateObject())
        {
            if (!Index(entry.Name, levels.Count, out int index)
                || seen[index]
                || entry.Value.ValueKind != JsonValueKind.String
                || !string.Equals(entry.Value.GetString(), levels[index], StringComparison.Ordinal))
            {
                return false;
            }

            seen[index] = true;
            count++;
        }

        return count == levels.Count;
    }

    private static bool Index(string name, int count, out int index) =>
        int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out index)
        && index < count
        && string.Equals(name, index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool Probability(JsonElement element, out double probability)
    {
        probability = 0;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out probability)
            && probability is >= 0 and <= 1;
    }

    private static Evidence Calibrated(Dictionary<string, double> values) =>
        new(EvidenceKind.Probability, values, _calibratedScale);

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
        && property.GetString() is { } value
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? ErrorType(JsonElement body)
    {
        if (StringProperty(body, "error_type") is { } top)
        {
            return top;
        }

        return body.TryGetProperty("detail", out JsonElement detail) ? StringProperty(detail, "error_type") : null;
    }

    /// <summary>
    /// What a 200 body normalised to: the value, its evidence and the provider-shaped extras, or the
    /// shape of the deviation that stopped the reading. The failure text describes the shape and
    /// never quotes the body.
    /// </summary>
    public readonly record struct Reading(DecisionValue? Value, Evidence? Evidence, JsonElement? Extra, string? Failure)
    {
        public static Reading Success(DecisionValue value, Evidence evidence) =>
            new(value, evidence, Extra: null, Failure: null);

        public static Reading Malformed(string shape) => new(Value: null, Evidence: null, Extra: null, shape);
    }
}
