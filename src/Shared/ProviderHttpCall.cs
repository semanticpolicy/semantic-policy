using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers;

/// <summary>
/// Sends one provider request under a timer of its own. Only that timer ends a call as a
/// <see cref="FailureKind.Timeout"/>: the caller's cancellation propagates, and a cancellation with
/// neither token cancelled is a programming error that propagates too. A transport failure is
/// <see cref="FailureKind.Unavailable"/>, described by its error code and nothing the exception says.
/// </summary>
/// <remarks>
/// Compiled into every HTTP provider package as a linked file rather than shipped as a public type, so
/// the packages share one behaviour without a shared API to version.
/// </remarks>
internal static class ProviderHttpCall
{
    /// <summary>
    /// Sends the message and reads the whole body before the timer stops, then hands both to
    /// <paramref name="interpret"/> while the response is still open. A failure goes to
    /// <paramref name="fail"/> as a kind and a message that carries no content. The client and the
    /// message are the caller's to dispose.
    /// </summary>
    public static async Task<ProviderResult> SendAsync(
        HttpClient client,
        HttpRequestMessage message,
        TimeSpan timeout,
        Func<HttpResponseMessage, byte[], ProviderResult> interpret,
        Func<FailureKind, string, ProviderResult> fail,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timer.Token)
                .ConfigureAwait(false);
            byte[] body = await response.Content.ReadAsByteArrayAsync(timer.Token).ConfigureAwait(false);
            return interpret(response, body);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            long milliseconds = (long)Math.Round(timeout.TotalMilliseconds);
            return fail(FailureKind.Timeout, $"no response within {milliseconds} ms");
        }
        catch (HttpRequestException exception)
        {
            return fail(FailureKind.Unavailable, $"HttpRequestException: {exception.HttpRequestError}");
        }
        catch (HttpIOException exception)
        {
            return fail(FailureKind.Unavailable, $"HttpIOException: {exception.HttpRequestError}");
        }
    }
}
