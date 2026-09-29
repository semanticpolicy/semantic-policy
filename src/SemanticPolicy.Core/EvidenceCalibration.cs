using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// A calibration of one Boolean rule's evidence on one provider: it maps the flagged answer's value of
/// <see cref="SourceKind"/> to an estimated probability, p = σ(<see cref="Slope"/> · x + <see cref="Intercept"/>),
/// where σ is the logistic function and x is <see cref="Input"/> of the value under <see cref="Transform"/>.
/// The thresholds of a calibrated operating point read that probability instead of the provider's number.
/// It is an estimate fitted on labelled data, and like the evidence it comes from it can be wrong in either
/// direction, most of all on inputs unlike that data. It belongs to the provider, the model and the rule it
/// was fitted on, and does not carry over to another without a new fit.
/// </summary>
/// <remarks>
/// Platt (1999) and scikit-learn write the same map as P = 1 / (1 + exp(A · f + B)), with A and B fitted
/// on the raw value f. Such a pair is <see cref="CalibrationTransform.Identity"/> with
/// <see cref="Slope"/> = −A and <see cref="Intercept"/> = −B: both signs flip. A pair copied as it is
/// fails validation by its slope's sign alone, because an increasing map has A below zero; a pair whose
/// slope alone was flipped validates, with the intercept's sign wrong.
/// </remarks>
/// <param name="Method">How the map was fitted.</param>
/// <param name="SourceKind">
/// The evidence the map reads: <see cref="EvidenceKind.Score"/>, <see cref="EvidenceKind.Logit"/> or
/// <see cref="EvidenceKind.Probability"/>. The provider must produce it, and the operating point's gate reads
/// its margin as the provider returned it.
/// </param>
/// <param name="Transform">How the value becomes the map's input x.</param>
/// <param name="Slope">The factor on x: a finite number above zero, so a higher value never means a lower probability.</param>
/// <param name="Intercept">The term added to it: a finite number.</param>
/// <param name="Provenance">What the map was fitted on, or <see langword="null"/> when that is not recorded.</param>
public sealed record EvidenceCalibration(
    [property: JsonRequired] CalibrationMethod Method,
    [property: JsonRequired] EvidenceKind SourceKind,
    [property: JsonRequired] CalibrationTransform Transform,
    [property: JsonRequired] double Slope,
    [property: JsonRequired] double Intercept,
    CalibrationProvenance? Provenance = null)
{
    /// <summary>
    /// The bound <see cref="CalibrationTransform.LogOdds"/> holds a value to before taking its log-odds:
    /// [ε, 1 − ε], so a provider's rounded 0 or 1 reads as a finite x of about ±13.8. It is part of the
    /// transform's definition, and every calibration fitted under it depends on it.
    /// </summary>
    public const double Epsilon = 1e-6;

    /// <summary>
    /// The map's input for a value: under <see cref="CalibrationTransform.LogOdds"/>, ln(v / (1 − v)) of the
    /// value held to [<see cref="Epsilon"/>, 1 − <see cref="Epsilon"/>]; under
    /// <see cref="CalibrationTransform.Identity"/>, the value itself. A fitter computes x for every labelled
    /// row with this, so the fit and the runtime read a value alike.
    /// </summary>
    /// <param name="transform">How the value becomes x.</param>
    /// <param name="value">The flagged answer's value of the source kind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transform"/> is not a defined transform.</exception>
    public static double Input(CalibrationTransform transform, double value) =>
        transform switch
        {
            CalibrationTransform.LogOdds => Logit(Math.Clamp(value, Epsilon, 1 - Epsilon)),
            CalibrationTransform.Identity => value,
            _ => throw new ArgumentOutOfRangeException(nameof(transform), transform, "The transform is not defined."),
        };

    /// <summary>
    /// The estimated probability for a value: σ(<see cref="Slope"/> · <see cref="Input"/>(<see cref="Transform"/>,
    /// value) + <see cref="Intercept"/>).
    /// </summary>
    /// <param name="value">The flagged answer's value of the source kind.</param>
    public double Apply(double value) => Sigmoid((Slope * Input(Transform, value)) + Intercept);

    /// <summary>
    /// The value <see cref="Apply"/> maps to a probability: σ((logit p − <see cref="Intercept"/>) / <see cref="Slope"/>)
    /// under <see cref="CalibrationTransform.LogOdds"/>, (logit p − <see cref="Intercept"/>) / <see cref="Slope"/>
    /// under <see cref="CalibrationTransform.Identity"/>. It turns a threshold on the probability back into one on
    /// the value. Under <see cref="CalibrationTransform.LogOdds"/> the map is flat beyond the clamp, so the answer is
    /// exact only for a probability <see cref="Apply"/> reaches from a value inside
    /// [<see cref="Epsilon"/>, 1 − <see cref="Epsilon"/>].
    /// </summary>
    /// <param name="probability">A probability, in [0, 1].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="probability"/> is NaN or outside [0, 1].</exception>
    public double Invert(double probability)
    {
        if (probability is not (>= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(probability), probability, "A probability must be in [0, 1].");
        }

        double x = (Logit(probability) - Intercept) / Slope;
        return Transform == CalibrationTransform.LogOdds ? Sigmoid(x) : x;
    }

    private static double Logit(double p) => Math.Log(p / (1 - p));

    private static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));
}
