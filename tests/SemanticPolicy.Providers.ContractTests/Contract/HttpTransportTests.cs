using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Support;

namespace SemanticPolicy.Providers.ContractTests.Contract;

/// <summary>
/// What every provider that calls over HTTP holds on top of the contract: an exception from a handler
/// the host added is a failure result, a body too long to be an answer is never read whole, and the
/// client a registration sets up follows no redirect. A provider subclasses this with its harness,
/// the transport under it, and a registration.
/// </summary>
public abstract class HttpTransportTests
{
    // The limit every HTTP provider reads up to, 1 MiB.
    private const int _limit = 1024 * 1024;

    /// <summary>Registers the provider under <paramref name="name"/> with everything it requires.</summary>
    protected abstract void Register(ISemanticPolicyBuilder builder, string name);

    private protected abstract (ProviderHarness Harness, ScriptedHttpMessageHandler Handler) CreateTransport();

    [Fact]
    public async Task Handler_Exception_Reads_As_An_Unknown_Failure_Naming_Only_Its_Type()
    {
        (ProviderHarness harness, ScriptedHttpMessageHandler handler) = CreateTransport();
        handler.Throw(new CircuitOpenException($"the circuit is open for {ProviderHarness.Marker}"));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Unknown);
        result.Outcome.Message.Should().Be("CircuitOpenException from the HTTP pipeline");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true, FailureKind.Malformed)]
    [InlineData(HttpStatusCode.OK, false, FailureKind.Malformed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, FailureKind.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, FailureKind.Unavailable)]
    public async Task Body_Over_The_Limit_Is_A_Failure_Of_The_Status_Kind(
        HttpStatusCode status,
        bool declareLength,
        FailureKind kind)
    {
        (ProviderHarness harness, ScriptedHttpMessageHandler handler) = CreateTransport();
        handler.RespondLong(status, _limit + 1, declareLength);

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Kind.Should().Be(kind);
        result.Outcome.Message.Should().Be($"HTTP {(int)status}, body over {_limit} bytes");
        result.Raw.Should().BeNull();
    }

    // A JSON string of exactly the limit is read and judged on its shape, which is not an answer.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_At_The_Limit_Is_Read(bool declareLength)
    {
        (ProviderHarness harness, ScriptedHttpMessageHandler handler) = CreateTransport();
        handler.RespondLong(HttpStatusCode.OK, _limit, declareLength);

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Kind.Should().Be(FailureKind.Malformed);
        result.Outcome.Message.Should().NotContain("body over");
    }

    // A 307 or 308 resends the body, the context with it, to wherever the server points; the factory's
    // default primary handler follows both unless told not to.
    [Fact]
    public void Registered_Client_Follows_No_Redirect()
    {
        ServiceCollection services = new();
        Register(services.AddSemanticPolicy(), "remote");
        using ServiceProvider container = services.BuildServiceProvider();

        HttpMessageHandler handler = container.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("remote");
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        bool follows = handler switch
        {
            SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
            HttpClientHandler client => client.AllowAutoRedirect,
            _ => throw new InvalidOperationException($"unexpected primary handler {handler.GetType().Name}"),
        };
        follows.Should().BeFalse();
    }

    private sealed class CircuitOpenException(string message) : Exception(message);
}
