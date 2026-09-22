namespace SemanticPolicy.Telemetry;

/// <summary>
/// The names the runtime reports under: one activity source and one meter, an activity per evaluation
/// with a child per provider attempt, three instruments, and the tag on each. Every value a tag
/// carries is an identifier, a number or a name from the policy's own vocabulary — a verdict, a mode,
/// a decision type, an evidence kind, an option key — and never the question, a context part, a
/// provider's raw response or its message. A verdict reported here is a semantic signal, not an
/// authorization: what the application did with it is not recorded, because the runtime does not know.
/// </summary>
public static class SemanticPolicyTelemetry
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> every evaluation's activities come from.</summary>
    public const string ActivitySourceName = "SemanticPolicy";

    /// <summary>The <see cref="System.Diagnostics.Metrics.Meter"/> every instrument belongs to.</summary>
    public const string MeterName = "SemanticPolicy";

    /// <summary>One evaluation of one policy: the parent of every attempt made for it.</summary>
    public const string EvaluateActivity = "semanticpolicy.evaluate";

    /// <summary>One provider call for one rule: a child of the evaluation, spanning the call.</summary>
    public const string AttemptActivity = "semanticpolicy.attempt";

    /// <summary>
    /// Counts evaluations that reached a verdict, by policy id, mode and evaluated verdict. An evaluation
    /// the caller cancelled or a provider broke by throwing is not counted, because it concluded nothing.
    /// </summary>
    public const string EvaluationsInstrument = "semanticpolicy.evaluations";

    /// <summary>Counts provider attempts, by provider id, decision type, outcome status and failure kind.</summary>
    public const string AttemptsInstrument = "semanticpolicy.attempts";

    /// <summary>The duration of an evaluation, in seconds, by policy id and mode.</summary>
    public const string EvaluationDurationInstrument = "semanticpolicy.evaluation.duration";

    /// <summary>The policy's id.</summary>
    public const string PolicyIdTag = "semanticpolicy.policy.id";

    /// <summary>The policy's mode: <c>shadow</c> or <c>enforce</c>.</summary>
    public const string PolicyModeTag = "semanticpolicy.policy.mode";

    /// <summary>The verdict the application acts on — always <c>allow</c> in Shadow.</summary>
    public const string EffectiveVerdictTag = "semanticpolicy.verdict.effective";

    /// <summary>The verdict the policy concluded, in either mode.</summary>
    public const string EvaluatedVerdictTag = "semanticpolicy.verdict.evaluated";

    /// <summary>
    /// The correlation id the application put on the context, when it did. It is the only way a verdict
    /// is tied back to what it judged; the runtime never derives one from the content.
    /// </summary>
    public const string CorrelationIdTag = "semanticpolicy.correlation_id";

    /// <summary>The rule an attempt answers.</summary>
    public const string RuleIdTag = "semanticpolicy.rule.id";

    /// <summary>The provider an attempt was made on, by the name it is registered under.</summary>
    public const string ProviderIdTag = "semanticpolicy.provider.id";

    /// <summary>
    /// The model the provider reported for an attempt. Absent on a result the runtime synthesized without
    /// a call, such as a budget expiry.
    /// </summary>
    public const string ProviderModelTag = "semanticpolicy.provider.model";

    /// <summary>The decision type of an attempt: <c>boolean</c>, <c>choice</c> or <c>score</c>.</summary>
    public const string DecisionTypeTag = "semanticpolicy.decision.type";

    /// <summary>
    /// What became of an attempt: <c>success</c>, <c>abstain</c> or <c>failure</c>, as evaluation acted on
    /// it — a success that broke the contract reports as a failure.
    /// </summary>
    public const string OutcomeStatusTag = "semanticpolicy.outcome.status";

    /// <summary>
    /// Why an attempt failed, on a failure: <c>timeout</c>, <c>unavailable</c>, <c>malformed</c>, …
    /// </summary>
    public const string OutcomeFailureKindTag = "semanticpolicy.outcome.failure_kind";

    /// <summary>
    /// The kind of evidence a decided Boolean rule was read on: <c>probability</c>, <c>score</c>, …
    /// </summary>
    public const string EvidenceKindTag = "semanticpolicy.evidence.kind";

    /// <summary>The flagged answer's evidence of that kind, the number the ladder was read against.</summary>
    public const string EvidenceValueTag = "semanticpolicy.evidence.value";

    /// <summary>The gap between the top answer and the runner-up, on an attempt a margin gate applied to.</summary>
    public const string MarginTag = "semanticpolicy.margin";

    /// <summary>
    /// The ladder or Score rung the deciding attempt crossed, when one was: <c>warn</c>, <c>deny</c>, …
    /// </summary>
    public const string ThresholdCrossedTag = "semanticpolicy.threshold.crossed";

    /// <summary>
    /// Why the chain moved past an attempt to the next binding: <c>gate</c> or <c>failure</c>. Absent on an
    /// attempt that decided, ended the rule or was the last binding.
    /// </summary>
    public const string ChainMovedByTag = "semanticpolicy.chain.moved_by";

    /// <summary>The provider id of the binding the chain moved on to, beside <see cref="ChainMovedByTag"/>.</summary>
    public const string FallbackToTag = "semanticpolicy.fallback.to";
}
