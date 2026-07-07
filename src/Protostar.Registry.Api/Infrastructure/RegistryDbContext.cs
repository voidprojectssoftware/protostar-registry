using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>
/// The registry database and unit of work. Holds <see cref="User"/> and the <see cref="Skill"/> aggregate
/// (<see cref="Skill"/>, <see cref="SkillVersion"/>, <see cref="SkillFile"/>) plus OpenIddict's own tables
/// (applications, authorizations, scopes, tokens), added to the model by the <c>UseOpenIddict()</c> call
/// configured on the context options. As the infrastructure boundary it applies each feature's
/// <see cref="IEntityTypeConfiguration{TEntity}"/>. Domain events are dispatched by
/// <see cref="DbContextDomainEventExtensions.SaveChangesAndDispatchAsync"/> at the call site, since Aspire
/// pools this context and a pooled context cannot take a scoped dispatcher in its constructor.
/// </summary>
public sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Skill> Skills => Set<Skill>();

    public DbSet<SkillVersion> SkillVersions => Set<SkillVersion>();

    public DbSet<SkillFile> SkillFiles => Set<SkillFile>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Each feature owns its mapping in an IEntityTypeConfiguration co-located with its model.
        builder.ApplyConfigurationsFromAssembly(typeof(RegistryDbContext).Assembly);
    }
}
