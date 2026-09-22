using SemanticPolicy.Evaluation;

namespace SemanticPolicy.AgentFramework;

/// <summary>
/// One policy at one intervention point, in two halves: <see cref="EvaluateAsync"/> builds the
/// context for a subject and asks the evaluator, and the guard half then hands the verdict to the
/// application's handler and returns what it decided. The split is the seam a frontend that only
/// annotates — one that never applies an outcome — builds on. The guard applies no outcome, reads no
/// verdict and reports no telemetry of its own: the evaluator's activity is the one trace, tied to
/// the subject through the context's correlation id.
/// </summary>
/// <typeparam name="TSubject">What the point judges.</typeparam>
/// <typeparam name="TOutcome">What the handler decides.</typeparam>
internal sealed class PolicyGuard<TSubject, TOutcome>
{
    private readonly GuardSubject<TSubject> _subject;
    private readonly IPolicyEvaluator _evaluator;
    private readonly Policy? _policy;
    private readonly string? _policyId;
    private readonly Func<TSubject, PolicyVerdict, CancellationToken, ValueTask<TOutcome>> _handler;
    private readonly Func<TSubject, SemanticContext> _buildContext;

    /// <summary>A guard over a policy passed as a value.</summary>
    /// <param name="subject">How the guard reads its subject.</param>
    /// <param name="evaluator">The runtime that evaluates the policy.</param>
    /// <param name="policy">The policy; validated by the evaluator on the first evaluation.</param>
    /// <param name="handler">The application's decision, given the subject and the verdict.</param>
    /// <param name="buildContext">
    /// The application's own context for a subject, in place of the layer's default; a context it
    /// returns without a correlation id gets the subject's.
    /// </param>
    public PolicyGuard(
        GuardSubject<TSubject> subject,
        IPolicyEvaluator evaluator,
        Policy policy,
        Func<TSubject, PolicyVerdict, CancellationToken, ValueTask<TOutcome>> handler,
        Func<TSubject, SemanticContext>? buildContext = null)
        : this(subject, evaluator, handler, buildContext)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>A guard over a policy registered with the evaluator under the id.</summary>
    /// <param name="subject">How the guard reads its subject.</param>
    /// <param name="evaluator">The runtime that evaluates the policy.</param>
    /// <param name="policyId">The id the policy was registered under; non-blank.</param>
    /// <param name="handler">The application's decision, given the subject and the verdict.</param>
    /// <param name="buildContext">
    /// The application's own context for a subject, in place of the layer's default; a context it
    /// returns without a correlation id gets the subject's.
    /// </param>
    public PolicyGuard(
        GuardSubject<TSubject> subject,
        IPolicyEvaluator evaluator,
        string policyId,
        Func<TSubject, PolicyVerdict, CancellationToken, ValueTask<TOutcome>> handler,
        Func<TSubject, SemanticContext>? buildContext = null)
        : this(subject, evaluator, handler, buildContext)
    {
        _policyId = string.IsNullOrWhiteSpace(policyId)
            ? throw new ArgumentException("A guard needs a policy id.", nameof(policyId))
            : policyId;
    }

    private PolicyGuard(
        GuardSubject<TSubject> subject,
        IPolicyEvaluator evaluator,
        Func<TSubject, PolicyVerdict, CancellationToken, ValueTask<TOutcome>> handler,
        Func<TSubject, SemanticContext>? buildContext)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(handler);
        _subject = subject;
        _evaluator = evaluator;
        _handler = handler;
        _buildContext = buildContext ?? subject.DefaultContext;
    }

    /// <summary>Where in the loop the guard sits.</summary>
    public InterventionPoint Point => _subject.Point;

    /// <summary>
    /// The evaluate half: builds the subject's context and asks the evaluator. The handler is not
    /// involved. The caller's token goes to the evaluator as it is, so cancelling it ends the
    /// evaluation with an <see cref="OperationCanceledException"/> and no verdict.
    /// </summary>
    /// <param name="subject">What the policy is asked about.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>What the policy concluded, with every attempt behind it.</returns>
    /// <exception cref="InvalidOperationException">The application's context delegate returned nothing.</exception>
    public Task<PolicyVerdict> EvaluateAsync(TSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        SemanticContext context = _buildContext(subject)
            ?? throw new InvalidOperationException("The context delegate returned no context.");

        // The id is what ties the evaluator's activity to this subject; an application that builds
        // its own context rarely sets one, and one it did set is its own to keep.
        if (context.CorrelationId is null)
        {
            context = context with { CorrelationId = _subject.CorrelationId(subject) };
        }

        return _policy is not null
            ? _evaluator.EvaluateAsync(_policy, context, cancellationToken)
            : _evaluator.EvaluateAsync(_policyId!, context, cancellationToken);
    }

    /// <summary>
    /// The guard half: evaluates, then hands the subject and the verdict to the handler and returns
    /// both the verdict and what the handler decided. The outcome is returned as decided, in every
    /// mode; applying it is the frontend's. A cancelled token ends the evaluation before the handler
    /// runs, and a verdict the evaluator could not reach — a configuration error, a cancellation —
    /// surfaces as its exception, untouched.
    /// </summary>
    /// <param name="subject">What the policy is asked about.</param>
    /// <param name="cancellationToken">The caller's token, passed to the evaluator and the handler alike.</param>
    /// <returns>What the policy concluded and what the application decided on it.</returns>
    public async Task<(PolicyVerdict Verdict, TOutcome Outcome)> GuardAsync(TSubject subject, CancellationToken cancellationToken)
    {
        PolicyVerdict verdict = await EvaluateAsync(subject, cancellationToken).ConfigureAwait(false);
        TOutcome outcome = await _handler(subject, verdict, cancellationToken).ConfigureAwait(false);
        return (verdict, outcome);
    }
}
