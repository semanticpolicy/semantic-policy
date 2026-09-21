namespace SemanticPolicy.Protocol;

/// <summary>
/// Arithmetic over declared evidence, never a reinterpretation of it. What comes out is on the
/// evidence's own scale: a margin says how far apart the top two answers are, not how likely the top
/// one is to be right.
/// </summary>
public static class EvidenceMath
{
    /// <summary>
    /// Completes a Boolean probability a provider reported one-sidedly: <c>probability</c> evidence whose
    /// only key is <c>true</c> or <c>false</c> gains the other as 1 − p. Anything else — another kind,
    /// both keys already present, a key that is not a Boolean answer — comes back as it is, because the
    /// complement is only meaningful for a probability.
    /// </summary>
    /// <param name="evidence">One entry of a result's evidence.</param>
    public static Evidence WithBooleanComplement(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind != EvidenceKind.Probability || evidence.Values.Count != 1)
        {
            return evidence;
        }

        if (evidence.Values.TryGetValue("true", out double pTrue))
        {
            return evidence with { Values = Both(pTrue, 1 - pTrue) };
        }

        if (evidence.Values.TryGetValue("false", out double pFalse))
        {
            return evidence with { Values = Both(1 - pFalse, pFalse) };
        }

        return evidence;
    }

    /// <summary>
    /// The gap between the top value and the runner-up, on the evidence's own scale, after
    /// <see cref="WithBooleanComplement"/>. <see langword="null"/> when fewer than two values remain,
    /// because there is no gap to measure. A small margin means the provider found the answers close,
    /// whatever the top value says.
    /// </summary>
    /// <param name="evidence">One entry of a result's evidence.</param>
    public static double? Margin(Evidence evidence)
    {
        IReadOnlyDictionary<string, double> values = WithBooleanComplement(evidence).Values;
        if (values.Count < 2)
        {
            return null;
        }

        double top = double.NegativeInfinity;
        double runnerUp = double.NegativeInfinity;
        foreach (double value in values.Values)
        {
            if (value > top)
            {
                runnerUp = top;
                top = value;
            }
            else if (value > runnerUp)
            {
                runnerUp = value;
            }
        }

        return top - runnerUp;
    }

    private static Dictionary<string, double> Both(double pTrue, double pFalse) =>
        new(StringComparer.Ordinal) { ["true"] = pTrue, ["false"] = pFalse };
}
