using System.Text.Json;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Api.Agents;

/// <summary>One immutable push of an <see cref="Agent"/>: its file, verbatim, and the metadata read from its front matter.</summary>
/// <remarks>
/// An agent is one file, so the file lives on the version row (ADR 0003): there is no files table. <see cref="ToolsJson"/>
/// is a <c>jsonb</c> column the registry stores for later readers and does not query into.
/// </remarks>
public sealed class AgentVersion
{
    private AgentVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid AgentId { get; private set; }

    /// <summary>Monotonic version number within the agent.</summary>
    public int VersionNumber { get; private set; }

    /// <summary>When Claude Code should use the agent (front-matter <c>description</c>).</summary>
    public string Description { get; private set; } = default!;

    /// <summary>Front-matter <c>tools</c> as a JSON array; <c>null</c> when absent.</summary>
    public string? ToolsJson { get; private set; }

    /// <summary>Front-matter <c>model</c>; <c>null</c> when absent.</summary>
    public string? Model { get; private set; }

    /// <summary>The file's path relative to the agents folder, with <c>/</c> separators (an agent may sit in a subfolder).</summary>
    public RelativePath Path { get; private set; } = default!;

    /// <summary>The file's bytes, verbatim.</summary>
    public byte[] Content { get; private set; } = default!;

    /// <summary>The file's size in bytes.</summary>
    public long Size { get; private set; }

    /// <summary>SHA-256 of <see cref="Content"/> alone, as uppercase hex; identical re-pushes share it.</summary>
    public Sha256Hash ContentHash { get; private set; } = default!;

    /// <summary>The user who pushed this version.</summary>
    public Guid PushedById { get; private set; }

    public User PushedBy { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    internal static AgentVersion Create(
        Guid agentId, int versionNumber, AgentManifest manifest, RelativePath path, byte[] content, Sha256Hash contentHash, Guid pushedBy, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            VersionNumber = versionNumber,
            Description = manifest.Description,
            ToolsJson = manifest.Tools.Count > 0 ? JsonSerializer.Serialize(manifest.Tools) : null,
            Model = manifest.Model,
            Path = path,
            Content = (byte[])content.Clone(),
            Size = content.LongLength,
            ContentHash = contentHash,
            PushedById = pushedBy,
            CreatedAt = now,
        };
}
