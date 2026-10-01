using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SemanticPolicy.Evals.Policies;

/// <summary>
/// The policy file every verb starts from, read in the library's own JSON format and held to the same
/// validation the runtime applies at first use, so a policy that cannot be evaluated fails here and not
/// after the first provider call. A verb that makes a new policy writes it here in the same format.
/// </summary>
public static class PolicyFile
{
    // A written policy is read by people as much as by the library, and is diffed against the one it came from: no
    // byte-order mark, the same newline everywhere, and nothing escaped that a reader would have to decode.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions _indented =
        new(SemanticPolicyJson.Options)
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

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

    /// <summary>
    /// Writes the policy in the library's JSON format, indented, with <c>\n</c> line ends, UTF-8 without a byte-order
    /// mark and a final newline, replacing a file that is already there. The same policy writes the same bytes.
    /// </summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="policy">The policy to write; the caller has validated it.</param>
    /// <exception cref="EvalsException">The file cannot be written; the message names the path.</exception>
    public static void Write(string path, Policy policy)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(policy);

        // Serialised first: a policy that cannot be written must not leave a truncated file behind.
        string json = JsonSerializer.Serialize(policy, _indented);
        try
        {
            File.WriteAllText(path, json + "\n", _utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EvalsException($"Policy file '{path}' cannot be written: {e.Message}");
        }
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
