using System.CommandLine;
using SemanticPolicy.Evals.Calibrating;
using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Output;
using SemanticPolicy.Evals.Policies;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Cli;

// `calibrate`: fits a Platt map for one binding of a Boolean rule on the tune rows of a recording and writes a new
// policy that carries it, with every threshold moved so that each recorded row keeps its verdict, and no policy where
// one would not. It calls no provider and never rewrites a file it reads.
internal static class CalibrateVerb
{
    private static readonly Option<string> _outPolicy = new("--out-policy")
    {
        Description = "Write the calibrated policy to this file; it must not be a file calibrate reads.",
        HelpName = "file",
        Required = true,
    };

    private static readonly Option<string?> _diagram = new("--diagram")
    {
        Description = "Write the reliability diagram before and after calibration as SVG to this file.",
        HelpName = "file",
    };

    public static Command Build(CliIo io)
    {
        Command command = new(
            "calibrate",
            "Fit a calibration for one binding on a recording and write it into a new policy.");
        foreach (Option option in SharedOptions.InputOptions)
        {
            command.Options.Add(option);
        }

        command.Options.Add(SharedOptions.Recording);
        command.Options.Add(SharedOptions.Force);
        command.Options.Add(SharedOptions.Out);
        command.Options.Add(SharedOptions.Provider);
        command.Options.Add(_outPolicy);
        command.Options.Add(_diagram);
        command.SetAction(parse => EvalsCli.Guard(io, () => Run(parse, io)));
        return command;
    }

    private static int Run(ParseResult parse, CliIo io)
    {
        string outPolicy = parse.GetValue(_outPolicy)!;
        if (string.IsNullOrWhiteSpace(outPolicy))
        {
            throw new EvalsException("--out-policy names no file; give the file to write the calibrated policy to.");
        }

        CliFiles.RefuseSharedFiles(
            "calibrate",
            [
                ("--policy", parse.GetValue(SharedOptions.Policy)),
                ("--dataset", parse.GetValue(SharedOptions.Dataset)),
                ("--tune", parse.GetValue(SharedOptions.Tune)),
                ("--test", parse.GetValue(SharedOptions.Test)),
                ("--recording", parse.GetValue(SharedOptions.Recording)),
            ],
            [
                ("--out-policy", parse.GetValue(_outPolicy)),
                ("--out", parse.GetValue(SharedOptions.Out)),
                ("--diagram", parse.GetValue(_diagram)),
            ]);
        ReplayedInputs replayed = ReplayedInputs.Load(parse);
        Policy policy = replayed.Inputs.Policy;
        if (replayed.Set.Rule is not BooleanRule rule)
        {
            throw new EvalsException(
                $"Rule '{replayed.Set.Rule.Id}' is a {Names.Camel(replayed.Set.Rule.Type)} rule; calibrate fits a "
                + "map for a Boolean rule only, whose thresholds read one number.");
        }

        int bindingIndex = replayed.BindingIndex(parse.GetValue(SharedOptions.Provider), "calibrate");
        ProviderBinding binding = policy.Bindings[bindingIndex];
        RuleOperatingPoint point = binding.OperatingPoints.First(candidate =>
            string.Equals(candidate.RuleId, rule.Id, StringComparison.Ordinal));
        EvidenceKind source = point.Calibration?.SourceKind ?? point.Thresholds[0].Kind;
        if (source is not (EvidenceKind.Score or EvidenceKind.Logit or EvidenceKind.Probability))
        {
            throw new EvalsException(
                $"The operating point of rule '{rule.Id}' on binding '{binding.ProviderId}' reads {Names.Camel(source)} "
                + "evidence; calibrate maps score, logit or probability evidence only.");
        }

        // The binding alone and no gate: every row reaches the binding, and every attempt that succeeded is read.
        IReadOnlyList<EvaluatedRow> before = replayed.Set.Evaluate(Alone(policy, binding, point));
        IReadOnlyList<FittingRow> fitting = CalibrationFit.Rows(before, replayed.Tune, rule, source);
        int flagged = fitting.Count(row => row.Flagged);
        int other = fitting.Count - flagged;
        if (flagged < CalibrationFit.MinimumRows || other < CalibrationFit.MinimumRows)
        {
            throw new EvalsException(
                $"Binding '{binding.ProviderId}' has {flagged} flagged and {other} other fitting rows on "
                + $"{replayed.Wording.ChosenOn}; calibrate needs at least {CalibrationFit.MinimumRows} of each. A "
                + "fitting row is labelled with an answer, and the binding's recorded attempt on it succeeded.");
        }

        CalibrationTransform transform = CalibrationFit.Transform(
            source,
            fitting,
            point.Thresholds.Select(threshold => CalibrationFit.Raw(point, threshold.AtOrAbove)));
        (double slope, double intercept) = PlattScaling.Fit(
            [.. fitting.Select(row => (EvidenceCalibration.Input(transform, row.Value), row.Flagged))]);
        if (!double.IsFinite(slope) || slope <= 0 || !double.IsFinite(intercept))
        {
            throw new EvalsException(
                $"The fitted slope for binding '{binding.ProviderId}' is {slope}, not a finite number greater than "
                + "zero: on the fitting rows its evidence does not rank flagged rows above the others, and no "
                + "increasing map can turn it into a probability.");
        }

        (string? model, int otherModelRows) = CalibrationFit.Model(fitting);
        EvidenceCalibration calibration = new(
            CalibrationMethod.Platt,
            source,
            transform,
            slope,
            intercept,
            new CalibrationProvenance(
                model,
                "sha256:" + replayed.Inputs.Datasets[0].Sha256,
                replayed.Inputs.Splits.Source == SplitSource.Metadata ? InputSelection.From(parse).SplitNames.Tune : null,
                flagged,
                other));
        RuleOperatingPoint calibrated = CalibrationFit.Calibrate(point, calibration);
        Policy written = Replace(policy, bindingIndex, point, calibrated);
        try
        {
            written.Validate();
        }
        catch (PolicyConfigurationException e)
        {
            throw new EvalsException($"The calibrated policy is not valid, so it is not written: {e.Message}");
        }

        IReadOnlyList<EvaluatedRow> after = replayed.Set.Evaluate(Alone(written, written.Bindings[bindingIndex], calibrated));
        RefuseChangedVerdicts(before, after, binding.ProviderId);
        CalibrateSection section = new(
            binding.ProviderId,
            rule.Id,
            source,
            transform,
            slope,
            intercept,
            flagged,
            other,
            model,
            otherModelRows,
            replayed.Wording,
            Measure(before, replayed.Test, rule),
            Measure(after, replayed.Test, rule));

        // Everything is computed before anything is written, and the policy goes last, so a run that fails leaves no
        // new policy behind.
        if (parse.GetValue(_diagram) is { } diagramPath)
        {
            Diagram(diagramPath, section, io);
        }

        EvalsResult result = replayed.Result("calibrate", calibrate: section);
        if (parse.GetValue(SharedOptions.Out) is { } outPath)
        {
            ResultWriter.Write(outPath, result);
        }

        PolicyFile.Write(outPolicy, written);
        CalibrateRenderer.Write(io.Output, result, section, outPolicy);
        return ExitCodes.Success;
    }

