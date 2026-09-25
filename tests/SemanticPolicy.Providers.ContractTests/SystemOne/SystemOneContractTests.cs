using SemanticPolicy.Providers.ContractTests.Contract;

namespace SemanticPolicy.Providers.ContractTests.SystemOne;

public sealed class SystemOneContractTests : ProviderContractTests
{
    protected override ProviderHarness CreateHarness() => new SystemOneHarness();
}
