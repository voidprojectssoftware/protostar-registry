using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Protostar.Registry.Api.Identity;

/// <summary>EF mapping for the <see cref="User"/> entity.</summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);
        builder.HasIndex(u => u.GitHubId).IsUnique();
        builder.Property(u => u.GitHubId).IsRequired();
        builder.Property(u => u.Login).IsRequired();
    }
}
