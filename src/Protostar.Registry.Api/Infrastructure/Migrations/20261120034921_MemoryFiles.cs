using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class MemoryFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryFiles_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemoryFileCopies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Folder = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryFileCopies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryFileCopies_MemoryFiles_MemoryFileId",
                        column: x => x.MemoryFileId,
                        principalTable: "MemoryFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryFileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryFileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryFileVersions_MemoryFiles_MemoryFileId",
                        column: x => x.MemoryFileId,
                        principalTable: "MemoryFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryFileCopies_MemoryFileId_MachineId_Folder",
                table: "MemoryFileCopies",
                columns: new[] { "MemoryFileId", "MachineId", "Folder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryFiles_OwnerId_ProjectId_Path",
                table: "MemoryFiles",
                columns: new[] { "OwnerId", "ProjectId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryFiles_ProjectId",
                table: "MemoryFiles",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryFileVersions_MemoryFileId_Number",
                table: "MemoryFileVersions",
                columns: new[] { "MemoryFileId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryFileCopies");

            migrationBuilder.DropTable(
                name: "MemoryFileVersions");

            migrationBuilder.DropTable(
                name: "MemoryFiles");
        }
    }
}
