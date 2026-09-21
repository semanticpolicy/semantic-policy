using System.Text;

namespace SemanticPolicy;

/// <summary>
/// A policy cannot be evaluated as written: it is incomplete, contradicts itself, or names something
/// the runtime does not have. It is thrown when the policy is built, validated or first used — never
/// returned as a verdict, so a policy that can never work does not wait for traffic to say so. The
/// message carries ids and the error, never a question, an option or a level an author wrote.
/// </summary>
public class PolicyConfigurationException : Exception
{
    /// <summary>Creates the exception with the ids the message names.</summary>
    /// <param name="message">The error, naming the ids and never any authored text.</param>
    /// <param name="policyId">The policy's id, or <see langword="null"/> when there is none to name.</param>
    /// <param name="ruleId">The rule's id, when the error is about one rule.</param>
    /// <param name="providerId">The provider's id, when the error is about one binding.</param>
    public PolicyConfigurationException(
        string message,
        string? policyId,
        string? ruleId = null,
        string? providerId = null)
        : base(message)
    {
        PolicyId = policyId;
        RuleId = ruleId;
        ProviderId = providerId;
    }

    /// <summary>The policy the error is about, or <see langword="null"/>.</summary>
    public string? PolicyId { get; }

    /// <summary>The rule the error is about, when it is about one rule.</summary>
    public string? RuleId { get; }

    /// <summary>The provider the error is about, when it is about one binding.</summary>
    public string? ProviderId { get; }

    /// <summary>
    /// One error in the one message shape: the policy id, then the provider and rule ids where they
    /// apply, then the error. Used wherever a configuration error is raised so every message reads alike.
    /// </summary>
    internal static PolicyConfigurationException For(
        string policyId,
        string error,
        string? ruleId = null,
        string? providerId = null)
    {
        StringBuilder text = new StringBuilder("Policy '").Append(policyId).Append('\'');
        if (providerId is not null)
        {
            text.Append(", provider '").Append(providerId).Append('\'');
        }

        if (ruleId is not null)
        {
            text.Append(", rule '").Append(ruleId).Append('\'');
        }

        string message = text.Append(": ").Append(error).ToString();
        return new PolicyConfigurationException(message, policyId, ruleId, providerId);
    }
}
