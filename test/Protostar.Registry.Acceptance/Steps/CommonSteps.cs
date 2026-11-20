using Protostar.Registry.Acceptance.Support;
using Reqnroll;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps every feature starts from: a registry over a fresh database, and users signed in to it.</summary>
[Binding]
public sealed class CommonSteps
{
    private readonly RegistryScenario _scenario;

    public CommonSteps(RegistryScenario scenario) => _scenario = scenario;

    [Given("a running registry")]
    public Task GivenARunningRegistry() => _scenario.StartAsync();

    [Given("the user {string} is signed in")]
    public Task GivenTheUserIsSignedIn(string login) => _scenario.SignInAsync(login);

    [Given("the users {string} and {string} are signed in")]
    public async Task GivenTheUsersAreSignedIn(string first, string second)
    {
        await _scenario.SignInAsync(first);
        await _scenario.SignInAsync(second);
    }
}
