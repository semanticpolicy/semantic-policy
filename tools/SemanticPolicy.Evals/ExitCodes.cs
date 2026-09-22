namespace SemanticPolicy.Evals;

/// <summary>
/// The process exit codes, so a script can tell a bad invocation from an unsatisfiable request without
/// reading the text.
/// </summary>
public static class ExitCodes
{
    /// <summary>The verb did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The options, a file, a row or the policy were wrong; the message says which.</summary>
    public const int UsageOrData = 1;

    /// <summary>Every input was fine but no threshold or gate satisfies the constraints given.</summary>
    public const int InfeasibleConstraint = 2;
}
