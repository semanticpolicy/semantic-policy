using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Results;

/// <summary>
/// Every selected binding of the policy, each swept and measured on its own. The bindings are compared as
/// alternatives, not as the chain the policy runs: each entry comes from a policy holding that binding alone, so
/// no number of one provider's depends on whether another answered first.
/// </summary>
/// <param name="Split">Which rows the operating points were chosen on and which they are reported on.</param>
/// <param name="Bindings">One entry per selected binding, in the policy's order.</param>
public sealed record CompareSection(SplitWording Split, IReadOnlyList<CompareEntry> Bindings)
{
    /// <summary>Whether every binding met every constraint.</summary>
    public bool Feasible => Bindings.All(binding => binding.Sweep.Feasible);
}

/// <summary>One binding, alone: its sweep, and the report numbers on the test rows at the point it was given.</summary>
/// <param name="Sweep">The binding's curves, recommendations and test-row rates, as <c>sweep</c> reports them.</param>
/// <param name="Discrimination">
/// How well each rung's evidence separates the labels on the test rows; <see langword="null"/> for a rule with no
/// ladder.
/// </param>
/// <param name="Outcomes">
/// What became of the test rows at the recommended gate, or at the policy file's gate when no gate could be
/// recommended.
/// </param>
/// <param name="Provider">What the binding's provider cost on the test rows.</param>
public sealed record CompareEntry(
    SweepSection Sweep,
    IReadOnlyList<RungDiscrimination>? Discrimination,
    OutcomeCounts Outcomes,
    ProviderStats Provider);
