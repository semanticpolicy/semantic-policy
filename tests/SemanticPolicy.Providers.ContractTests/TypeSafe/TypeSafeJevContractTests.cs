using SemanticPolicy.Providers.ContractTests.Contract;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

public sealed class TypeSafeJevContractTests : ProviderContractTests
{
    protected override ProviderHarness CreateHarness() => new TypeSafeJevHarness();
}
