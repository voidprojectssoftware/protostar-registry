using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transcripts_OwnerId_MachineId",
                table: "Transcripts");

            migrationBuilder.AddColumn<string>(
                name: "ProjectFactsJson",
                table: "Transcripts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectFolder",
                table: "Transcripts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "Transcripts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectSubpath",
                table: "Transcripts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectKeys_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_MachineId_ProjectFolder",
                table: "Transcripts",
                columns: new[] { "OwnerId", "MachineId", "ProjectFolder" });

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_ProjectId",
                table: "Transcripts",
                columns: new[] { "OwnerId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_ProjectId",
                table: "Transcripts",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectKeys_OwnerId_Kind_Key",
                table: "ProjectKeys",
                columns: new[] { "OwnerId", "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectKeys_ProjectId",
                table: "ProjectKeys",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OwnerId",
                table: "Projects",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transcripts_Projects_ProjectId",
                table: "Transcripts",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Transcripts backed up before projects existed carry no facts, only the working directory their lines
            // recorded. File each under a machine-and-folder project for that directory (ADR 0002's fallback), the
            // same as ProjectIdentity.FromFolder would, until a later registration brings git facts.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE folder_projects ON COMMIT DROP AS
                SELECT gen_random_uuid() AS id, d."OwnerId" AS owner_id, d."MachineId" AS machine_id, d."Summary_WorkingDirectory" AS folder
                FROM (SELECT DISTINCT "OwnerId", "MachineId", "Summary_WorkingDirectory"
                      FROM "Transcripts" WHERE "Summary_WorkingDirectory" IS NOT NULL) d;

                INSERT INTO "Projects" ("Id", "OwnerId", "Name", "CreatedAt", "UpdatedAt")
                SELECT id, owner_id,
                       left(coalesce(nullif(regexp_replace(rtrim(folder, '/\'), '^.*[/\\]', ''), ''), folder), 500),
                       now(), now()
                FROM folder_projects;

                INSERT INTO "ProjectKeys" ("Id", "ProjectId", "OwnerId", "Kind", "Key", "CreatedAt")
                SELECT gen_random_uuid(), id, owner_id, 'Folder', machine_id::text || ':' || folder, now()
                FROM folder_projects;

                UPDATE "Transcripts" t
                SET "ProjectId" = f.id, "ProjectFolder" = f.folder
                FROM folder_projects f
                WHERE t."OwnerId" = f.owner_id AND t."MachineId" = f.machine_id AND t."Summary_WorkingDirectory" = f.folder;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transcripts_Projects_ProjectId",
                table: "Transcripts");

            migrationBuilder.DropTable(
                name: "ProjectKeys");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Transcripts_OwnerId_MachineId_ProjectFolder",
                table: "Transcripts");

            migrationBuilder.DropIndex(
                name: "IX_Transcripts_OwnerId_ProjectId",
                table: "Transcripts");

            migrationBuilder.DropIndex(
                name: "IX_Transcripts_ProjectId",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "ProjectFactsJson",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "ProjectFolder",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "ProjectSubpath",
                table: "Transcripts");

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_MachineId",
                table: "Transcripts",
                columns: new[] { "OwnerId", "MachineId" });
        }
    }
}
