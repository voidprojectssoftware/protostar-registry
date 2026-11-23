namespace Protostar.Registry.Api.Agents;

/// <summary>The fields the registry reads from a custom agent's front matter (Claude Code's sub-agent schema).</summary>
/// <remarks>
/// A value object. Only what the registry lists or keeps as metadata is read; every other key (<c>permissionMode</c>,
/// <c>hooks</c>, <c>mcpServers</c> and the rest) stays in the stored bytes, which are kept verbatim.
/// </remarks>
public sealed record AgentManifest
{
    /// <summary>The agent's identifier (<c>name</c>), which is its identity: the file name does not have to match.</summary>
    public required string Name { get; init; }

    /// <summary>When Claude Code should delegate to the agent (<c>description</c>).</summary>
    public required string Description { get; init; }

    /// <summary>The tools the agent may use (<c>tools</c>); empty when absent, which means every tool.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>The model the agent runs on (<c>model</c>); <c>null</c> when absent.</summary>
    public string? Model { get; init; }
}
