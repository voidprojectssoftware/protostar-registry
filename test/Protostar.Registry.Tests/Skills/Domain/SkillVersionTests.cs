using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain;

/// <summary>
/// Black-box contract tests for <see cref="SkillVersion"/>. A version is obtained only by pushing
/// through the <see cref="Skill"/> aggregate and reading <c>outcome.Version</c>. Every expectation is
/// derived from the documented public surface, not the implementation.
/// </summary>
public sealed class SkillVersionTests
{
    private static SkillFileContent FileAt(string path, string text) =>
        SkillFileContent.Create(RelativePath.FromTrusted(path), Encoding.UTF8.GetBytes(text));

    private static Skill NewSkill(Guid creator) =>
        Skill.Create(creator, "demo", DateTimeOffset.UtcNow);

    private static IReadOnlyList<SkillFileContent> DefaultFiles() => new[]
    {
        FileAt("SKILL.md", "# demo"),
        FileAt("a/helper.py", "print(1)"),
    };

    private static SkillManifest FullManifest() => new()
    {
        Description = "does a thing",
        License = "MIT",
        Compatibility = "claude-code",
        Metadata = new Dictionary<string, string> { ["category"] = "demo" },
        AllowedTools = new[] { "Bash", "Read" },
    };

    // --- Description / License / Compatibility: present ---

