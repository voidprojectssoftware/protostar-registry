using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentTombstones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    LastVersion = table.Column<int>(type: "integer", nullable: false),
                    RenamedTo = table.Column<string>(type: "text", nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTombstones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTombstones_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentTombstones_Users_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CarriedByFolderKey = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Agents_Users_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    ToolsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    PushedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentVersions_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentVersions_Users_PushedById",
                        column: x => x.PushedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_CreatorId_ProjectId_Name",
                table: "Agents",
                columns: new[] { "CreatorId", "ProjectId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_CurrentVersionId",
                table: "Agents",
                column: "CurrentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ProjectId",
                table: "Agents",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTombstones_CreatorId_ProjectId_Name",
                table: "AgentTombstones",
                columns: new[] { "CreatorId", "ProjectId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTombstones_ProjectId",
                table: "AgentTombstones",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentVersions_AgentId_VersionNumber",
                table: "AgentVersions",
                columns: new[] { "AgentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentVersions_PushedById",
                table: "AgentVersions",
                column: "PushedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_AgentVersions_CurrentVersionId",
                table: "Agents",
                column: "CurrentVersionId",
                principalTable: "AgentVersions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_AgentVersions_CurrentVersionId",
                table: "Agents");

            migrationBuilder.DropTable(
                name: "AgentTombstones");

            migrationBuilder.DropTable(
                name: "AgentVersions");

            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
