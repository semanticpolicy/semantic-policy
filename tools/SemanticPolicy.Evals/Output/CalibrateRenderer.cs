using System.Globalization;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Output;

// The text `calibrate` prints: the fitted map with every number in full, what it was fitted on, and the reliability
// before and after it on the rows it is reported on.
internal static class CalibrateRenderer
{
    public static void Write(TextWriter writer, EvalsResult result, CalibrateSection section, string policyPath)
    {
        writer.WriteLine($"calibrate of rule '{section.Rule}' on binding '{section.Provider}', policy '{result.PolicyId}'");
        SweepRenderer.WriteRows(writer, result.Rows);
        writer.WriteLine(
            $"fit: Platt scaling of {Names.Camel(section.SourceKind)} evidence, transform {Names.Camel(section.Transform)}");
        writer.WriteLine($"x: {Input(section)}");
        writer.WriteLine(
            $"slope {Number(section.Slope)}, intercept {Number(section.Intercept)}: "
            + "p = 1 / (1 + exp(-(slope * x + intercept)))");
        writer.WriteLine(
            $"fitting rows: {Count(section.FlaggedRows)} flagged, {Count(section.OtherRows)} other, on {section.Split.ChosenOn}");
        writer.WriteLine(Model(section));
        writer.WriteLine($"split: {section.Split.Sentence}");
        writer.WriteLine();
        writer.WriteLine(
            $"reliability on {section.Split.ReportedOn}, at binding '{section.Provider}' alone with no gate");
        ReportRenderer.CalibrationSection("before calibration", section.Before, DecisionType.Boolean, writer);
        ReportRenderer.CalibrationSection("after calibration", section.After, DecisionType.Boolean, writer);
        writer.WriteLine();
        writer.WriteLine("The calibrated probability is an estimate fitted on labelled data; it can be wrong on inputs unlike them.");
        writer.WriteLine("The map keeps the binding's order of rows, so it flags the same rows and tells them apart no better.");
        writer.WriteLine($"policy written to {policyPath}");
    }

    private static string Input(CalibrateSection section) =>
        section.Transform == CalibrationTransform.LogOdds
            ? $"the log-odds of the flagged answer's {Names.Camel(section.SourceKind)}, clamped to "
              + $"[{Epsilon(EvidenceCalibration.Epsilon)}, {Epsilon(1 - EvidenceCalibration.Epsilon)}]"
            : $"the flagged answer's {Names.Camel(section.SourceKind)} as it is";

    private static string Model(CalibrateSection section)
    {
        if (section.Model is null)
        {
            return "model: none named by the fitting rows";
        }

        string others = section.OtherModelRows == 1
            ? "1 fitting row names another model"
            : $"{Count(section.OtherModelRows)} fitting rows name another model";
        return $"model: {section.Model}; {others}";
    }

    // Every digit the policy file carries, so the printed map is the written one.
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Epsilon(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
