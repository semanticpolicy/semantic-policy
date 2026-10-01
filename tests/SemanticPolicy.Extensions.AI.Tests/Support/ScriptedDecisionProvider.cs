using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Extensions.AI.Tests.Support;

/// <summary>
/// A provider double for the frontend's tests: answers every request as scripted and keeps each
/// request it received, so a test reads the context the layer built straight off the wire. It
/// declares structured context, so an object context reaches it as an object. A held script answers
/// nothing until the test releases it, which is how a test proves several evaluations were in flight
/// at once.
/// </summary>
internal sealed class ScriptedDecisionProvider : IDecisionProvider
{
    private static readonly ProviderMetadata _metadata = new("scripted", "scripted-model", 1);

    private readonly Lock _lock = new();
    private readonly List<DecisionRequest> _requests = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _arrivals = [];
    private readonly List<TaskCompletionSource> _held = [];
    private Func<DecisionRequest, CancellationToken, Task<ProviderResult>> _script =
        (_, _) => throw new InvalidOperationException("The scripted provider has no script.");

    public string Id => "scripted";

    public ProviderCapabilities Capabilities { get; } = new(
        new HashSet<DecisionType> { DecisionType.Boolean, DecisionType.Choice, DecisionType.Score },
        new HashSet<EvidenceKind> { EvidenceKind.Probability },
        RawOutput: false,
        StructuredContext: true);

    /// <summary>Every request received so far, in arrival order.</summary>
    public IReadOnlyList<DecisionRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Answer every request with the result.</summary>
    public ScriptedDecisionProvider Returns(ProviderResult result) => Returns(_ => result);

    /// <summary>Answer every request with the result the function builds for it.</summary>
    public ScriptedDecisionProvider Returns(Func<DecisionRequest, ProviderResult> result)
    {
        _script = (request, _) => Task.FromResult(result(request));
        return this;
    }

    /// <summary>
    /// Hold every request until <see cref="Release"/>, honouring the token, then answer with the result
    /// the function builds for it.
    /// </summary>
    public ScriptedDecisionProvider Holds(Func<DecisionRequest, ProviderResult> result)
    {
        _script = async (request, cancellationToken) =>
        {
            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _held.Add(held);
            }

            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => held.TrySetCanceled(cancellationToken));
            await held.Task;
            return result(request);
        };
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
            if (_requests.Count >= count)
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
        // The script runs first, so a held request is in the held list before its arrival is
        // signalled: a test that waits for every arrival and then releases cannot miss one.
        Task<ProviderResult> answer = _script(request, cancellationToken);
        List<TaskCompletionSource> reached = [];
        lock (_lock)
        {
            _requests.Add(request);
            _arrivals.RemoveAll(waiter =>
            {
                if (_requests.Count < waiter.Count)
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

        return answer;
    }

    /// <summary>A Boolean answer with the probability of its being true, the rest of the mass on false.</summary>
    public static ProviderResult Boolean(bool value, double probabilityOfTrue)
    {
        Dictionary<string, double> probabilities = new(StringComparer.Ordinal)
        {
            ["true"] = probabilityOfTrue,
            ["false"] = 1 - probabilityOfTrue,
        };
        Evidence evidence = new(EvidenceKind.Probability, probabilities);
        return new(DecisionType.Boolean, ProviderOutcome.Success, new BooleanValue(value), [evidence], _metadata);
    }
}
