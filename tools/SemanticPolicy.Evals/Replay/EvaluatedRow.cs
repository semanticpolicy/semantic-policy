using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evaluation;

namespace SemanticPolicy.Evals.Replay;

/// <summary>
/// A row and the verdict the rule reached on it. The verdict comes from the step function, trace and all,
/// so everything a metric needs to know — which binding decided, on what evidence, and whether a failure or
/// an exhausted gate ended the chain — is read from it rather than recomputed.
/// </summary>
/// <param name="Row">The dataset row, with the label the verdict is scored against.</param>
/// <param name="Verdict">The rule's verdict, as <see cref="PolicyEvaluation"/> produced it.</param>
public sealed record EvaluatedRow(DatasetRow Row, RuleVerdict Verdict);
