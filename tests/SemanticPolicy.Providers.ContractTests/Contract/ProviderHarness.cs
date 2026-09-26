using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.ContractTests.Contract;

/// <summary>
/// What the generic suite needs from a provider under test: the provider on a transport that never
/// reaches a network, a request of every type, and calls scripted by kind rather than by wire. An
/// adapter joins the suite by implementing this once.
/// </summary>
public abstract class ProviderHarness
{
    /// <summary>
    /// The text the suite plants in every request and every failure fixture and then expects to find
    /// in no message: a message that carries it carried content.
    /// </summary>
    public const string Marker = "canary-8f2c41";

    /// <summary>The provider under test.</summary>
    public abstract IDecisionProvider Provider { get; }

    /// <summary>How many requests the transport has received.</summary>
    public abstract int RequestCount { get; }

    /// <summary>
    /// A valid request of the type whose question and context both contain <paramref name="marker"/>.
    /// </summary>
    public abstract DecisionRequest CreateRequest(DecisionType type, string marker);

    /// <summary>Script the next call to answer a request created for the type successfully.</summary>
    public abstract void ScriptSuccess(DecisionType type);

    /// <summary>Script the next call to fail with the kind; whatever body it takes carries <see cref="Marker"/>.</summary>
    public abstract void ScriptFailure(FailureKind kind);

    /// <summary>
    /// Script the next call to hang until the token the transport was given is cancelled, running
    /// <paramref name="onHang"/> once the request has reached the transport and is waiting.
    /// </summary>
    public abstract void ScriptHang(Action onHang);
}
