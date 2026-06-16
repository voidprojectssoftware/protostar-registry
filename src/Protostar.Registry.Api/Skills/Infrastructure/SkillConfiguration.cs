using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Protostar.Registry.Api.Skills;

/// <summary>EF mapping for the <see cref="Skill"/> aggregate root.</summary>
public sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired();
        builder.HasIndex(s => new { s.CreatorId, s.Name }).IsUnique();

        builder.HasOne(s => s.Creator)
            .WithMany()
            .HasForeignKey(s => s.CreatorId)
            .OnDelete(DeleteBehavior.Cascade);

        // The current-version pointer is a non-cascading reference into the skill's own versions;
        // the versions themselves cascade from the skill.
        builder.HasOne(s => s.CurrentVersion)
            .WithMany()
            .HasForeignKey(s => s.CurrentVersionId)
            .OnDelete(DeleteBehavior.NoAction);

        // Children inside the aggregate carry the FK but no back-reference navigation; the root is the
        // only entry point. The collection is read-only over a backing field.
        builder.HasMany(s => s.Versions)
            .WithOne()
            .HasForeignKey(v => v.SkillId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>EF mapping for <see cref="SkillVersion"/>, a child of the skill aggregate.</summary>
public sealed class SkillVersionConfiguration : IEntityTypeConfiguration<SkillVersion>
{
    public void Configure(EntityTypeBuilder<SkillVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.HasIndex(v => new { v.SkillId, v.VersionNumber }).IsUnique();

        builder.Property(v => v.ContentHash)
            .HasConversion(hash => hash.Value, value => Sha256Hash.FromTrusted(value))
            .IsRequired();
        builder.Property(v => v.MetadataJson).HasColumnType("jsonb");
        builder.Property(v => v.AllowedToolsJson).HasColumnType("jsonb");

        builder.HasOne(v => v.PushedBy)
            .WithMany()
            .HasForeignKey(v => v.PushedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(v => v.Files)
            .WithOne()
            .HasForeignKey(f => f.SkillVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(v => v.Files).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>EF mapping for <see cref="SkillFile"/>, a child of the skill aggregate.</summary>
public sealed class SkillFileConfiguration : IEntityTypeConfiguration<SkillFile>
{
    public void Configure(EntityTypeBuilder<SkillFile> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.RelativePath)
            .HasConversion(path => path.Value, value => RelativePath.FromTrusted(value))
            .IsRequired();
        builder.Property(f => f.Content).IsRequired();
        builder.Property(f => f.Sha256)
            .HasConversion(hash => hash.Value, value => Sha256Hash.FromTrusted(value))
            .IsRequired();
        builder.HasIndex(f => new { f.SkillVersionId, f.RelativePath }).IsUnique();
    }
}
