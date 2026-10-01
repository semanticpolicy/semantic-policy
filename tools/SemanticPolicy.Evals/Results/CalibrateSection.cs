using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Results;

/// <summary>
/// One binding's operating point, calibrated: the map fitted on the tune rows, what it was fitted on, and the
/// reliability of the point's probabilities on the test rows before and after it. The fitted map is the one the
/// written policy carries, in the library's own convention.
/// </summary>
/// <param name="Provider">The calibrated binding, by provider name.</param>
/// <param name="Rule">The calibrated rule.</param>
/// <param name="SourceKind">The evidence kind the map reads.</param>
/// <param name="Transform">What the source value goes through before the linear map.</param>
/// <param name="Slope">The fitted slope; always finite and greater than zero.</param>
/// <param name="Intercept">The fitted intercept.</param>
/// <param name="FlaggedRows">The fitting rows labelled with the flagged answer.</param>
/// <param name="OtherRows">The fitting rows labelled with the other answer.</param>
/// <param name="Model">
/// The model most fitting rows name, or <see langword="null"/> when none names one.
/// </param>
/// <param name="OtherModelRows">The fitting rows that name a model other than <paramref name="Model"/>.</param>
/// <param name="Split">Which rows the map was fitted on and which the reliability is reported on.</param>
/// <param name="Before">
/// The reliability of the operating point as it stood, on the test rows; not applicable when it read no probability.
/// </param>
/// <param name="After">The reliability of the calibrated operating point on the same rows.</param>
public sealed record CalibrateSection(
    string Provider,
    string Rule,
    EvidenceKind SourceKind,
    CalibrationTransform Transform,
    double Slope,
    double Intercept,
    int FlaggedRows,
    int OtherRows,
    string? Model,
    int OtherModelRows,
    SplitWording Split,
    Calibration Before,
    Calibration After);
