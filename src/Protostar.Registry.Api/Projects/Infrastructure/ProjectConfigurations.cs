using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Api.Projects;

/// <summary>EF mapping for the <see cref="Project"/> aggregate root.</summary>
public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(Project.MaxNameLength).IsRequired();
        builder.HasIndex(p => p.OwnerId);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Keys).WithOne().HasForeignKey(k => k.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Keys).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>EF mapping for <see cref="ProjectKey"/>.</summary>
public sealed class ProjectKeyConfiguration : IEntityTypeConfiguration<ProjectKey>
{
    public void Configure(EntityTypeBuilder<ProjectKey> builder)
    {
        builder.HasKey(k => k.Id);

        // The domain assigns the id. Without this, EF takes a key added to an already loaded project for an
        // existing row and issues an UPDATE that matches nothing.
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(k => k.Key).HasMaxLength(ProjectKey.MaxLength).IsRequired();

        // A key resolves to at most one of an owner's projects. This is what makes concurrent resolution safe.
        builder.HasIndex(k => new { k.OwnerId, k.Kind, k.Key }).IsUnique();
    }
}
