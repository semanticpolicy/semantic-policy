using System.Text.Json;

namespace SemanticPolicy.Evals.Policies;

/// <summary>
/// The policy file every verb starts from, read in the library's own JSON format and held to the same
/// validation the runtime applies at first use, so a policy that cannot be evaluated fails here and not
/// after the first provider call.
/// </summary>
public static class PolicyFile
{
    /// <summary>Reads, deserializes and validates the policy.</summary>
    /// <param name="path">The policy file.</param>
    /// <exception cref="EvalsException">
    /// The file cannot be read, is not a policy document, or fails validation; the message names the path
    /// and, from the validation error, the policy, rule and provider ids.
    /// </exception>
    public static Policy Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EvalsException($"Policy file '{path}' cannot be read: {e.Message}");
        }

        Policy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<Policy>(json, SemanticPolicyJson.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            // A record constructor rejecting a null member surfaces as an ArgumentException, not a JsonException.
            throw new EvalsException($"Policy file '{path}' is not a policy document: {e.Message}");
        }

        if (policy is null)
        {
            throw new EvalsException($"Policy file '{path}' holds no policy.");
        }

        try
        {
            policy.Validate();
        }
        catch (PolicyConfigurationException e)
        {
            // The library's message already names the policy, provider and rule ids; nothing else is added.
            throw new EvalsException($"Policy file '{path}': {e.Message}");
        }

        return policy;
    }

    /// <summary>The rule a verb works on: the only one, or the one named by <c>--rule</c>.</summary>
    /// <param name="policy">The loaded policy.</param>
    /// <param name="ruleId">The <c>--rule</c> value, or <see langword="null"/> when it was not given.</param>
    /// <exception cref="EvalsException">Several rules and no id, or an id no rule has; the rule ids are listed.</exception>
    public static Rule SelectRule(Policy policy, string? ruleId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (ruleId is null)
        {
            return policy.Rules.Count == 1
                ? policy.Rules[0]
                : throw new EvalsException(
                    $"Policy '{policy.Id}' has {policy.Rules.Count} rules ({RuleIds(policy)}); pass --rule <id>.");
        }

        return policy.Rules.FirstOrDefault(rule => rule.Id == ruleId)
            ?? throw new EvalsException($"Policy '{policy.Id}' has no rule '{ruleId}'; its rules are {RuleIds(policy)}.");
    }

    private static string RuleIds(Policy policy) => string.Join(", ", policy.Rules.Select(rule => $"'{rule.Id}'"));
}
