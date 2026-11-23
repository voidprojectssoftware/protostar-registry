using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class SkillTombstoneVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Versions",
                table: "SkillTombstones",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            // A rename tombstone from before this column still has its versions, on the skill it names: fill them in, so a
            // copy of a skill renamed before this release is recognised too. A deleted skill's versions are gone, so its
            // tombstone stays empty. The hashes are what a skill version stores (uppercase hex), in the list's camelCase.
            migrationBuilder.Sql(
                """
                UPDATE "SkillTombstones" AS t
                SET "Versions" = v.versions
                FROM (
                    SELECT t2."Id" AS id,
                           jsonb_agg(jsonb_build_object('number', sv."VersionNumber", 'contentHash', sv."ContentHash") ORDER BY sv."VersionNumber") AS versions
                    FROM "SkillTombstones" AS t2
                    JOIN "Skills" AS s
                      ON s."CreatorId" = t2."CreatorId" AND s."ProjectId" IS NOT DISTINCT FROM t2."ProjectId" AND s."Name" = t2."RenamedTo"
                    JOIN "SkillVersions" AS sv
                      ON sv."SkillId" = s."Id" AND sv."VersionNumber" <= t2."LastVersion"
                    WHERE t2."RenamedTo" IS NOT NULL
                    GROUP BY t2."Id"
                ) AS v
                WHERE t."Id" = v.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Versions",
                table: "SkillTombstones");
        }
    }
}
