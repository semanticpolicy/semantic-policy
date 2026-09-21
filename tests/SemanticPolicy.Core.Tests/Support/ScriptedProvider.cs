using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Core.Tests.Support;

/// <summary>
/// A provider double: answers every request the way it was scripted and records each request it
/// received, in arrival order, with the token it was given. The default capabilities declare every
/// decision type and Probability and Score evidence; a test narrows them to provoke a configuration
/// error.
/// </summary>
internal sealed class ScriptedProvider : IDecisionProvider
{
    private static readonly ProviderMetadata _metadata = new("scripted", "scripted-model", 1);

    private readonly Lock _lock = new();
    private readonly List<Call> _calls = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _arrivals = [];
    private readonly List<TaskCompletionSource> _held = [];
    private Func<DecisionRequest, CancellationToken, Task<ProviderResult>> _script =
        (_, _) => throw new InvalidOperationException("The scripted provider has no script.");

    public ScriptedProvider(string id = "scripted", ProviderCapabilities? capabilities = null)
    {
        Id = id;
        Capabilities = capabilities ?? Everything;
    }

    /// <summary>Every decision type, Probability and Score evidence, no raw output, structured context.</summary>
    public static ProviderCapabilities Everything { get; } = new(
        new HashSet<DecisionType> { DecisionType.Boolean, DecisionType.Choice, DecisionType.Score },
        new HashSet<EvidenceKind> { EvidenceKind.Probability, EvidenceKind.Score },
        RawOutput: false,
        StructuredContext: true);

    public string Id { get; }

    public ProviderCapabilities Capabilities { get; }

    /// <summary>Every request received so far, in arrival order.</summary>
    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Answer every request with the result.</summary>
    public ScriptedProvider Returns(ProviderResult result) => Returns(_ => result);

    /// <summary>Answer every request with the result the function builds for it.</summary>
    public ScriptedProvider Returns(Func<DecisionRequest, ProviderResult> result)
    {
        _script = (request, _) => Task.FromResult(result(request));
        return this;
    }

    /// <summary>
    /// Wait for the duration, honouring the token, then answer with the result. A cancelled token
    /// surfaces as the <see cref="OperationCanceledException"/> that
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> throws.
    /// </summary>
    public ScriptedProvider Delays(TimeSpan duration, ProviderResult result)
    {
        _script = async (_, cancellationToken) =>
        {
            await Task.Delay(duration, cancellationToken);
            return result;
        };
        return this;
    }

    /// <summary>
    /// Hold every request until <see cref="Release"/>, honouring the token, then answer with the result.
    /// </summary>
    public ScriptedProvider Holds(ProviderResult result)
    {
        _script = async (_, cancellationToken) =>
        {
            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _held.Add(held);
            }

            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => held.TrySetCanceled(cancellationToken));
            await held.Task;
            return result;
        };
        return this;
    }

    /// <summary>Throw the exception from <see cref="DecideAsync"/> itself, before any task exists.</summary>
    public ScriptedProvider Throws(Exception exception)
    {
        _script = (_, _) => throw exception;
        return this;
    }

    /// <summary>Lets every held request answer.</summary>
    public void Release()
    {
        TaskCompletionSource[] held;
        lock (_lock)
        {
            held = [.. _held];
            _held.Clear();
        }

        foreach (TaskCompletionSource call in held)
        {
            call.TrySetResult();
        }
    }

    /// <summary>Completes once at least <paramref name="count"/> requests have arrived.</summary>
    public Task WaitForCallsAsync(int count, CancellationToken cancellationToken)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_calls.Count >= count)
            {
                return Task.CompletedTask;
            }

            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _arrivals.Add((count, signal));
        }

        cancellationToken.Register(() => signal.TrySetCanceled(cancellationToken));
        return signal.Task;
    }

    public Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        Record(new Call(request, cancellationToken, cancellationToken.IsCancellationRequested));
        return _script(request, cancellationToken);
    }

    /// <summary>A successful result; the decision type follows the value's shape.</summary>
    public static ProviderResult Success(DecisionValue value, params (string Key, double Value)[] probabilities) =>
        new(TypeOf(value), ProviderOutcome.Success, value, [Probability(probabilities)], _metadata);

    public static ProviderResult Abstain(DecisionType type, string? message = null) =>
        new(type, ProviderOutcome.Abstain(message), Value: null, Evidence: [], _metadata);

    public static ProviderResult Failure(DecisionType type, FailureKind kind, string message = "scripted") =>
        ProviderResult.Failed(type, kind, message, _metadata);

    public static Evidence Probability(params (string Key, double Value)[] values) =>
        new(
            EvidenceKind.Probability,
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static DecisionType TypeOf(DecisionValue value) =>
        value switch
        {
            BooleanValue => DecisionType.Boolean,
            ChoiceValue => DecisionType.Choice,
            ScoreValue => DecisionType.Score,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private void Record(Call call)
    {
        List<TaskCompletionSource> reached = [];
        lock (_lock)
        {
            _calls.Add(call);
            _arrivals.RemoveAll(waiter =>
            {
                if (_calls.Count < waiter.Count)
                {
                    return false;
                }

                reached.Add(waiter.Signal);
                return true;
            });
        }

        foreach (TaskCompletionSource signal in reached)
        {
            signal.TrySetResult();
        }
    }

    /// <summary>One request as the provider saw it.</summary>
    /// <param name="Request">The request.</param>
    /// <param name="Token">The token the call was given; read it later to see whether it was cancelled.</param>
    /// <param name="CancelledOnArrival">Whether the token was already cancelled when the request arrived.</param>
    public sealed record Call(DecisionRequest Request, CancellationToken Token, bool CancelledOnArrival);
}
