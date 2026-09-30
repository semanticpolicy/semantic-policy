using SemanticPolicy;
using SemanticPolicy.Evaluation;
using SemanticPolicy.FluentValidation;

namespace FluentValidation;

/// <summary>
/// Semantic rules: <c>.Semantic(...)</c> asks a policy about a property and reports a flagged verdict as
/// a validation failure. By default an enforced <see cref="Verdict.Deny"/> fails the property at
/// <see cref="Severity.Error"/> and an enforced <see cref="Verdict.Escalate"/> at
/// <see cref="Severity.Warning"/>, while <see cref="Verdict.Warn"/>, <see cref="Verdict.Abstain"/> and
/// <see cref="Verdict.Allow"/> pass. The mapping reads <see cref="PolicyVerdict.Effective"/>, so a policy
/// in <see cref="PolicyMode.Shadow"/> never fails the validation; a <c>severity:</c> delegate replaces
/// the mapping. The rule is asynchronous: validate with <c>ValidateAsync</c>, since <c>Validate</c>
/// throws <see cref="AsyncValidatorInvokedSynchronouslyException"/>. A verdict is a probabilistic signal
/// about content and not an authorization, so a semantic rule is one check among several and never the
/// one a security decision rests on — <c>SECURITY.md</c> says what that means for what you build on it.
/// </summary>
/// <remarks>
/// A failure's message is <c>'{PropertyName}' was flagged by semantic policy '{PolicyId}'.</c> Its
/// <see cref="Results.ValidationFailure.CustomState"/> is the <see cref="PolicyVerdict"/> the evaluator
/// returned, and its <see cref="Results.ValidationFailure.ErrorCode"/> is <c>SemanticPolicyValidator</c>.
/// The rule adds two placeholders, which <c>.WithMessage()</c> can use and
/// <see cref="Results.ValidationFailure.FormattedMessagePlaceholderValues"/> carries: <c>{PolicyId}</c>,
/// the policy's id, and <c>{Verdict}</c>, the name of <see cref="PolicyVerdict.Evaluated"/> — the verdict
/// the policy reached, which in Shadow is not the one it applied. <c>.WithMessage()</c>,
/// <c>.WithSeverity()</c>, <c>.WithState()</c> and <c>.WithErrorCode()</c> replace what they name;
/// whether the rule fails stays the mapping's.
/// </remarks>
public static class SemanticPolicyRuleBuilderExtensions
{
    /// <summary>
    /// Asks the policy registered under <paramref name="policyId"/> about the property's text. A null,
    /// empty or whitespace value passes without asking; any other value goes to the policy as one part
    /// named <c>text</c>.
    /// </summary>
    /// <typeparam name="T">The type being validated.</typeparam>
    /// <param name="ruleBuilder">The rule the check is added to.</param>
    /// <param name="evaluator">The runtime that evaluates the policy.</param>
    /// <param name="policyId">The id the policy was registered under with <c>AddPolicy</c>; non-blank.</param>
    /// <param name="severity">
    /// Maps the verdict to the failure's severity in place of the default mapping; null means no failure.
    /// Pass it by name, <c>severity:</c>, so a lambda is never taken for a context delegate.
    /// </param>
    /// <returns>The rule's options.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="ruleBuilder"/>, <paramref name="evaluator"/> or <paramref name="policyId"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="policyId"/> is empty or whitespace.</exception>
    /// <remarks>
    /// The id is looked up when the rule runs: an id no policy is registered under throws the evaluator's
    /// <see cref="ArgumentException"/> from the first <c>ValidateAsync</c>, not here, so a test that runs
    /// the validator once catches it.
    /// </remarks>
    public static IRuleBuilderOptions<T, string?> Semantic<T>(
        this IRuleBuilder<T, string?> ruleBuilder,
        IPolicyEvaluator evaluator,
        string policyId,
        Func<PolicyVerdict, Severity?>? severity = null)
    {
        ArgumentNullException.ThrowIfNull(ruleBuilder);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        return new SemanticPolicyValidator<T, string?>(evaluator, policyId, TextOf, severity).AddTo(ruleBuilder);
    }

    /// <summary>
    /// Asks the policy about the property's text. A null, empty or whitespace value passes without asking;
    /// any other value goes to the policy as one part named <c>text</c>. The policy is given here, so it
    /// needs no registration.
    /// </summary>
    /// <typeparam name="T">The type being validated.</typeparam>
    /// <param name="ruleBuilder">The rule the check is added to.</param>
    /// <param name="evaluator">The runtime that evaluates the policy.</param>
    /// <param name="policy">The policy to evaluate.</param>
    /// <param name="severity">
    /// Maps the verdict to the failure's severity in place of the default mapping; null means no failure.
    /// Pass it by name, <c>severity:</c>, so a lambda is never taken for a context delegate.
    /// </param>
    /// <returns>The rule's options.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="ruleBuilder"/>, <paramref name="evaluator"/> or <paramref name="policy"/> is null.
    /// </exception>
    /// <remarks>
    /// The evaluator checks the policy against its registered providers each time the rule runs: a policy
    /// that names a provider the evaluator does not have, or relies on something that provider does not
    /// declare, throws <see cref="PolicyConfigurationException"/> from <c>ValidateAsync</c>.
    /// </remarks>
    public static IRuleBuilderOptions<T, string?> Semantic<T>(
        this IRuleBuilder<T, string?> ruleBuilder,
        IPolicyEvaluator evaluator,
        Policy policy,
        Func<PolicyVerdict, Severity?>? severity = null)
    {
        ArgumentNullException.ThrowIfNull(ruleBuilder);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(policy);
        return new SemanticPolicyValidator<T, string?>(evaluator, policy, TextOf, severity).AddTo(ruleBuilder);
    }

