using Reqnroll;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>Starts the shared containers before the first scenario and removes them after the last.</summary>
[Binding]
public static class TestRunHooks
{
    [BeforeTestRun]
    public static Task StartContainersAsync() => RegistryInfrastructure.StartAsync();

    [AfterTestRun]
    public static Task StopContainersAsync() => RegistryInfrastructure.StopAsync();
}

/// <summary>Stops each scenario's registry and drops its database, pass or fail.</summary>
[Binding]
public sealed class ScenarioHooks
{
    private readonly RegistryScenario _scenario;

    public ScenarioHooks(RegistryScenario scenario) => _scenario = scenario;

    [AfterScenario]
    public Task DisposeRegistryAsync() => _scenario.DisposeAsync();
}
