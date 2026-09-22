using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Metrics;

/// <summary>
/// One bucket of a reliability diagram: what the provider predicted in this range, and how often it was
/// right. A well-calibrated provider's observed frequency tracks its mean prediction down the whole table.
/// </summary>
/// <param name="Lower">The bucket's lower edge, inclusive.</param>
/// <param name="Upper">The bucket's upper edge, exclusive except on the last bucket.</param>
/// <param name="Count">How many rows landed here.</param>
/// <param name="MeanPrediction">The mean predicted probability; <see langword="null"/> for an empty bucket.</param>
/// <param name="ObservedFrequency">The share that turned out positive; <see langword="null"/> when empty.</param>
public sealed record ReliabilityBin(
    double Lower,
    double Upper,
    int Count,
    double? MeanPrediction,
    double? ObservedFrequency);

/// <summary>
/// How far a provider's probabilities are from the frequencies they claim. Calibration is measured here
/// and applied nowhere: nothing in this tool rewrites a provider's numbers. It needs evidence that says it
/// is a probability, so a rule decided on a score or a logit gets a first-class "not applicable" naming
/// what was found instead, rather than a number computed off a scale that does not carry one.
/// </summary>
/// <param name="Applicable">Whether any row carried a probability to measure.</param>
/// <param name="KindsFound">The evidence kinds of the rows that were left out, named for the report.</param>
/// <param name="Rows">How many rows the numbers are over, which is the sample size to read them against.</param>
/// <param name="Ece">
/// The expected calibration error: the count-weighted mean gap between a bucket's mean prediction and its
/// observed frequency.
/// </param>
/// <param name="Brier">The mean squared error of the probability against the outcome.</param>
/// <param name="Bins">The reliability diagram, ten equal-width buckets, empty ones included.</param>
public sealed record Calibration(
    bool Applicable,
    IReadOnlyList<string> KindsFound,
    int Rows,
    double? Ece,
    double? Brier,
    IReadOnlyList<ReliabilityBin> Bins)
{
    private const int _buckets = 10;

    /// <summary>
    /// Measures the classified rows of a selection. A Boolean rule is read on the flagged answer's
    /// probability as the step function completed it; a Choice rule on the top probability of the attempt
    /// that decided, against whether the option it picked is the labelled one. A Score rule is not
    /// calibrated in this release, whatever its provider returned.
    /// </summary>
    /// <param name="rows">The bucketed rows of the selection.</param>
    /// <param name="rule">The rule the rows were decided on.</param>
    public static Calibration Compute(IReadOnlyList<RowOutcome> rows, Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rule);
        List<(double Prediction, double Outcome)> observations = [];
        List<string> kindsFound = [];
        foreach (RowOutcome row in rows)
        {
            if (row.Bucket != RowBucket.Classified)
            {
                continue;
            }

            RuleVerdict verdict = row.Row.Verdict;
            Attempt? deciding = verdict.DecidingBinding is { } index
                ? verdict.Attempts.FirstOrDefault(attempt => attempt.BindingIndex == index)
                : null;
            double? prediction = rule switch
            {
                BooleanRule when verdict.EvidenceKind == EvidenceKind.Probability => verdict.EvidenceValue,
                ChoiceRule => TopProbability(deciding),
                _ => null,
            };

            if (prediction is { } value)
            {
                observations.Add((value, Hit(row, rule) ? 1 : 0));
                continue;
            }

            // Naming the kind is the point of a "not applicable" section: a reader learns what the provider
            // returned in place of a probability, instead of being told a number could not be computed.
            foreach (EvidenceKind kind in KindsOf(rule, verdict, deciding))
            {
                string name = Names.Camel(kind);
                if (!kindsFound.Contains(name, StringComparer.Ordinal))
                {
                    kindsFound.Add(name);
                }
            }
        }

        return observations.Count == 0
            ? new Calibration(Applicable: false, kindsFound, Rows: 0, Ece: null, Brier: null, Bins: [])
            : Measure(observations, kindsFound);
    }

    private static Calibration Measure(List<(double Prediction, double Outcome)> observations, List<string> kinds)
    {
        int[] counts = new int[_buckets];
        double[] predicted = new double[_buckets];
        double[] observed = new double[_buckets];
        double squaredError = 0;
        foreach ((double prediction, double outcome) in observations)
        {
            int bucket = BucketOf(prediction);
            counts[bucket]++;
            predicted[bucket] += prediction;
            observed[bucket] += outcome;
            squaredError += (prediction - outcome) * (prediction - outcome);
        }

        List<ReliabilityBin> bins = new(_buckets);
        double gap = 0;
        for (int bucket = 0; bucket < _buckets; bucket++)
        {
            double lower = (double)bucket / _buckets;
            double upper = (double)(bucket + 1) / _buckets;
            if (counts[bucket] == 0)
            {
                bins.Add(new ReliabilityBin(lower, upper, 0, MeanPrediction: null, ObservedFrequency: null));
                continue;
            }

            double meanPrediction = predicted[bucket] / counts[bucket];
            double frequency = observed[bucket] / counts[bucket];
            bins.Add(new ReliabilityBin(lower, upper, counts[bucket], meanPrediction, frequency));
            gap += counts[bucket] * Math.Abs(meanPrediction - frequency);
        }

        return new Calibration(
            Applicable: true,
            kinds,
            observations.Count,
            gap / observations.Count,
            squaredError / observations.Count,
            bins);
    }

    // The edges are compared, never multiplied out: three tenths times ten is 2.9999999999999996 in binary
    // floating point, which would drop a probability of exactly 0.3 into the bucket below its own.
    private static int BucketOf(double prediction)
    {
        for (int bucket = 0; bucket < _buckets - 1; bucket++)
        {
            if (prediction < (double)(bucket + 1) / _buckets)
            {
                return bucket;
            }
        }

        return _buckets - 1;
    }

    private static bool Hit(RowOutcome row, Rule rule) =>
        rule is BooleanRule boolean
            ? string.Equals(
                row.Row.Row.Label.Answer,
                boolean.FlaggedAnswer ? "true" : "false",
                StringComparison.Ordinal)
            : string.Equals(row.PredictedAnswer, row.Row.Row.Label.Answer, StringComparison.Ordinal);

    private static double? TopProbability(Attempt? deciding)
    {
        Evidence? evidence = deciding?.Result.Evidence?.FirstOrDefault(entry =>
            entry is { Kind: EvidenceKind.Probability } && entry.Values is not null);
        return evidence is null || evidence.Values.Count == 0 ? null : evidence.Values.Values.Max();
    }

    // A Boolean verdict names the one kind its ladder was read on; a Choice or Score rule reads no evidence
    // through its thresholds, so what the deciding attempt carried is the only answer to "found instead".
    private static IEnumerable<EvidenceKind> KindsOf(Rule rule, RuleVerdict verdict, Attempt? deciding)
    {
        if (rule is BooleanRule)
        {
            return verdict.EvidenceKind is { } kind ? [kind] : [];
        }

        return deciding?.Result.Evidence?
            .Where(entry => entry is not null)
            .Select(entry => entry.Kind)
            .Distinct() ?? [];
    }
}
