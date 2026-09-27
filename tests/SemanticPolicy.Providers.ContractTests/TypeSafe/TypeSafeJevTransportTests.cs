using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

public sealed class TypeSafeJevTransportTests : HttpTransportTests
{
    protected override void Register(ISemanticPolicyBuilder builder, string name) =>
        builder.AddTypeSafeJev(name, options =>
        {
            options.Route = TypeSafeJevRoute.TypeSafe;
            options.ApiKey = TypeSafeJevHarness.ApiKey;
        });

    private protected override (ProviderHarness Harness, ScriptedHttpMessageHandler Handler) CreateTransport()
    {
        TypeSafeJevHarness harness = new();
        return (harness, harness.Handler);
    }
}
