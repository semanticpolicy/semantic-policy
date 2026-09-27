using System.Globalization;
using System.Net;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers;

/// <summary>
/// Sends one provider request under a timer of its own. Only that timer ends a call as a
/// <see cref="FailureKind.Timeout"/>: the caller's cancellation propagates, and a cancellation with
/// neither token cancelled is a programming error that propagates too. A transport failure is
/// <see cref="FailureKind.Unavailable"/>, described by its error code and nothing the exception says;
/// any other exception from the client or its handlers, such as a resilience handler's rejection, is
/// <see cref="FailureKind.Unknown"/>, described by its type alone.
/// </summary>
/// <remarks>
/// Compiled into every HTTP provider package as a linked file rather than shipped as a public type, so
/// the packages share one behaviour without a shared API to version.
/// </remarks>
internal static class ProviderHttpCall
{
    /// <summary>
    /// The longest body a provider reads, 1 MiB. A decision is a few hundred bytes, so a longer body is
    /// not an answer, and reading it whole would let a misbehaving server fill the host's memory.
    /// </summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// Sends the message and reads the body, up to <see cref="MaxBodyBytes"/>, before the timer stops,
    /// then hands both to <paramref name="interpret"/> while the response is still open. A failure goes
    /// to <paramref name="fail"/> as a kind and a message that carries no content; a body over the limit
    /// is <see cref="FailureKind.Malformed"/> on a 200 and the status's kind otherwise. An exception
    /// from <paramref name="interpret"/> is the provider's own bug and propagates. The client and the
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
        HttpResponseMessage? response = null;
        try
        {
            byte[]? body;
            try
            {
                response = await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timer.Token)
                    .ConfigureAwait(false);
                body = await ReadBoundedAsync(response.Content, timer.Token).ConfigureAwait(false);
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A handler the host added, such as a circuit breaker, a rate limiter or a resilience
                // pipeline's own timeout, throws its own type. Its message may quote the request, so
                // only the type is kept.
                return fail(FailureKind.Unknown, $"{exception.GetType().Name} from the HTTP pipeline");
            }

            if (body is null)
            {
                HttpStatusCode status = response.StatusCode;
                FailureKind kind = status == HttpStatusCode.OK ? FailureKind.Malformed : ProviderHttp.KindOf(status);
                string limit = MaxBodyBytes.ToString(CultureInfo.InvariantCulture);
                return fail(kind, $"HTTP {(int)status}, body over {limit} bytes");
            }

            return interpret(response, body);
        }
        finally
        {
            response?.Dispose();
        }
    }

    // The body, or null when it is longer than the limit: a declared length over it is refused before
    // any byte is read, and an undeclared one is read no further than the write that crosses it.
    // CopyToAsync hands the token to every kind of content; ReadAsStreamAsync drops it for a content
    // that does not stream its own, and would then buffer it whole before the limit could apply.
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using CappedBuffer buffer = new();
        try
        {
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (BodyTooLongException)
        {
            return null;
        }

        return buffer.ToArray();
    }

    // Refuses the write that would take it past the limit, which ends the copy there. Every write path
    // checks, because a content may use any of them.
    private sealed class CappedBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Admit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Admit(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Admit(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Admit(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            Admit(1);
            base.WriteByte(value);
        }

        private void Admit(int count)
        {
            if (Length + count > MaxBodyBytes)
            {
                throw new BodyTooLongException();
            }
        }
    }

    // Not an IOException, so HttpContent.CopyToAsync passes it through rather than wrapping it in an
    // HttpRequestException that would read as Unavailable.
    private sealed class BodyTooLongException : Exception
    {
    }
}
