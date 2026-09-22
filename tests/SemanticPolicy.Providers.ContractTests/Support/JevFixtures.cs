using System.Globalization;
using System.Text.Json;

namespace SemanticPolicy.Providers.ContractTests.Support;

/// <summary>
/// Synthetic Jev bodies, shaped like the vendor's and invented here: nothing in them was ever sent to
/// or received from a live endpoint. Probabilities are two-decimal values that sum to one exactly, so
/// the suite's tolerance is never what makes a fixture pass.
/// </summary>
internal static class JevFixtures
{
    /// <summary>A 200 body answering a Boolean question with the probability of <c>true</c>.</summary>
    public static string BooleanAnswer(double noul, double? confidence = null, Envelope? envelope = null) =>
        Response(Answer("noul", ("noul", noul), ("confidence", confidence)), envelope);

    /// <summary>A 200 body answering a Choice question with the option picked and a distribution.</summary>
    public static string ChoiceAnswer(
        string choice,
        IReadOnlyDictionary<string, double> probabilities,
        double? confidence = null,
        Envelope? envelope = null) =>
        Response(
            Answer("choice", ("choice", choice), ("probabilities", probabilities), ("confidence", confidence)),
            envelope);

    /// <summary>
    /// A 200 body answering a Score question: the legend maps index strings onto the levels in order,
    /// the distribution is keyed by the same indices, and <paramref name="score"/> is the vendor's
    /// expected index.
    /// </summary>
    public static string ScoreAnswer(
        IReadOnlyList<string> legend,
        IReadOnlyList<double> probabilities,
        double? score = null,
        double? confidence = null,
        Envelope? envelope = null) =>
        Response(
            Answer(
                "score",
                ("score", score),
                ("legend", ByIndex(legend)),
                ("probabilities", ByIndex(probabilities)),
                ("confidence", confidence)),
            envelope);

    /// <summary>
    /// A 200 body whose <c>answers.decision</c> is given verbatim, for the bodies outside the contract.
    /// </summary>
    public static string Response(object? answer, Envelope? envelope = null)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        if (envelope?.Model is { } model)
        {
            body["model"] = model;
        }

        if (envelope?.Id is { } id)
        {
            body["id"] = id;
        }

        body["answers"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["decision"] = answer };
        if (envelope?.Usage is true)
        {
            body["usage"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["input_tokens"] = 128,
                ["output_tokens"] = 4,
            };
        }

        if (envelope?.Provider is { } provider)
        {
            body["provider"] = provider;
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>An answer object of the wire type with the fields that are not <see langword="null"/>.</summary>
    public static Dictionary<string, object?> Answer(string type, params (string Name, object? Value)[] fields)
    {
        Dictionary<string, object?> answer = new(StringComparer.Ordinal) { ["type"] = type };
        foreach ((string name, object? value) in fields)
        {
            if (value is not null)
            {
                answer[name] = value;
            }
        }

        return answer;
    }

    /// <summary>An OpenRouter-shaped error body; the message is whatever text the test salts it with.</summary>
    public static string OpenRouterError(int code, string message) =>
        JsonSerializer.Serialize(new { error = new { code, message, metadata = new { } } });

    /// <summary>A TypeSafe-shaped error body with the error type under <c>detail</c>.</summary>
    public static string TypeSafeError(string errorType, string message) =>
        JsonSerializer.Serialize(new { detail = new { error_type = errorType, message } });

    /// <summary>A body that is not JSON at all, as a gateway's error page would be.</summary>
    public static string Html(string text) => $"<html><body><p>{text}</p></body></html>";

    /// <summary>The values keyed by their position as a string: <c>"0"</c>, <c>"1"</c>, and so on.</summary>
    public static Dictionary<string, T> ByIndex<T>(IReadOnlyList<T> values)
    {
        Dictionary<string, T> byIndex = new(StringComparer.Ordinal);
        for (int i = 0; i < values.Count; i++)
        {
            byIndex[i.ToString(CultureInfo.InvariantCulture)] = values[i];
        }

        return byIndex;
    }

    /// <summary>The optional top-level fields of a 200 body.</summary>
    /// <param name="Model">The <c>model</c> the body reports, when it reports one.</param>
    /// <param name="Id">The <c>id</c> a gateway adds, when it adds one.</param>
    /// <param name="Usage">Whether the body carries a <c>usage</c> object.</param>
    /// <param name="Provider">The <c>provider</c> a gateway reports, when it reports one.</param>
    public sealed record Envelope(string? Model = null, string? Id = null, bool Usage = false, string? Provider = null);
}
