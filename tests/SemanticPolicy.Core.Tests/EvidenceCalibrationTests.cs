using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests;

public sealed class EvidenceCalibrationTests
{
    private const double _tolerance = 1e-12;

    // Every expected value is a closed form: σ(ln k) = k / (1 + k), so σ(2 · ln 3) = 0.9, σ(ln 3) = 0.75
    // and σ(ln 4) = 0.8.
    public static TheoryData<CalibrationTransform, double, double, double, double> Maps => new()
    {
        { CalibrationTransform.LogOdds, 1, 0, 0.2, 0.2 },
        { CalibrationTransform.LogOdds, 1, 0, 0.9, 0.9 },
        { CalibrationTransform.LogOdds, 2, 0, 0.75, 0.9 },
        { CalibrationTransform.Identity, 1, 0, 0, 0.5 },
        { CalibrationTransform.Identity, Math.Log(3), 0, 1, 0.75 },
        { CalibrationTransform.Identity, 1, Math.Log(4), 0, 0.8 },
    };

    [Theory]
    [MemberData(nameof(Maps))]
    public void Calibration_Maps_A_Value_Through_The_Sigmoid_Of_Its_Transformed_Input(
        CalibrationTransform transform,
        double slope,
        double intercept,
        double value,
        double expected)
    {
        EvidenceCalibration calibration = Platt(transform, slope, intercept);

        calibration.Apply(value).Should().BeApproximately(expected, _tolerance);
    }

    // 1 − ε has no exact binary form, so the upper bound's log-odds is taken of the double that holds it:
    // its distance from 1 is ε only to about ten digits.
    public static TheoryData<CalibrationTransform, double, double> Inputs => new()
    {
        { CalibrationTransform.LogOdds, 0, LogOdds(1e-6) },
        { CalibrationTransform.LogOdds, -3, LogOdds(1e-6) },
        { CalibrationTransform.LogOdds, 1, LogOdds(1 - 1e-6) },
        { CalibrationTransform.LogOdds, 7, LogOdds(1 - 1e-6) },
        { CalibrationTransform.Identity, 7, 7 },
        { CalibrationTransform.Identity, -3, -3 },
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Log_Odds_Input_Reads_A_Value_Beyond_The_Clamp_At_Its_Bound(
        CalibrationTransform transform,
        double value,
        double expected)
    {
        EvidenceCalibration.Epsilon.Should().Be(1e-6);

        EvidenceCalibration.Input(transform, value).Should().BeApproximately(expected, _tolerance);
    }

    public static TheoryData<EvidenceCalibration, double, double> Inversions => new()
    {
        { Platt(CalibrationTransform.LogOdds, 2, 0), 0.9, 0.75 },
        { Platt(CalibrationTransform.Identity, 1, Math.Log(4)), 0.8, 0 },
        { Platt(CalibrationTransform.LogOdds, 1.7, -0.4), Platt(CalibrationTransform.LogOdds, 1.7, -0.4).Apply(0.3), 0.3 },
    };

    [Theory]
    [MemberData(nameof(Inversions))]
    public void Inverting_A_Calibrated_Probability_Returns_The_Value(
        EvidenceCalibration calibration,
        double probability,
        double expected)
    {
        calibration.Invert(probability).Should().BeApproximately(expected, _tolerance);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Inverting_A_Number_Outside_Zero_To_One_Throws(double probability)
    {
        EvidenceCalibration calibration = Platt(CalibrationTransform.LogOdds, 2, 0);

        Action invert = () => calibration.Invert(probability);

        invert.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static double LogOdds(double value) => Math.Log(value / (1 - value));

    private static EvidenceCalibration Platt(CalibrationTransform transform, double slope, double intercept) =>
        new(CalibrationMethod.Platt, EvidenceKind.Score, transform, slope, intercept);
}
