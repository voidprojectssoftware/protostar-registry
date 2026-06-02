using Microsoft.EntityFrameworkCore;

namespace Protostar.Registry.Api.Data;

/// <summary>
/// The registry database. Holds <see cref="User"/> records plus OpenIddict's own tables
/// (applications, authorizations, scopes, tokens), which are added to the model by the
/// <c>UseOpenIddict()</c> call configured on the context options.
/// </summary>
public sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.GitHubId).IsUnique();
            entity.Property(u => u.GitHubId).IsRequired();
            entity.Property(u => u.Login).IsRequired();
        });
    }
}