    [Fact]
    public void Version_carries_the_manifest_description()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, FullManifest(), DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Equal("does a thing", version.Description);
    }

    [Fact]
    public void Version_carries_the_manifest_license()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, FullManifest(), DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Equal("MIT", version.License);
    }

    [Fact]
    public void Version_carries_the_manifest_compatibility()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, FullManifest(), DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Equal("claude-code", version.Compatibility);
    }

    // --- Description / License / Compatibility: omitted -> null ---

    [Fact]
    public void Version_description_is_null_when_manifest_omits_it()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, SkillManifest.Empty, DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Null(version.Description);
    }

    [Fact]
    public void Version_license_is_null_when_manifest_omits_it()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, SkillManifest.Empty, DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Null(version.License);
    }

    [Fact]
    public void Version_compatibility_is_null_when_manifest_omits_it()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, SkillManifest.Empty, DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Null(version.Compatibility);
    }

    // --- MetadataJson ---

    [Fact]
    public void Version_metadata_json_round_trips_the_manifest_metadata()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var manifest = SkillManifest.Empty with
        {
            Metadata = new Dictionary<string, string> { ["category"] = "demo", ["team"] = "core" },
        };

        var version = skill.PushVersion(creator, manifest, DefaultFiles(), DateTimeOffset.UtcNow).Version;

        // ASSUMES: MetadataJson is a JSON object keyed by the metadata keys; shape not pinned, parsed back.
        Assert.NotNull(version.MetadataJson);
        using var doc = JsonDocument.Parse(version.MetadataJson!);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("demo", doc.RootElement.GetProperty("category").GetString());
        Assert.Equal("core", doc.RootElement.GetProperty("team").GetString());
    }

    [Fact]
    public void Version_metadata_json_is_null_when_manifest_metadata_is_empty()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        // SkillManifest.Empty has empty Metadata by default.
        var version = skill.PushVersion(creator, SkillManifest.Empty, DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Null(version.MetadataJson);
    }

    // --- AllowedToolsJson ---

    [Fact]
    public void Version_allowed_tools_json_round_trips_the_manifest_allowed_tools()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var manifest = SkillManifest.Empty with { AllowedTools = new[] { "Bash", "Read" } };

        var version = skill.PushVersion(creator, manifest, DefaultFiles(), DateTimeOffset.UtcNow).Version;

        // ASSUMES: AllowedToolsJson is a JSON array of the tool names; shape not pinned, parsed back.
        Assert.NotNull(version.AllowedToolsJson);
        using var doc = JsonDocument.Parse(version.AllowedToolsJson!);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var tools = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Bash", "Read" }, tools);
    }

    [Fact]
    public void Version_allowed_tools_json_is_null_when_manifest_allowed_tools_is_empty()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        // SkillManifest.Empty has empty AllowedTools by default.
        var version = skill.PushVersion(creator, SkillManifest.Empty, DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Null(version.AllowedToolsJson);
    }

    // --- Numbering / identity / provenance ---

    [Fact]
    public void Version_number_is_one_for_the_first_push()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, FullManifest(), DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Equal(1, version.VersionNumber);
    }

    [Fact]
    public void Version_pushed_by_id_is_the_creator_who_pushed()
    {
        // Only the creator may push (a non-creator throws), so the version's pusher is the creator.
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, FullManifest(), DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Equal(creator, version.PushedById);
    }

    [Fact]
    public void Version_skill_id_is_the_skills_id()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);

        var version = skill.PushVersion(creator, FullManifest(), DefaultFiles(), DateTimeOffset.UtcNow).Version;

        Assert.Equal(skill.Id, version.SkillId);
    }

    // --- CreatedAt ---

    [Fact]
    public void Version_created_at_is_the_supplied_timestamp()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var when = new DateTimeOffset(2026, 6, 16, 8, 30, 0, TimeSpan.Zero);

        var version = skill.PushVersion(creator, FullManifest(), DefaultFiles(), when).Version;

        Assert.Equal(when, version.CreatedAt);
    }

    // --- ContentHash ---

    [Fact]
    public void Version_content_hash_equals_sha256_over_the_input_files()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var files = DefaultFiles();

        var version = skill.PushVersion(creator, FullManifest(), files, DateTimeOffset.UtcNow).Version;

        var expected = Sha256Hash.OfFiles(files);
        Assert.Equal(expected.Value, version.ContentHash.Value);
    }

    // --- Files: count and per-file contents (order-independent) ---

    [Fact]
    public void Version_has_one_file_per_input_file()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var files = DefaultFiles();

        var version = skill.PushVersion(creator, FullManifest(), files, DateTimeOffset.UtcNow).Version;

        Assert.Equal(files.Count, version.Files.Count);
    }

    [Fact]
    public void Version_captures_each_input_path()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var files = DefaultFiles();

        var version = skill.PushVersion(creator, FullManifest(), files, DateTimeOffset.UtcNow).Version;

        // ASSUMES: file order is unspecified by the contract, so match by path rather than position.
        var paths = version.Files.Select(f => f.RelativePath.Value).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "SKILL.md", "a/helper.py" }, paths);
    }

    [Fact]
    public void Version_file_content_bytes_match_the_input()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var files = DefaultFiles();

        var version = skill.PushVersion(creator, FullManifest(), files, DateTimeOffset.UtcNow).Version;

        var skillMd = version.Files.Single(f => f.RelativePath.Value == "SKILL.md");
        Assert.Equal(Encoding.UTF8.GetBytes("# demo"), skillMd.Content);
    }

    [Fact]
    public void Version_file_size_matches_the_input_byte_length()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var files = DefaultFiles();

        var version = skill.PushVersion(creator, FullManifest(), files, DateTimeOffset.UtcNow).Version;

        var helper = version.Files.Single(f => f.RelativePath.Value == "a/helper.py");
        Assert.Equal(Encoding.UTF8.GetByteCount("print(1)"), helper.Size);
    }

    [Fact]
    public void Version_file_sha256_matches_a_single_file_hash_of_that_input()
    {
        var creator = Guid.NewGuid();
        var skill = NewSkill(creator);
        var bytes = Encoding.UTF8.GetBytes("# demo");
        var files = new[] { FileAt("SKILL.md", "# demo") };

        var version = skill.PushVersion(creator, FullManifest(), files, DateTimeOffset.UtcNow).Version;

        // A SkillFile's Sha256 is the hash of its own content bytes (not the composite OfFiles digest).
        var captured = Assert.Single(version.Files);
        var expected = Sha256Hash.Of(bytes);
        Assert.Equal(expected.Value, captured.Sha256.Value);
    }
}
