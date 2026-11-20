using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Memory;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Search;
using Protostar.Registry.Api.Sessions;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>
/// The registry database and unit of work. Holds <see cref="User"/> and the <see cref="Skill"/> aggregate
/// (<see cref="Skill"/>, <see cref="SkillVersion"/>, <see cref="SkillFile"/>) with the <see cref="SkillTombstone"/>s of
/// removed skill names, the sessions feature
/// (<see cref="Machine"/>, <see cref="Transcript"/>, <see cref="TranscriptChunk"/>; their bytes live in blob storage), the
/// <see cref="Project"/> aggregate (<see cref="Project"/>, <see cref="ProjectKey"/>), the <see cref="MemoryFile"/> aggregate
/// (<see cref="MemoryFile"/>, <see cref="MemoryFileVersion"/>, <see cref="MemoryFileCopy"/>), the search index's
/// <see cref="SearchEntry"/> rows, plus OpenIddict's own tables
/// (applications, authorizations, scopes, tokens), added to the model by the <c>UseOpenIddict()</c> call
/// configured on the context options. As the infrastructure boundary it applies each feature's
/// <see cref="IEntityTypeConfiguration{TEntity}"/>. Domain events are dispatched by
/// <see cref="DbContextDomainEventExtensions.SaveChangesAndDispatchAsync"/> at the call site, since Aspire
/// pools this context and a pooled context cannot take a scoped dispatcher in its constructor.
/// </summary>
public sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Skill> Skills => Set<Skill>();

    public DbSet<SkillVersion> SkillVersions => Set<SkillVersion>();

    public DbSet<SkillFile> SkillFiles => Set<SkillFile>();

    public DbSet<SkillTombstone> SkillTombstones => Set<SkillTombstone>();

    public DbSet<Machine> Machines => Set<Machine>();

    public DbSet<Transcript> Transcripts => Set<Transcript>();

    public DbSet<TranscriptChunk> TranscriptChunks => Set<TranscriptChunk>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectKey> ProjectKeys => Set<ProjectKey>();

    public DbSet<MemoryFile> MemoryFiles => Set<MemoryFile>();

    public DbSet<MemoryFileVersion> MemoryFileVersions => Set<MemoryFileVersion>();

    public DbSet<MemoryFileCopy> MemoryFileCopies => Set<MemoryFileCopy>();

    public DbSet<SearchEntry> SearchEntries => Set<SearchEntry>();

    /// <summary>Whether the save in progress came from <see cref="SaveChangesRacingAsync"/>.</summary>
    internal bool SavingRacingInsert { get; private set; }

    /// <summary>Saves the unit of work for a caller that handles a unique violation as a lost insert race.</summary>
    /// <remarks>
    /// <see cref="HandledRaceLogging"/> logs that save's unique violation at Debug instead of Error, because the
    /// caller recovers from it. Any other failure of the save, and a unique violation in any other save, still
    /// logs at Error. Use it only around a save whose unique violation the caller catches.
    /// </remarks>
    public async Task<int> SaveChangesRacingAsync(CancellationToken cancellationToken)
    {
        SavingRacingInsert = true;
        try
        {
            return await SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // Aspire pools this context, so the flag must not outlive the save.
            SavingRacingInsert = false;
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Npgsql writes only offset 0 to timestamptz; converting here covers every DateTimeOffset column,
        // nullable ones and complex-type members included, so no feature has to remember it.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Each feature owns its mapping in an IEntityTypeConfiguration co-located with its model.
        builder.ApplyConfigurationsFromAssembly(typeof(RegistryDbContext).Assembly);
    }
}
