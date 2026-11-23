using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Agents;

/// <summary>EF mapping for the <see cref="Agent"/> aggregate root.</summary>
/// <remarks>Mirrors <see cref="SkillConfiguration"/>; see there for why each rule is as it is.</remarks>
public sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).IsRequired();
        builder.Property(a => a.CarriedByFolderKey).HasMaxLength(ProjectKey.MaxLength);

        // One agent per (creator, project, name); a null project (a personal agent) is one value, not distinct.
        builder.HasIndex(a => new { a.CreatorId, a.ProjectId, a.Name }).IsUnique().AreNullsDistinct(false);

        // A project with agents is never deleted from under them (ProjectService.ForgetFolderProjectsAsync checks first).
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Creator)
            .WithMany()
            .HasForeignKey(a => a.CreatorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.CurrentVersion)
            .WithMany()
            .HasForeignKey(a => a.CurrentVersionId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(a => a.Versions)
            .WithOne()
            .HasForeignKey(v => v.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(a => a.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>EF mapping for <see cref="AgentVersion"/>, a child of the agent aggregate.</summary>
public sealed class AgentVersionConfiguration : IEntityTypeConfiguration<AgentVersion>
{
    public void Configure(EntityTypeBuilder<AgentVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.HasIndex(v => new { v.AgentId, v.VersionNumber }).IsUnique();

        builder.Property(v => v.Description).IsRequired();
        builder.Property(v => v.ToolsJson).HasColumnType("jsonb");
        builder.Property(v => v.Path)
            .HasConversion(path => path.Value, value => RelativePath.FromTrusted(value))
            .IsRequired();
        builder.Property(v => v.Content).IsRequired();
        builder.Property(v => v.ContentHash)
            .HasConversion(hash => hash.Value, value => Sha256Hash.FromTrusted(value))
            .IsRequired();

        builder.HasOne(v => v.PushedBy)
            .WithMany()
            .HasForeignKey(v => v.PushedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
