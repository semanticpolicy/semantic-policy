using System.Diagnostics;
using System.Threading.Channels;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Evals.Running;

/// <summary>What a run did: how many rows reached the recording, how many calls it made, and how many failed.</summary>
/// <param name="Rows">The rows written to the recording.</param>
/// <param name="Attempts">The provider calls made, one per row, rule and binding.</param>
/// <param name="FailuresByKind">The attempts that came back as a failure, by the camel-case failure kind.</param>
public sealed record RunSummary(int Rows, int Attempts, IReadOnlyDictionary<string, int> FailuresByKind);

/// <summary>
/// The live loop: asks every binding's provider about every rule on every row, eagerly, and records what
/// each one answered. Nothing here is the lazy cascade the runtime walks — a fallback binding is asked even
/// when the first one decided — because only the complete attempt map lets a recording be replayed under a
/// different chain or gate. The policy's budget plays no part; the per-attempt timeout is the only clock.
/// </summary>
public sealed class EvalRunner
{
    private readonly IReadOnlyDictionary<string, IDecisionProvider> _providers;
    private readonly int _parallel;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a runner over the registered providers.</summary>
    /// <param name="providers">The providers, by the registration name a binding refers to.</param>
    /// <param name="parallel">How many calls may be in flight at once; at least one.</param>
    /// <param name="timeout">How long one call may take before it is recorded as a timeout.</param>
    public EvalRunner(IReadOnlyDictionary<string, IDecisionProvider> providers, int parallel, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallel, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _providers = providers;
        _parallel = parallel;
        _timeout = timeout;
    }

    /// <summary>
    /// Runs every row × rule × binding and writes each row as soon as it and every row before it are
    /// complete, so the file is always in dataset order and an interrupted run leaves a readable prefix.
    /// </summary>
    /// <param name="policy">The policy whose rules are asked and whose bindings name the providers.</param>
    /// <param name="rows">The rows, in dataset order; every label is sent alike.</param>
    /// <param name="writer">The recording, header already written.</param>
    /// <param name="progress">Told the number of rows written so far, after each row.</param>
    /// <param name="cancellationToken">Stops the run; the rows already written stay in the file.</param>
    /// <exception cref="ArgumentException">A binding names a provider this runner was not given.</exception>
    public async Task<RunSummary> RunAsync(
        Policy policy,
        IReadOnlyList<DatasetRow> rows,
        RecordingWriter writer,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(writer);
        foreach (ProviderBinding binding in policy.Bindings)
        {
            if (!_providers.ContainsKey(binding.ProviderId))
            {
                throw new ArgumentException($"Binding provider '{binding.ProviderId}' is not registered.", nameof(policy));
            }
        }

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim slots = new(_parallel, _parallel);

        // Rows enter the channel in dataset order, each as the task that completes when its last attempt
        // does; reading them in that order is what keeps the file in dataset order while later rows finish
        // first.
        Channel<Task<RecordedRow>> ordered = Channel.CreateUnbounded<Task<RecordedRow>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        Task dispatching = DispatchAsync(policy, rows, slots, ordered.Writer, stop.Token);

        int written = 0;
        int attempts = 0;
        Dictionary<FailureKind, int> failures = [];
        try
        {
            await foreach (Task<RecordedRow> pending in ordered.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                RecordedRow row = await pending.ConfigureAwait(false);
                await writer.WriteAsync(row, stop.Token).ConfigureAwait(false);
                foreach (ProviderResult result in row.Attempts.Values.SelectMany(byProvider => byProvider.Values))
                {
                    attempts++;
                    if (result.Outcome is { Status: OutcomeStatus.Failure, Kind: { } kind })
                    {
                        failures[kind] = failures.GetValueOrDefault(kind) + 1;
                    }
                }

                written++;
                progress?.Report(written);
            }

            await dispatching.ConfigureAwait(false);
        }
        catch
        {
            // The writer is disposed by the caller as soon as this returns, so no call may still be running
            // when it does: every attempt is cancelled and waited for before the failure goes on.
            await stop.CancelAsync().ConfigureAwait(false);
            await DrainAsync(dispatching, ordered.Reader).ConfigureAwait(false);
            throw;
        }

        Dictionary<string, int> byKind = new(failures.Count, StringComparer.Ordinal);
        foreach (FailureKind kind in failures.Keys.Order())
        {
            byKind[Names.Camel(kind)] = failures[kind];
        }

        return new RunSummary(written, attempts, byKind);
    }

