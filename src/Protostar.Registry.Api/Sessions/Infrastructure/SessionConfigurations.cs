using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Api.Sessions;

/// <summary>EF mapping for <see cref="Machine"/>.</summary>
public sealed class MachineConfiguration : IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> builder)
    {
        // Client-generated ids are unique per owner, so the key is the pair.
        builder.HasKey(m => new { m.OwnerId, m.Id });
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.OperatingSystem).HasMaxLength(40).IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.OwnerId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>EF mapping for the <see cref="Transcript"/> aggregate root.</summary>
public sealed class TranscriptConfiguration : IEntityTypeConfiguration<Transcript>
{
    public void Configure(EntityTypeBuilder<Transcript> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Harness)
            .HasConversion(h => h.Value, v => HarnessId.FromTrusted(v))
            .HasMaxLength(40)
            .IsRequired();
        builder.Property(t => t.Key)
            .HasConversion(k => k.Value, v => TranscriptKey.FromTrusted(v))
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(t => t.ParentKey)
            .HasConversion(k => k!.Value, v => TranscriptKey.FromTrusted(v))
            .HasMaxLength(200);
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.SourcePath).HasMaxLength(2000).IsRequired();
        builder.Property(t => t.MetadataJson).HasColumnType("jsonb");

        builder.HasIndex(t => new { t.OwnerId, t.Harness, t.Key }).IsUnique();
        builder.HasIndex(t => new { t.OwnerId, t.Harness, t.ParentKey });
        builder.HasIndex(t => new { t.OwnerId, t.Kind });

        builder.HasOne<User>().WithMany().HasForeignKey(t => t.OwnerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Machine>().WithMany().HasForeignKey(t => new { t.OwnerId, t.MachineId }).OnDelete(DeleteBehavior.Restrict);

        builder.ComplexProperty(t => t.Summary, summary =>
        {
            summary.Property(s => s.Title).HasMaxLength(500);
            summary.Property(s => s.FirstPrompt).HasMaxLength(500);
            summary.Property(s => s.WorkingDirectory).HasMaxLength(2000);
            summary.Property(s => s.GitBranch).HasMaxLength(500);
            summary.Property(s => s.HarnessVersion).HasMaxLength(100);
        });

        // Two appends racing for the same offset: the second save fails instead of double-counting.
        // Npgsql maps a uint row version to Postgres's system xmin column (no real column is created).
        builder.Property<uint>("Version").IsRowVersion();
    }
}

/// <summary>EF mapping for <see cref="TranscriptChunk"/>.</summary>
public sealed class TranscriptChunkConfiguration : IEntityTypeConfiguration<TranscriptChunk>
{
    public void Configure(EntityTypeBuilder<TranscriptChunk> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(c => c.BlobKey).HasMaxLength(300).IsRequired();

        // At most one chunk can start at a given offset of a revision.
        builder.HasIndex(c => new { c.TranscriptId, c.Revision, c.Offset }).IsUnique();

        builder.HasOne<Transcript>().WithMany().HasForeignKey(c => c.TranscriptId).OnDelete(DeleteBehavior.Cascade);
    }
}
