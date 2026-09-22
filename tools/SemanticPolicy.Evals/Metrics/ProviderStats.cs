using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Metrics;

/// <summary>
/// What one provider cost to ask, over every attempt made of it. Usage is summed field by field under the
/// provider's own names and is never interpreted: no policy reads it, the fields differ per provider, and
/// a tool that renamed "cost" into a currency would be inventing a number.
/// </summary>
/// <param name="Provider">The registration name the attempts were keyed by.</param>
/// <param name="Model">What the provider reported it ran, the first time it reported anything.</param>
/// <param name="Attempts">How many calls were made, successes and failures alike.</param>
/// <param name="LatencyP50Ms">The median call duration by nearest rank.</param>
/// <param name="LatencyP95Ms">The 95th percentile call duration by nearest rank.</param>
/// <param name="Usage">Every top-level numeric usage field, summed under its own name.</param>
public sealed record ProviderStats(
    string Provider,
    string? Model,
    int Attempts,
    double? LatencyP50Ms,
    double? LatencyP95Ms,
    IReadOnlyDictionary<string, double> Usage)
{
    /// <summary>
    /// One entry per provider, in the order the providers were first seen. Latency is measured over every
    /// attempt, including the failed ones: a provider that times out spent that time.
    /// </summary>
    /// <param name="attempts">Every attempt of the run, each with the name its provider is registered under.</param>
    public static IReadOnlyList<ProviderStats> Compute(IEnumerable<(string Provider, ProviderResult Result)> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        List<string> order = [];
        Dictionary<string, Tally> tallies = new(StringComparer.Ordinal);
        foreach ((string provider, ProviderResult result) in attempts)
        {
            if (!tallies.TryGetValue(provider, out Tally? tally))
            {
                tally = new Tally();
                tallies[provider] = tally;
                order.Add(provider);
            }

            tally.Add(result);
        }

        List<ProviderStats> stats = new(order.Count);
        foreach (string provider in order)
        {
            stats.Add(tallies[provider].ToStats(provider));
        }

        return stats;
    }

    private sealed class Tally
    {
        private readonly List<double> _latencies = [];
        private readonly Dictionary<string, double> _usage = new(StringComparer.Ordinal);
        private string? _model;

        internal void Add(ProviderResult result)
        {
            _latencies.Add(result.Provider.LatencyMs);
            _model ??= result.Provider.Model;
            if (result.Provider.Usage is not { ValueKind: JsonValueKind.Object } usage)
            {
                return;
            }

            foreach (JsonProperty property in usage.EnumerateObject())
            {
                // A nested object, a string or a token that does not fit a double is data this tool has no
                // way to add up, so it is left where the provider put it.
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double value))
                {
                    _usage[property.Name] = _usage.GetValueOrDefault(property.Name) + value;
                }
            }
        }

        internal ProviderStats ToStats(string provider)
        {
            _latencies.Sort();
            Dictionary<string, double> usage = new(_usage.Count, StringComparer.Ordinal);
            foreach (string field in _usage.Keys.Order(StringComparer.Ordinal))
            {
                usage[field] = _usage[field];
            }

            return new ProviderStats(
                provider,
                _model,
                _latencies.Count,
                Percentile(_latencies, 0.50),
                Percentile(_latencies, 0.95),
                usage);
        }

        // Nearest rank: the value at position ceil(p·n) of the sorted sample, which is a duration the run
        // actually observed rather than a point interpolated between two of them.
        private static double? Percentile(List<double> sorted, double percentile) =>
            sorted.Count == 0
                ? null
                : sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count), 1, sorted.Count) - 1];
    }
}
