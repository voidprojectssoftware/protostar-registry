using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Protostar.Registry.Tests;

/// <summary>
/// Boots the API in the "Testing" environment: the client-seeding hosted service and the
/// startup migration are skipped (no live database), and a placeholder connection string keeps
/// DbContext registration happy. The covered endpoints don't touch the database, so no Postgres
/// is required. Database-backed acceptance scenarios are the Reqnroll black-box layer (PROT-41).
/// </summary>
public sealed class RegistryApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting(
            "ConnectionStrings:registrydb",
            "Host=localhost;Port=5432;Database=registry_test;Username=postgres;Password=postgres");
    }
}
