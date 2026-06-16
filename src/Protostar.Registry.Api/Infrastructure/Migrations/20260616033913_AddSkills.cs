using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SkillFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativePath = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Skills_Users_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SkillVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    License = table.Column<string>(type: "text", nullable: true),
                    Compatibility = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    AllowedToolsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    PushedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkillVersions_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SkillVersions_Users_PushedById",
                        column: x => x.PushedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkillFiles_SkillVersionId_RelativePath",
                table: "SkillFiles",
                columns: new[] { "SkillVersionId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_CreatorId_Name",
                table: "Skills",
                columns: new[] { "CreatorId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_CurrentVersionId",
                table: "Skills",
                column: "CurrentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SkillVersions_PushedById",
                table: "SkillVersions",
                column: "PushedById");

            migrationBuilder.CreateIndex(
                name: "IX_SkillVersions_SkillId_VersionNumber",
                table: "SkillVersions",
                columns: new[] { "SkillId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SkillFiles_SkillVersions_SkillVersionId",
                table: "SkillFiles",
                column: "SkillVersionId",
                principalTable: "SkillVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Skills_SkillVersions_CurrentVersionId",
                table: "Skills",
                column: "CurrentVersionId",
                principalTable: "SkillVersions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Skills_SkillVersions_CurrentVersionId",
                table: "Skills");

            migrationBuilder.DropTable(
                name: "SkillFiles");

            migrationBuilder.DropTable(
                name: "SkillVersions");

            migrationBuilder.DropTable(
                name: "Skills");
        }
    }
}
