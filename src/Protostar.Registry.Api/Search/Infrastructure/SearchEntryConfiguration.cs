using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using Protostar.Registry.Api.Agents;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Memory;
using Protostar.Registry.Api.Sessions;

namespace Protostar.Registry.Api.Search;

/// <summary>EF mapping for <see cref="SearchEntry"/>, with its full-text column and GIN index.</summary>
public sealed class SearchEntryConfiguration : IEntityTypeConfiguration<SearchEntry>
{
    /// <summary>The stored <c>tsvector</c> column, generated from <see cref="SearchEntry.Text"/>; queried by name in SQL.</summary>
    public const string SearchVector = "SearchVector";

    public void Configure(EntityTypeBuilder<SearchEntry> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Text).IsRequired();

        // A shadow property: the domain never reads the vector, only the search query does. Postgres keeps it current.
        builder.Property<NpgsqlTsVector>(SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql("to_tsvector('english', \"Text\")", stored: true);
        builder.HasIndex(SearchVector).HasMethod("GIN");

        builder.HasIndex(e => e.OwnerId);
        builder.HasIndex(e => new { e.ChunkId, e.Segment }).IsUnique();
        builder.HasIndex(e => new { e.MemoryFileId, e.Segment }).IsUnique();
        builder.HasIndex(e => new { e.AgentId, e.Segment }).IsUnique();
        builder.HasIndex(e => e.TranscriptId);

        // Entries go with what they were made from.
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.OwnerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Transcript>().WithMany().HasForeignKey(e => e.TranscriptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TranscriptChunk>().WithMany().HasForeignKey(e => e.ChunkId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MemoryFile>().WithMany().HasForeignKey(e => e.MemoryFileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Agent>().WithMany().HasForeignKey(e => e.AgentId).OnDelete(DeleteBehavior.Cascade);
    }
}
