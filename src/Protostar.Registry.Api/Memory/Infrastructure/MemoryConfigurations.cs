using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Memory;

/// <summary>EF mapping for the <see cref="MemoryFile"/> aggregate root.</summary>
public sealed class MemoryFileConfiguration : IEntityTypeConfiguration<MemoryFile>
{
    public void Configure(EntityTypeBuilder<MemoryFile> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Path)
            .HasConversion(path => path.Value, value => RelativePath.FromTrusted(value))
            .HasMaxLength(MemoryLimits.MaxPathLength)
            .IsRequired();
        builder.HasIndex(f => new { f.OwnerId, f.ProjectId, f.Path }).IsUnique();

        // A project with memory is never deleted from under it: forgetting an empty folder project checks for memory first.
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(f => f.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(f => f.Versions)
            .WithOne()
            .HasForeignKey(v => v.MemoryFileId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(f => f.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(f => f.Copies)
            .WithOne()
            .HasForeignKey(c => c.MemoryFileId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(f => f.Copies).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>EF mapping for <see cref="MemoryFileVersion"/>, a child of the memory file aggregate.</summary>
public sealed class MemoryFileVersionConfiguration : IEntityTypeConfiguration<MemoryFileVersion>
{
    public void Configure(EntityTypeBuilder<MemoryFileVersion> builder)
    {
        builder.HasKey(v => v.Id);

        // The aggregate sets ids itself. Without this, a version added to a loaded file is taken for an existing row
        // (its key is already set), updated instead of inserted, and the save fails.
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.HasIndex(v => new { v.MemoryFileId, v.Number }).IsUnique();
        builder.Property(v => v.Content).IsRequired();
        builder.Property(v => v.Sha256)
            .HasConversion(hash => hash.Value, value => Sha256Hash.FromTrusted(value))
            .IsRequired();
    }
}

/// <summary>EF mapping for <see cref="MemoryFileCopy"/>, a child of the memory file aggregate.</summary>
public sealed class MemoryFileCopyConfiguration : IEntityTypeConfiguration<MemoryFileCopy>
{
    public void Configure(EntityTypeBuilder<MemoryFileCopy> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Folder).HasMaxLength(MemoryLimits.MaxFolderLength).IsRequired();
        builder.HasIndex(c => new { c.MemoryFileId, c.MachineId, c.Folder }).IsUnique();
    }
}
