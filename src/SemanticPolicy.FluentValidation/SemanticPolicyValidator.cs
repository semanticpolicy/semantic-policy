using FluentValidation;
using FluentValidation.Validators;
using SemanticPolicy.Evaluation;

namespace SemanticPolicy.FluentValidation;

// The component behind .Semantic(...). FluentValidation builds the failure from it, so the message,
// the severity, the state and the error code are each one the user's .With...() calls can replace,
// while IsValidAsync alone decides whether a failure exists.
internal sealed class SemanticPolicyValidator<T, TProperty> : AsyncPropertyValidator<T, TProperty>
{
    // One validator instance serves every call, often several at once, so a call's verdict and the
    // severity it maps to cannot sit on a field. FluentValidation builds the failure on the same context
    // straight after IsValidAsync returns false, calling the severity and state providers there, and the
    // root context's data carries the pair across that gap. The rules of one validation run one after
    // another, so a later rule's write never lands before an earlier rule's failure is built.
    private const string _flaggedKey = "SemanticPolicy.FluentValidation.Flagged";

    private readonly IPolicyEvaluator _evaluator;
    private readonly string _policyId;
    private readonly Policy? _policy;
    private readonly Func<T, TProperty, SemanticContext?> _context;
    private readonly Func<PolicyVerdict, Severity?> _severity;

    public SemanticPolicyValidator(
        IPolicyEvaluator evaluator,
        string policyId,
        Func<T, TProperty, SemanticContext?> context,
        Func<PolicyVerdict, Severity?>? severity)
        : this(evaluator, policyId, policy: null, context, severity)
    {
    }

    public SemanticPolicyValidator(
        IPolicyEvaluator evaluator,
        Policy policy,
        Func<T, TProperty, SemanticContext?> context,
        Func<PolicyVerdict, Severity?>? severity)
        : this(evaluator, policy.Id, policy, context, severity)
    {
    }

    private SemanticPolicyValidator(
        IPolicyEvaluator evaluator,
        string policyId,
        Policy? policy,
        Func<T, TProperty, SemanticContext?> context,
        Func<PolicyVerdict, Severity?>? severity)
    {
        _evaluator = evaluator;
        _policyId = policyId;
        _policy = policy;
        _context = context;
        _severity = severity ?? DefaultSeverity;
    }

    // The error code FluentValidation gives a failure by default; stable, because users match on it.
    public override string Name => "SemanticPolicyValidator";

    public IRuleBuilderOptions<T, TProperty> AddTo(IRuleBuilder<T, TProperty> ruleBuilder) =>
        ruleBuilder.SetAsyncValidator(this)
            .WithSeverity((_, _, context) => Flagged.On(context).Severity)
            .WithState((_, _, context) => Flagged.On(context).Verdict);

    // A null context means the value holds nothing to ask about, and the rule passes without asking.
    public override async Task<bool> IsValidAsync(ValidationContext<T> context, TProperty value, CancellationToken cancellation)
    {
        if (_context(context.InstanceToValidate, value) is not { } semanticContext)
        {
            return true;
        }

        PolicyVerdict verdict = await (_policy is null
                ? _evaluator.EvaluateAsync(_policyId, semanticContext, cancellation)
                : _evaluator.EvaluateAsync(_policy, semanticContext, cancellation))
            .ConfigureAwait(false);
        if (_severity(verdict) is not { } severity)
        {
            return true;
        }

        context.RootContextData[_flaggedKey] = new Flagged(verdict, severity);

        // {Verdict} names what the policy reached, not what it applied: in Shadow the effective verdict is
        // always Allow, and a failure exists there only because a severity delegate read the evaluated one.
        context.MessageFormatter
            .AppendArgument("PolicyId", _policyId)
            .AppendArgument("Verdict", verdict.Evaluated.ToString());
        return false;
    }

    protected override string GetDefaultMessageTemplate(string errorCode) =>
        "'{PropertyName}' was flagged by semantic policy '{PolicyId}'.";

    // Effective, so a Shadow policy never fails the validation unless a severity delegate says otherwise.
    private static Severity? DefaultSeverity(PolicyVerdict verdict) => verdict.Effective switch
    {
        Verdict.Deny => Severity.Error,
        Verdict.Escalate => Severity.Warning,
        _ => null,
    };

    private sealed record Flagged(PolicyVerdict Verdict, Severity Severity)
    {
        public static Flagged On(ValidationContext<T> context) => (Flagged)context.RootContextData[_flaggedKey];
    }
}
