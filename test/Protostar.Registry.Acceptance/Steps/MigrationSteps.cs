using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Protostar.Registry.Acceptance.Support;
using Protostar.Registry.Api.Infrastructure;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for Migrations.feature: the registry's first start against a database nothing has touched.</summary>
[Binding]
public sealed class MigrationSteps
{
    private readonly RegistryScenario _scenario;

    public MigrationSteps(RegistryScenario scenario) => _scenario = scenario;

    [Given("an empty database")]
    public async Task GivenAnEmptyDatabase()
    {
        await _scenario.CreateDatabaseAsync();
        Assert.Equal(0, await _scenario.CountTablesAsync());
    }

    [When("the registry starts against it")]
    public Task WhenTheRegistryStartsAgainstIt() => _scenario.StartAsync();

    [Then("every migration the registry ships is applied")]
    public async Task ThenEveryMigrationIsApplied()
    {
        await using var scope = _scenario.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
        var shipped = db.Database.GetMigrations().ToList();
        Assert.NotEmpty(shipped);
        Assert.Equal(shipped, await db.Database.GetAppliedMigrationsAsync());
    }

    [Then("the model has no changes that lack a migration")]
    public async Task ThenTheModelHasNoChangesThatLackAMigration()
    {
        await using var scope = _scenario.Factory.Services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<RegistryDbContext>().Database.HasPendingModelChanges(),
            "The model has changed since the last migration: add one with dotnet ef migrations add.");
    }

    [Then("the registry reports itself healthy")]
    public async Task ThenTheRegistryReportsItselfHealthy()
    {
        using var http = _scenario.CreateAnonymousClient();
        var response = await http.GetAsync("/health");
        Assert.True(response.IsSuccessStatusCode, $"/health answered {(int)response.StatusCode}.");
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
