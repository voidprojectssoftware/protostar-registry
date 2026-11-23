using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Agents;

/// <summary>EF mapping for <see cref="AgentTombstone"/>.</summary>
public sealed class AgentTombstoneConfiguration : IEntityTypeConfiguration<AgentTombstone>
{
    public void Configure(EntityTypeBuilder<AgentTombstone> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired();

        // One tombstone per removed name in a scope, keyed exactly as agents are (a null project is one value).
        builder.HasIndex(t => new { t.CreatorId, t.ProjectId, t.Name }).IsUnique().AreNullsDistinct(false);

        // A tombstone only matters while its scope exists, so it never holds a project or a user in place.
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(t => t.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.CreatorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
