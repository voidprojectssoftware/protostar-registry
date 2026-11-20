using System.Net;
using System.Net.Http.Json;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for MachineRegistration.feature: many registrations of one new machine at the same moment.</summary>
[Binding]
public sealed class MachineRegistrationSteps
{
    private readonly RegistryScenario _scenario;
    private readonly Guid _machineId = Guid.NewGuid();
    private HttpStatusCode[] _statuses = [];

    public MachineRegistrationSteps(RegistryScenario scenario) => _scenario = scenario;

    [When("{word} registers the same new machine {int} times in parallel")]
    public async Task WhenRegistersTheSameNewMachineInParallel(string login, int times)
    {
        var http = _scenario.User(login).Http;

        // Every request is built first and released together, so they reach the registry as close to simultaneously
        // as the in-process server allows, and race to insert the row.
        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, times).Select(i => Task.Run(async () =>
        {
            start.Wait();
            var response = await http.PutAsJsonAsync($"/v1/machines/{_machineId}", new { name = $"laptop-{i}", operatingSystem = "linux" });
            return response.StatusCode;
        })).ToArray();
        start.Set();
        _statuses = await Task.WhenAll(requests);
    }

    [Then("every registration succeeds")]
    public void ThenEveryRegistrationSucceeds() =>
        Assert.All(_statuses, s => Assert.True(s is HttpStatusCode.OK or HttpStatusCode.Created, $"A registration answered {(int)s}."));

    [Then("exactly one registration answered 201 and the rest 200")]
    public void ThenExactlyOneRegistrationAnswered201()
    {
        Assert.Single(_statuses, s => s == HttpStatusCode.Created);
        Assert.Equal(_statuses.Length - 1, _statuses.Count(s => s == HttpStatusCode.OK));
    }

    [Then("{word} has exactly one machine with that id")]
    public async Task ThenHasExactlyOneMachineWithThatId(string login)
    {
        var machines = (await _scenario.User(login).GetJsonAsync("/v1/machines")).AsArray();
        Assert.Equal(_machineId, Assert.Single(machines)!["id"]!.GetValue<Guid>());
    }
}
