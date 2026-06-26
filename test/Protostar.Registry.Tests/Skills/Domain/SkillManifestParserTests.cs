using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Skills.Domain;

/// <summary>
/// Black-box contract tests for <see cref="SkillManifestParser"/>, derived from its documented contract
/// (the public surface and doc comments) rather than its implementation.
/// </summary>
/// <remarks>
/// Covers the documented behavior plus the parser's resolved edge cases: it is lenient on input shape
/// (tolerates a leading BOM or blank lines before the opening fence, a `...` document-end closing fence,
/// and `allowed-tools` given as a single scalar or a comma-separated string) but returns null for anything
/// it cannot read as a YAML mapping (no/empty/unclosed front matter, malformed YAML). A blank field value
/// is treated as absent, and non-scalar `metadata` entries are skipped.
/// </remarks>
public sealed class SkillManifestParserTests
{
    // A well-formed SKILL.md with every recognized field populated. Keys sit at column 0 so the leading
    // `---` opens a real YAML front-matter mapping.
    private static string FullManifest() =>
        """
        ---
        name: pdf-tools
        description: Tools for working with PDF files.
        license: MIT
        compatibility: claude-code >= 1.0
        metadata:
          author: blake
          category: documents
        allowed-tools:
          - Bash
          - Read
        ---

        # PDF Tools

        Body content that is not part of the front matter.
        """;

    [Fact]
    public void Parse_returns_a_non_null_manifest_for_a_well_formed_front_matter_block()
    {
        var result = SkillManifestParser.Parse(FullManifest());

        Assert.NotNull(result);
    }

    [Fact]
    public void Parse_reads_the_name_field()
    {
        var result = SkillManifestParser.Parse(FullManifest());

        Assert.Equal("pdf-tools", result!.Name);
    }

    [Fact]
    public void Parse_reads_the_description_field()
    {
        var result = SkillManifestParser.Parse(FullManifest());

        Assert.Equal("Tools for working with PDF files.", result!.Description);
    }

    [Fact]
    public void Parse_reads_the_license_field()
    {
        var result = SkillManifestParser.Parse(FullManifest());

        Assert.Equal("MIT", result!.License);
    }

    [Fact]
    public void Parse_reads_the_compatibility_field()
    {
        var result = SkillManifestParser.Parse(FullManifest());

        Assert.Equal("claude-code >= 1.0", result!.Compatibility);
    }

    [Fact]
    public void Parse_reads_the_metadata_mapping_entries()
    {
        var result = SkillManifestParser.Parse(FullManifest());

        Assert.Equal("blake", Assert.Contains("author", result!.Metadata));
        Assert.Equal("documents", Assert.Contains("category", result.Metadata));
    }

    [Fact]
    public void Parse_reads_the_allowed_tools_list_entries()
    {
        var result = SkillManifestParser.Parse(FullManifest());

        Assert.Equal(new[] { "Bash", "Read" }, result!.AllowedTools);
    }

    [Fact]
    public void Parse_returns_null_without_a_leading_front_matter_block()
    {
        // No leading `---` line, so there is no front-matter block to extract.
        var markdown =
            """
            # PDF Tools

            name: pdf-tools

            Body content only, no front matter.
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_ignores_unrecognized_keys_while_reading_the_recognized_ones()
    {
        // `homepage` and `version` are not recognized front-matter keys; they must be ignored, and the
        // recognized `name` must still be read.
        var markdown =
            """
            ---
            name: pdf-tools
            homepage: https://example.com
            version: 2
            ---

            # PDF Tools
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.NotNull(result);
        Assert.Equal("pdf-tools", result!.Name);
    }

    [Fact]
    public void Parse_leaves_absent_string_fields_null()
    {
        // Only `name` is present; the other recognized string fields are absent and must be left null.
        var markdown =
            """
            ---
            name: pdf-tools
            ---

            # PDF Tools
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.NotNull(result);
        Assert.Null(result!.Description);
        Assert.Null(result.License);
        Assert.Null(result.Compatibility);
    }

    [Fact]
    public void Parse_leaves_absent_metadata_empty()
    {
        var markdown =
            """
            ---
            name: pdf-tools
            ---

            # PDF Tools
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.NotNull(result);
        Assert.Empty(result!.Metadata);
    }

    [Fact]
    public void Parse_leaves_absent_allowed_tools_empty()
    {
        var markdown =
            """
            ---
            name: pdf-tools
            ---

            # PDF Tools
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.NotNull(result);
        Assert.Empty(result!.AllowedTools);
    }

    // --- Resolved edge cases ---

    [Fact]
    public void Parse_returns_null_for_an_unclosed_front_matter_block()
    {
        // An opening `---` with no closing fence before end of document is not a valid block.
        var markdown =
            """
            ---
            name: pdf-tools
            description: no closing fence follows
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_when_the_front_matter_is_malformed_yaml()
    {
        // The fences are present, but the content between them is not valid YAML (unterminated quote).
        var markdown = "---\nname: \"unterminated\n---\n# Body\n";

        var result = SkillManifestParser.Parse(markdown);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_accepts_a_yaml_document_end_marker_as_the_closing_fence()
    {
        var markdown =
            """
            ---
            name: pdf-tools
            ...

            # Body
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.Equal("pdf-tools", result!.Name);
    }

    [Fact]
    public void Parse_tolerates_a_leading_byte_order_mark()
    {
        var markdown = "﻿---\nname: pdf-tools\n---\n# Body\n";

        var result = SkillManifestParser.Parse(markdown);

        Assert.Equal("pdf-tools", result!.Name);
    }

    [Fact]
    public void Parse_tolerates_blank_lines_before_the_opening_fence()
    {
        var markdown = "\n\n---\nname: pdf-tools\n---\n# Body\n";

        var result = SkillManifestParser.Parse(markdown);

        Assert.Equal("pdf-tools", result!.Name);
    }

    [Fact]
    public void Parse_accepts_allowed_tools_as_a_single_scalar()
    {
        var markdown =
            """
            ---
            name: pdf-tools
            allowed-tools: Bash
            ---
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.Equal(new[] { "Bash" }, result!.AllowedTools);
    }

    [Fact]
    public void Parse_accepts_allowed_tools_as_a_comma_separated_string()
    {
        var markdown =
            """
            ---
            name: pdf-tools
            allowed-tools: Bash, Read
            ---
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.Equal(new[] { "Bash", "Read" }, result!.AllowedTools);
    }

    [Fact]
    public void Parse_treats_a_blank_field_value_as_absent()
    {
        // `name:` carries no value; a blank value is treated the same as absent -> null.
        var markdown =
            """
            ---
            name:
            description: present
            ---
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.NotNull(result);
        Assert.Null(result!.Name);
    }

    [Fact]
    public void Parse_skips_metadata_entries_whose_value_is_not_a_scalar()
    {
        // `nested` has a mapping value, not a scalar; it is dropped while scalar entries are kept.
        var markdown =
            """
            ---
            name: pdf-tools
            metadata:
              category: documents
              nested:
                a: b
            ---
            """;

        var result = SkillManifestParser.Parse(markdown);

        Assert.Equal("documents", Assert.Contains("category", result!.Metadata));
        Assert.DoesNotContain("nested", result.Metadata);
    }

    [Fact]
    public void Parse_returns_null_for_empty_input()
    {
        var result = SkillManifestParser.Parse("");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_for_an_empty_front_matter_block()
    {
        // A fence pair with no mapping between them is not a YAML mapping -> null.
        var markdown = "---\n---\n# Body\n";

        var result = SkillManifestParser.Parse(markdown);

        Assert.Null(result);
    }
}
