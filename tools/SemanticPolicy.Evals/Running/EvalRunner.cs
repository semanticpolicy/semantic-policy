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
    private static readonly TimeSpan _longestRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyDictionary<string, IDecisionProvider> _providers;
    private readonly int _parallel;
    private readonly TimeSpan _timeout;
    private readonly int _retries;
    private readonly TimeSpan _retryBase;

    /// <summary>Creates a runner over the registered providers.</summary>
    /// <param name="providers">The providers, by the registration name a binding refers to.</param>
    /// <param name="parallel">How many calls may be in flight at once; at least one.</param>
    /// <param name="timeout">How long one call may take before it is recorded as a timeout; each retry gets its own.</param>
    /// <param name="retries">How many times an unavailable answer is called again; 0 calls once.</param>
    /// <param name="retryBase">The longest wait before the first retry; see <see cref="RetryDelay"/>.</param>
    public EvalRunner(
        IReadOnlyDictionary<string, IDecisionProvider> providers,
        int parallel,
        TimeSpan timeout,
        int retries,
        TimeSpan retryBase)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallel, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryBase, TimeSpan.Zero);
        _providers = providers;
        _parallel = parallel;
        _timeout = timeout;
        _retries = retries;
        _retryBase = retryBase;
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
    public Task<RunSummary> RunAsync(
        Policy policy,
        IReadOnlyList<DatasetRow> rows,
        RecordingWriter writer,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return RecordAsync(
            policy,
            [.. rows.Select(row => new PlannedRow(row, Recorded: null))],
            writer,
            progress,
            keepRecorded: false,
            cancellationToken);
    }

    /// <summary>
    /// Finishes a recording into a new file: every row in dataset order, calling every binding of a row the
    /// recording lacks and only the attempts it holds as unavailable, and keeping every other attempt as it was
    /// recorded. A resume cut short, or failing, still writes each row it did not reach — as finished when all
    /// its calls came back, as recorded otherwise — before the failure goes on, and that step calls nothing.
    /// </summary>
    /// <param name="policy">The policy the recording was made with.</param>
    /// <param name="rows">The rows the recording was made on, in dataset order.</param>
    /// <param name="recorded">The recording's rows, by id.</param>
    /// <param name="writer">The new file, header already written.</param>
    /// <param name="progress">Told the number of rows written so far, after each row.</param>
    /// <param name="cancellationToken">Stops the calls; the rows not yet written still go in as described.</param>
    /// <returns>The rows written, and the calls this resume made.</returns>
    /// <exception cref="ArgumentException">A binding names a provider this runner was not given.</exception>
    public Task<RunSummary> ResumeAsync(
        Policy policy,
        IReadOnlyList<DatasetRow> rows,
        IReadOnlyDictionary<string, RecordedRow> recorded,
        RecordingWriter writer,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(recorded);
        return RecordAsync(
            policy,
            [.. rows.Select(row => new PlannedRow(row, recorded.GetValueOrDefault(row.Id)))],
            writer,
            progress,
            keepRecorded: true,
            cancellationToken);
    }

    /// <summary>How many attempts a resume of these rows would make before any retry.</summary>
    /// <param name="policy">The policy the recording was made with.</param>
    /// <param name="rows">The rows the recording was made on.</param>
    /// <param name="recorded">The recording's rows, by id.</param>
    public static int AttemptsToFinish(
        Policy policy,
        IReadOnlyList<DatasetRow> rows,
        IReadOnlyDictionary<string, RecordedRow> recorded)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(recorded);
        return rows.Sum(row => policy.Rules.Sum(rule => policy.Bindings.Count(binding =>
            IsToBeMade(recorded.GetValueOrDefault(row.Id), rule.Id, binding.ProviderId))));
    }

    private async Task<RunSummary> RecordAsync(
        Policy policy,
        IReadOnlyList<PlannedRow> rows,
        RecordingWriter writer,
        IProgress<int>? progress,
        bool keepRecorded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
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
        Channel<Task<FinishedRow>> ordered = Channel.CreateUnbounded<Task<FinishedRow>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        Task dispatching = DispatchAsync(policy, rows, slots, ordered.Writer, stop.Token);

        int written = 0;
        int attempts = 0;
        Dictionary<FailureKind, int> failures = [];
        Task<FinishedRow>? unwritten = null;
        try
        {
            await foreach (Task<FinishedRow> pending in ordered.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                unwritten = pending;
                FinishedRow row = await pending.ConfigureAwait(false);

                // Never cancelled: a write stopped halfway could leave part of a line in the file, and a row whose
                // calls all came back is worth its line.
                await writer.WriteAsync(row.Row, CancellationToken.None).ConfigureAwait(false);
                unwritten = null;
                foreach (ProviderResult result in row.Made)
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
            List<Task<FinishedRow>> left = await DrainAsync(dispatching, ordered.Reader).ConfigureAwait(false);
            if (keepRecorded)
            {
                // A row read from the channel but not written comes first among the rows still to go in.
                if (unwritten is not null)
                {
                    left.Insert(0, unwritten);
                }

                await WriteRestAsync(rows.Skip(written), left, writer).ConfigureAwait(false);
            }

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
        IReadOnlyList<PlannedRow> rows,
        SemaphoreSlim slots,
        ChannelWriter<Task<FinishedRow>> ordered,
        CancellationToken runToken)
    {
        List<(string RuleId, string Provider, Task<Answered> Answer)> attempts = [];
        try
        {
            foreach (PlannedRow row in rows)
            {
                attempts = [];
                foreach (Rule rule in policy.Rules)
                {
                    DecisionRequest? request = null;
                    foreach (ProviderBinding binding in policy.Bindings)
                    {
                        if (!IsToBeMade(row.Recorded, rule.Id, binding.ProviderId))
                        {
                            continue;
                        }

                        if (request is null)
                        {
                            request = rule.CreateRequest(row.Row.Input);
                            request.EnsureValid();
                        }

                        await slots.WaitAsync(runToken).ConfigureAwait(false);
                        attempts.Add((rule.Id, binding.ProviderId, AttemptAsync(rule.Type, binding.ProviderId, request, slots, runToken)));
                    }
                }

                ordered.TryWrite(CollectAsync(row, attempts));
            }

            ordered.TryComplete();
        }
        catch (Exception failure)
        {
            // The row that was being dispatched never reaches the channel, so its calls are waited for here
            // rather than left running past the end of the run.
            await Task.WhenAll(attempts.Select(attempt => (Task)attempt.Answer))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ordered.TryComplete(failure);
        }
    }

    // An attempt is made when the row holds none for this rule and binding, or holds an unavailable one: the
    // answer retries gave up on. Any other recorded result is kept: a new call would pay again for an answer.
    private static bool IsToBeMade(RecordedRow? recorded, string ruleId, string provider) =>
        recorded?.Attempts.GetValueOrDefault(ruleId)?.GetValueOrDefault(provider) is not { } result
        || result.Outcome is { Status: OutcomeStatus.Failure, Kind: FailureKind.Unavailable };

    /// <summary>
    /// The wait before a retry: uniform between half and the whole of <paramref name="retryBase"/> doubled once
    /// for every retry before this one, and never more than 30 s. Half the range is always waited, so a retry
    /// does not go straight back into the limit that refused the call, and the other half is spread, so calls
    /// refused together do not come back together.
    /// </summary>
    /// <param name="retry">Which retry of the attempt this is, from 1.</param>
    /// <param name="retryBase">The longest wait before the first retry.</param>
    /// <param name="draw">Where in the range the wait falls: 0 for its shortest, 1 for its longest.</param>
    public static TimeSpan RetryDelay(int retry, TimeSpan retryBase, double draw)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retry, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryBase, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(draw);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draw, 1);

        // Any base of a tick or more reaches the cap within 30 doublings; stopping there keeps the product finite.
        double longest = Math.Min(
            retryBase.TotalMilliseconds * Math.Pow(2, Math.Min(retry - 1, 30)),
            _longestRetryDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds((longest / 2) + (draw * longest / 2));
    }

    private async Task<Answered> AttemptAsync(
        DecisionType type,
        string providerName,
        DecisionRequest request,
        SemaphoreSlim slots,
        CancellationToken runToken)
    {
        try
        {
            // Only an unavailable answer is asked again: it is what a rate limit, an overloaded server or one that
            // cannot be reached gives, and what a run's own parallelism sets off. A timeout is not, since the call may have been slow rather than
            // refused. The slot stays taken through the wait, so a provider shedding load is not handed another
            // row's calls meanwhile.
            ProviderResult result = await CallAsync(type, providerName, request, runToken).ConfigureAwait(false);
            int retries = 0;
            while (retries < _retries && result.Outcome is { Status: OutcomeStatus.Failure, Kind: FailureKind.Unavailable })
            {
                retries++;
                await Task.Delay(RetryDelay(retries, _retryBase, Random.Shared.NextDouble()), runToken).ConfigureAwait(false);
                result = await CallAsync(type, providerName, request, runToken).ConfigureAwait(false);
            }

            return new Answered(result, retries);
        }
        finally
        {
            slots.Release();
        }
    }

    // One call under its own timeout.
    private async Task<ProviderResult> CallAsync(
        DecisionType type,
        string providerName,
        DecisionRequest request,
        CancellationToken runToken)
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

    // The provider id is the registration name and the model is null because no adapter answered, which is
    // how the runtime records an attempt it gave up on.
    private static ProviderResult Failed(DecisionType type, FailureKind kind, string providerName, long started, string message) =>
        ProviderResult.Failed(
            type,
            kind,
            message,
            new ProviderMetadata(providerName, Model: null, Stopwatch.GetElapsedTime(started).TotalMilliseconds));

    // The row as recorded with every attempt made here put in: a made attempt takes the place of the recorded one
    // and brings its own retry count, and every other attempt keeps its result and count.
    private static async Task<FinishedRow> CollectAsync(
        PlannedRow planned,
        List<(string RuleId, string Provider, Task<Answered> Answer)> attempts)
    {
        await Task.WhenAll(attempts.Select(attempt => attempt.Answer)).ConfigureAwait(false);
        if (attempts.Count == 0 && planned.Recorded is { } kept)
        {
            return new FinishedRow(kept, []);
        }

        Dictionary<string, Dictionary<string, ProviderResult>> byRule = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, int>> retries = new(StringComparer.Ordinal);
        if (planned.Recorded is { } recorded)
        {
            foreach ((string ruleId, IReadOnlyDictionary<string, ProviderResult> byProvider) in recorded.Attempts)
            {
                byRule[ruleId] = new Dictionary<string, ProviderResult>(byProvider, StringComparer.Ordinal);
            }

            foreach ((string ruleId, IReadOnlyDictionary<string, int> byProvider) in
                     recorded.Retries ?? Enumerable.Empty<KeyValuePair<string, IReadOnlyDictionary<string, int>>>())
            {
                retries[ruleId] = new Dictionary<string, int>(byProvider, StringComparer.Ordinal);
            }
        }

        List<ProviderResult> made = new(attempts.Count);
        foreach ((string ruleId, string provider, Task<Answered> answer) in attempts)
        {
            Answered answered = await answer.ConfigureAwait(false);
            made.Add(answered.Result);
            if (!byRule.TryGetValue(ruleId, out Dictionary<string, ProviderResult>? results))
            {
                byRule[ruleId] = results = new(StringComparer.Ordinal);
            }

            results[provider] = answered.Result;
            if (!retries.TryGetValue(ruleId, out Dictionary<string, int>? counts))
            {
                retries[ruleId] = counts = new(StringComparer.Ordinal);
            }

            counts.Remove(provider);
            if (answered.Retries > 0)
            {
                counts[provider] = answered.Retries;
            }
        }

        Dictionary<string, IReadOnlyDictionary<string, int>> retried = retries
            .Where(rule => rule.Value.Count > 0)
            .ToDictionary(rule => rule.Key, rule => (IReadOnlyDictionary<string, int>)rule.Value, StringComparer.Ordinal);
        RecordedRow row = new(
            planned.Row.Id,
            byRule.ToDictionary(rule => rule.Key, rule => (IReadOnlyDictionary<string, ProviderResult>)rule.Value, StringComparer.Ordinal),
            retried.Count > 0 ? retried : null);
        return new FinishedRow(row, made);
    }

    private static async Task<List<Task<FinishedRow>>> DrainAsync(Task dispatching, ChannelReader<Task<FinishedRow>> ordered)
    {
        await dispatching.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        List<Task<FinishedRow>> left = [];
        while (ordered.TryRead(out Task<FinishedRow>? pending))
        {
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            left.Add(pending);
        }

        return left;
    }

    // The end of a resume that did not get through: each row not yet written goes in as finished when every call
    // it needed came back, else as recorded, else not at all. Nothing is called, but every row left is written, so
    // it takes longer the larger the recording is; the tool's Program.cs gives it the time after Ctrl+C.
    private static async Task WriteRestAsync(
        IEnumerable<PlannedRow> rest,
        IReadOnlyList<Task<FinishedRow>> dispatched,
        RecordingWriter writer)
    {
        int index = 0;
        foreach (PlannedRow planned in rest)
        {
            Task<FinishedRow>? pending = index < dispatched.Count ? dispatched[index] : null;
            index++;
            RecordedRow? row = pending is { IsCompletedSuccessfully: true }
                ? (await pending.ConfigureAwait(false)).Row
                : planned.Recorded;
            if (row is not null)
            {
                await writer.WriteAsync(row, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // A row to record, with what the recording being finished holds for it; null for a row it lacks, and always
    // null in a new run.
    private readonly record struct PlannedRow(DatasetRow Row, RecordedRow? Recorded);

    // A row ready to write, and the results of the calls made for it here.
    private readonly record struct FinishedRow(RecordedRow Row, IReadOnlyList<ProviderResult> Made);

    // What an attempt recorded, and how many calls after the first it took to get there.
    private readonly record struct Answered(ProviderResult Result, int Retries);
}
