using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Projects;

namespace Protostar.Registry.Api.Skills;

/// <summary>EF mapping for <see cref="SkillTombstone"/>.</summary>
public sealed class SkillTombstoneConfiguration : IEntityTypeConfiguration<SkillTombstone>
{
    private static readonly JsonSerializerOptions VersionsJson = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<SkillTombstone> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired();

        // A short list written once with the tombstone and read whole with the skill list, never queried into, so one
        // jsonb column rather than a table.
        builder.Property(t => t.Versions)
            .HasConversion(
                versions => JsonSerializer.Serialize(versions, VersionsJson),
                json => JsonSerializer.Deserialize<List<RemovedSkillVersion>>(json, VersionsJson) ?? new List<RemovedSkillVersion>(),
                new ValueComparer<IReadOnlyList<RemovedSkillVersion>>(
                    (a, b) => a!.SequenceEqual(b!),
                    versions => versions.Aggregate(0, (hash, version) => HashCode.Combine(hash, version)),
                    versions => versions.ToList()))
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb")
            .IsRequired();

        // One tombstone per removed name in a scope, keyed exactly as skills are (a null project is one value).
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