    // The operating point as it stood, or before it is calibrated, read by a chain of its binding alone with the gate
    // taken off; the policy's other rules and bindings do not reach the replay.
    private static Policy Alone(Policy policy, ProviderBinding binding, RuleOperatingPoint point) =>
        policy with
        {
            Bindings =
            [
                binding with
                {
                    OperatingPoints =
                    [
                        .. binding.OperatingPoints.Select(candidate =>
                            ReferenceEquals(candidate, point) ? point with { Gate = null } : candidate),
                    ],
                },
            ],
        };

    // The input policy whole, with only the calibrated operating point changed.
    private static Policy Replace(Policy policy, int bindingIndex, RuleOperatingPoint point, RuleOperatingPoint calibrated) =>
        policy with
        {
            Bindings =
            [
                .. policy.Bindings.Select((binding, index) => index != bindingIndex
                    ? binding
                    : binding with
                    {
                        OperatingPoints =
                        [
                            .. binding.OperatingPoints.Select(candidate =>
                                ReferenceEquals(candidate, point) ? calibrated : candidate),
                        ],
                    }),
            ],
        };

    // Moving each threshold through the map keeps every verdict only where the map tells the values on either side of
    // a threshold apart. Under log-odds it cannot near 0 and 1: the clamp gives every value there one input, so a
    // threshold at 1.0, or one a re-fit takes back above the old map's top, gets the probability of rows below it. No
    // map separates such rows, so the replays at the binding alone are compared row by row and the fit is refused.
    private static void RefuseChangedVerdicts(
        IReadOnlyList<EvaluatedRow> before,
        IReadOnlyList<EvaluatedRow> after,
        string providerId)
    {
        const int shown = 5;
        string[] changed =
        [
            .. before.Zip(after)
                .Where(pair => pair.First.Verdict.Verdict != pair.Second.Verdict.Verdict)
                .Select(pair => $"'{pair.First.Row.Id}' {Names.Camel(pair.First.Verdict.Verdict)} → "
                    + Names.Camel(pair.Second.Verdict.Verdict)),
        ];
        if (changed.Length == 0)
        {
            return;
        }

        string rows = string.Join(", ", changed.Take(shown))
            + (changed.Length > shown ? $" and {changed.Length - shown} more" : string.Empty);
        throw new EvalsException(
            $"The calibrated policy changes the verdict of binding '{providerId}' on "
            + $"{(changed.Length == 1 ? "1 recorded row" : $"{changed.Length} recorded rows")} ({rows}), so it is not "
            + "written: a threshold sits where the map gives rows on both sides of it one probability, within the "
            + "log-odds clamp near 0 or 1, or above the top of the calibration being replaced. Move that threshold to "
            + "a value the provider's evidence reaches, and calibrate again.");
    }

    private static Calibration Measure(IReadOnlyList<EvaluatedRow> replayed, IReadOnlyList<DatasetRow> test, Rule rule)
    {
        HashSet<string> testIds = new(test.Select(row => row.Id), StringComparer.Ordinal);
        RowOutcome[] outcomes =
        [
            .. replayed.Where(row => testIds.Contains(row.Row.Id)).Select(row => RowCounting.Classify(row, rule)),
        ];
        return Calibration.Compute(outcomes, rule);
    }

    // Before the map, a point that read no probability has nothing to draw; the map is drawn alone and standard error
    // says why, in the words of the report's section.
    private static void Diagram(string path, CalibrateSection section, CliIo io)
    {
        if (!section.After.Applicable)
        {
            io.Error.WriteLine($"diagram not written: {ReportRenderer.NotApplicableReason(section.After, DecisionType.Boolean)}");
            return;
        }

        if (section.Before.Applicable)
        {
            ReliabilityDiagram.Write(path, section.Before, section.After);
            return;
        }

        ReliabilityDiagram.Write(path, section.After);
        io.Error.WriteLine($"before not drawn: {ReportRenderer.NotApplicableReason(section.Before, DecisionType.Boolean)}");
    }

}
