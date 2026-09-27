using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;

namespace SemanticPolicy.Providers.ContractTests.SystemOne;

public sealed class SystemOneTransportTests : HttpTransportTests
{
    protected override void Register(ISemanticPolicyBuilder builder, string name) =>
        builder.AddSystemOne(name, options =>
        {
            options.BaseUrl = SystemOneHarness.BaseUrl;
            options.Model = SystemOneHarness.Model;
        });

    private protected override (ProviderHarness Harness, ScriptedHttpMessageHandler Handler) CreateTransport()
    {
        SystemOneHarness harness = new();
        return (harness, harness.Handler);
    }
}