    /// <summary>
    /// Asks the policy registered under <paramref name="policyId"/> about the context the delegate builds
    /// from the object being validated. The property can be of any type: the delegate decides what the
    /// policy sees. No value is skipped: a field the delegate reads can be null or blank, and
    /// <see cref="ContextPart.Text"/> throws on null, so put the rules that catch that first, under
    /// <see cref="CascadeMode.Stop"/>.
    /// </summary>
    /// <typeparam name="T">The type being validated.</typeparam>
    /// <typeparam name="TProperty">The type of the property the rule is on.</typeparam>
    /// <param name="ruleBuilder">The rule the check is added to.</param>
    /// <param name="evaluator">The runtime that evaluates the policy.</param>
    /// <param name="policyId">The id the policy was registered under with <c>AddPolicy</c>; non-blank.</param>
    /// <param name="context">Builds the context from the object being validated; its parts reach the policy in order.</param>
    /// <param name="severity">
    /// Maps the verdict to the failure's severity in place of the default mapping; null means no failure.
    /// Pass it by name, <c>severity:</c>, so a lambda is never taken for the context delegate.
    /// </param>
    /// <returns>The rule's options.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="ruleBuilder"/>, <paramref name="evaluator"/>, <paramref name="policyId"/> or
    /// <paramref name="context"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="policyId"/> is empty or whitespace.</exception>
    /// <remarks>
    /// The id is looked up when the rule runs: an id no policy is registered under throws the evaluator's
    /// <see cref="ArgumentException"/> from the first <c>ValidateAsync</c>, not here, so a test that runs
    /// the validator once catches it.
    /// </remarks>
    public static IRuleBuilderOptions<T, TProperty> Semantic<T, TProperty>(
        this IRuleBuilder<T, TProperty> ruleBuilder,
        IPolicyEvaluator evaluator,
        string policyId,
        Func<T, SemanticContext> context,
        Func<PolicyVerdict, Severity?>? severity = null)
    {
        ArgumentNullException.ThrowIfNull(ruleBuilder);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(context);
        return new SemanticPolicyValidator<T, TProperty>(evaluator, policyId, (instance, _) => context(instance), severity)
            .AddTo(ruleBuilder);
    }

    /// <summary>
    /// Asks the policy about the context the delegate builds from the object being validated. The property
    /// can be of any type: the delegate decides what the policy sees. The policy is given here, so it needs
    /// no registration. No value is skipped: a field the delegate reads can be null or blank, and
    /// <see cref="ContextPart.Text"/> throws on null, so put the rules that catch that first, under
    /// <see cref="CascadeMode.Stop"/>.
    /// </summary>
    /// <typeparam name="T">The type being validated.</typeparam>
    /// <typeparam name="TProperty">The type of the property the rule is on.</typeparam>
    /// <param name="ruleBuilder">The rule the check is added to.</param>
    /// <param name="evaluator">The runtime that evaluates the policy.</param>
    /// <param name="policy">The policy to evaluate.</param>
    /// <param name="context">Builds the context from the object being validated; its parts reach the policy in order.</param>
    /// <param name="severity">
    /// Maps the verdict to the failure's severity in place of the default mapping; null means no failure.
    /// Pass it by name, <c>severity:</c>, so a lambda is never taken for the context delegate.
    /// </param>
    /// <returns>The rule's options.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="ruleBuilder"/>, <paramref name="evaluator"/>, <paramref name="policy"/> or
    /// <paramref name="context"/> is null.
    /// </exception>
    /// <remarks>
    /// The evaluator checks the policy against its registered providers each time the rule runs: a policy
    /// that names a provider the evaluator does not have, or relies on something that provider does not
    /// declare, throws <see cref="PolicyConfigurationException"/> from <c>ValidateAsync</c>.
    /// </remarks>
    public static IRuleBuilderOptions<T, TProperty> Semantic<T, TProperty>(
        this IRuleBuilder<T, TProperty> ruleBuilder,
        IPolicyEvaluator evaluator,
        Policy policy,
        Func<T, SemanticContext> context,
        Func<PolicyVerdict, Severity?>? severity = null)
    {
        ArgumentNullException.ThrowIfNull(ruleBuilder);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        return new SemanticPolicyValidator<T, TProperty>(evaluator, policy, (instance, _) => context(instance), severity)
            .AddTo(ruleBuilder);
    }

    private static SemanticContext? TextOf<T>(T instance, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : SemanticContext.FromText(value);
    }
}
