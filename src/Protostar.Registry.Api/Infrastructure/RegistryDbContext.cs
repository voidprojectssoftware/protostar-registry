using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>
/// The registry database and unit of work. Holds <see cref="User"/> plus OpenIddict's own tables
/// (applications, authorizations, scopes, tokens), added to the model by the <c>UseOpenIddict()</c> call
/// configured on the context options. As the infrastructure boundary it applies each feature's
/// <see cref="IEntityTypeConfiguration{TEntity}"/>.
/// </summary>
public sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Each feature owns its mapping in an IEntityTypeConfiguration co-located with its model.
        builder.ApplyConfigurationsFromAssembly(typeof(RegistryDbContext).Assembly);
    }
}
