using System.CommandLine;
using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Output;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Evals.Sweeping;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// <c>compare</c>: every binding of the policy as an alternative to the others. Each is swept on a policy that holds
/// it alone, under the same constraints, and reported on the test rows at the point it was given; any binding whose
/// constraints cannot be met ends the run with <see cref="ExitCodes.InfeasibleConstraint"/> after everything has been
/// printed and written.
/// </summary>
public static class CompareVerb
{
    private static readonly Option<string[]> _providers = new("--provider")
    {
        Description = "Compare only these bindings, by provider name; repeatable. Every binding when omitted.",
        HelpName = "name",
        AllowMultipleArgumentsPerToken = true,
    };

    /// <summary>Builds the verb.</summary>
    /// <param name="io">Where it writes.</param>
    public static Command Build(CliIo io)
    {
        ArgumentNullException.ThrowIfNull(io);
        Command command = new(
            "compare",
            "Sweep every binding of the policy on its own, under the same constraints, and compare them on the test rows.");
        foreach (Option option in SharedOptions.InputOptions)
        {
            command.Options.Add(option);
        }

        command.Options.Add(SharedOptions.Recording);
        command.Options.Add(SharedOptions.Force);
        command.Options.Add(SharedOptions.Out);
        command.Options.Add(_providers);
        foreach (Option option in SharedOptions.ConstraintOptions)
        {
            command.Options.Add(option);
        }

        command.SetAction(parse => EvalsCli.Guard(io, () => Run(parse, io)));
        return command;
    }

    private static int Run(ParseResult parse, CliIo io)
    {
        IReadOnlyList<RungConstraint> rungConstraints = ReplayedInputs.RungConstraints(parse);
        IReadOnlyList<GateConstraint> gateConstraints = ReplayedInputs.GateConstraints(parse);
        ReplayedInputs replayed = ReplayedInputs.Load(parse);
        HashSet<int> named = [.. (parse.GetValue(_providers) ?? []).Select(replayed.IndexOf)];
        IReadOnlyList<ProviderBinding> bindings = replayed.Inputs.Policy.Bindings;
        List<CompareEntry> entries = [];
        for (int index = 0; index < bindings.Count; index++)
        {
            if (named.Count == 0 || named.Contains(index))
            {
                entries.Add(Measure(replayed, bindings[index], rungConstraints, gateConstraints));
            }
        }

        CompareSection section = new(replayed.Wording, entries);
        EvalsResult result = replayed.Result("compare", compare: section);
        if (parse.GetValue(SharedOptions.Out) is { } path)
        {
            ResultWriter.Write(path, result);
        }

        SweepRenderer.WriteCompare(io.Output, result, section);
        return section.Feasible ? ExitCodes.Success : ExitCodes.InfeasibleConstraint;
    }

    private static CompareEntry Measure(
        ReplayedInputs replayed,
        ProviderBinding binding,
        IReadOnlyList<RungConstraint> rungConstraints,
        IReadOnlyList<GateConstraint> gateConstraints)
    {
        // Alone, the binding answers every row it has an answer for: under the whole chain a later binding sees
        // only what the ones before it passed on, and its numbers would be about that remainder.
        Policy alone = replayed.Inputs.Policy with { Bindings = [binding] };
        ReplaySet set = replayed.Set;
        SweepSection sweep = OperatingPointSweep.Run(
            set,
            alone,
            0,
            replayed.Inputs.Splits,
            replayed.Wording,
            rungConstraints,
            gateConstraints);

        IReadOnlyList<RungDiscrimination>? discrimination = set.Rule is BooleanRule
            ?
            [
                .. ThresholdCurve.Compute(set, alone, 0, replayed.Test)
                    .Select(curve => new RungDiscrimination(curve.Rung, Discrimination.FromCurve(curve))),
            ]
            : null;

        RuleOperatingPoint? point = binding.OperatingPoints.FirstOrDefault(candidate =>
            string.Equals(candidate.RuleId, set.Rule.Id, StringComparison.Ordinal));
        double? gate = sweep.Gate?.Recommendation is { Feasible: true } recommended ? recommended.Below : point?.Gate?.Below;
        OutcomeCounts outcomes = GateSweep.OutcomesAt(set, alone, 0, replayed.Test, gate);

        HashSet<string> testIds = new(replayed.Test.Select(row => row.Id), StringComparer.Ordinal);
        IEnumerable<(string Provider, ProviderResult Result)> attempts = set.Rows
            .Where(row => testIds.Contains(row.Row.Id))
            .Select(row => row.AttemptsByProvider.TryGetValue(binding.ProviderId, out ProviderResult? result) ? result : null)
            .OfType<ProviderResult>()
            .Select(result => (binding.ProviderId, result));
        ProviderStats stats = ProviderStats.Compute(attempts).FirstOrDefault()
            ?? new ProviderStats(binding.ProviderId, null, 0, null, null, new Dictionary<string, double>(StringComparer.Ordinal));
        return new CompareEntry(sweep, discrimination, outcomes, stats);
    }
}
