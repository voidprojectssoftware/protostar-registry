using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct the context without the
/// Aspire-injected connection string. Migrations are generated, not executed, against this
/// connection, so a placeholder local Postgres string is sufficient.
/// </summary>
public sealed class RegistryDbContextFactory : IDesignTimeDbContextFactory<RegistryDbContext>
{
    public RegistryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=registrydb;Username=postgres;Password=postgres")
            .UseOpenIddict()
            .Options;

        return new RegistryDbContext(options);
    }
}
