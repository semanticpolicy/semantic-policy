namespace SemanticPolicy;

/// <summary>
/// What an <see cref="EvidenceCalibration"/> was fitted on. The runtime reads only the model: when a result
/// names another one, the attempt is marked and evaluation goes on. The other members are carried for
/// whoever audits or refits the calibration.
/// </summary>
/// <param name="Model">
/// The model the provider reported while the calibration was fitted, or <see langword="null"/> when none was
/// recorded; with none, no result is compared against it.
/// </param>
/// <param name="DatasetDigest">A digest of the labelled dataset, or <see langword="null"/>.</param>
/// <param name="Split">The part of the dataset the fit used, or <see langword="null"/>.</param>
/// <param name="FlaggedRows">How many rows of the fit carried the flagged label, or <see langword="null"/>.</param>
/// <param name="OtherRows">How many rows of the fit carried the other label, or <see langword="null"/>.</param>
public sealed record CalibrationProvenance(
    string? Model = null,
    string? DatasetDigest = null,
    string? Split = null,
    int? FlaggedRows = null,
    int? OtherRows = null);