    private async Task DispatchAsync(
        Policy policy,
        IReadOnlyList<DatasetRow> rows,
        SemaphoreSlim slots,
        ChannelWriter<Task<RecordedRow>> ordered,
        CancellationToken runToken)
    {
        List<(string RuleId, string Provider, Task<ProviderResult> Result)> attempts = [];
        try
        {
            foreach (DatasetRow row in rows)
            {
                attempts = new(policy.Rules.Count * policy.Bindings.Count);
                foreach (Rule rule in policy.Rules)
                {
                    DecisionRequest request = rule.CreateRequest(row.Input);
                    request.EnsureValid();
                    foreach (ProviderBinding binding in policy.Bindings)
                    {
                        await slots.WaitAsync(runToken).ConfigureAwait(false);
                        attempts.Add((rule.Id, binding.ProviderId, AttemptAsync(rule.Type, binding.ProviderId, request, slots, runToken)));
                    }
                }

                ordered.TryWrite(CollectAsync(row.Id, attempts));
            }

            ordered.TryComplete();
        }
        catch (Exception failure)
        {
            // The row that was being dispatched never reaches the channel, so its calls are waited for here
            // rather than left running past the end of the run.
            await Task.WhenAll(attempts.Select(attempt => (Task)attempt.Result))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ordered.TryComplete(failure);
        }
    }

    private async Task<ProviderResult> AttemptAsync(
        DecisionType type,
        string providerName,
        DecisionRequest request,
        SemaphoreSlim slots,
        CancellationToken runToken)
    {
        try
        {
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            attempt.CancelAfter(_timeout);
            long started = Stopwatch.GetTimestamp();
            try
            {
                return await _providers[providerName].DecideAsync(request, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested && !runToken.IsCancellationRequested)
            {
                // The runtime's own mapping of budget expiry, so a recorded timeout means what a live one does.
                return Failed(type, FailureKind.Timeout, providerName, started, "the timeout expired before the provider answered.");
            }
            catch (Exception thrown) when (!runToken.IsCancellationRequested)
            {
                // A provider is meant to return its failures, and the runtime lets a thrown one escape as a
                // bug. A run records it instead, so one broken call does not discard every other row. Only the
                // type is kept: an adapter's message can quote the request it failed on.
                return Failed(
                    type,
                    FailureKind.Unknown,
                    providerName,
                    started,
                    $"the provider threw {thrown.GetType().FullName} instead of returning a result.");
            }
        }
        finally
        {
            slots.Release();
        }
    }

    // The provider id is the registration name and the model is null because no adapter answered, which is
    // how the runtime records an attempt it gave up on.
    private static ProviderResult Failed(DecisionType type, FailureKind kind, string providerName, long started, string message) =>
        ProviderResult.Failed(
            type,
            kind,
            message,
            new ProviderMetadata(providerName, Model: null, Stopwatch.GetElapsedTime(started).TotalMilliseconds));

    private static async Task<RecordedRow> CollectAsync(
        string id,
        List<(string RuleId, string Provider, Task<ProviderResult> Result)> attempts)
    {
        await Task.WhenAll(attempts.Select(attempt => attempt.Result)).ConfigureAwait(false);
        Dictionary<string, IReadOnlyDictionary<string, ProviderResult>> byRule = new(StringComparer.Ordinal);
        foreach (IGrouping<string, (string RuleId, string Provider, Task<ProviderResult> Result)> rule in
                 attempts.GroupBy(attempt => attempt.RuleId, StringComparer.Ordinal))
        {
            byRule[rule.Key] = rule.ToDictionary(
                attempt => attempt.Provider,
                attempt => attempt.Result.Result,
                StringComparer.Ordinal);
        }

        return new RecordedRow(id, byRule);
    }

    private static async Task DrainAsync(Task dispatching, ChannelReader<Task<RecordedRow>> ordered)
    {
        await dispatching.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        while (ordered.TryRead(out Task<RecordedRow>? pending))
        {
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
