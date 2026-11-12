using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Machines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OperatingSystem = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Machines", x => new { x.OwnerId, x.Id });
                    table.ForeignKey(
                        name: "FK_Machines_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transcripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Harness = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ParentKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    Summary_AssistantMessages = table.Column<int>(type: "integer", nullable: false),
                    Summary_FirstPrompt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Summary_GitBranch = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Summary_HarnessVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Summary_LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Summary_Lines = table.Column<int>(type: "integer", nullable: false),
                    Summary_MalformedLines = table.Column<int>(type: "integer", nullable: false),
                    Summary_StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Summary_Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Summary_UserMessages = table.Column<int>(type: "integer", nullable: false),
                    Summary_WorkingDirectory = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transcripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transcripts_Machines_OwnerId_MachineId",
                        columns: x => new { x.OwnerId, x.MachineId },
                        principalTable: "Machines",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transcripts_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TranscriptChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TranscriptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    Length = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    BlobKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StoredBytes = table.Column<int>(type: "integer", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscriptChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranscriptChunks_Transcripts_TranscriptId",
                        column: x => x.TranscriptId,
                        principalTable: "Transcripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptChunks_TranscriptId_Revision_Offset",
                table: "TranscriptChunks",
                columns: new[] { "TranscriptId", "Revision", "Offset" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_Harness_Key",
                table: "Transcripts",
                columns: new[] { "OwnerId", "Harness", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_Harness_ParentKey",
                table: "Transcripts",
                columns: new[] { "OwnerId", "Harness", "ParentKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_Kind",
                table: "Transcripts",
                columns: new[] { "OwnerId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_MachineId",
                table: "Transcripts",
                columns: new[] { "OwnerId", "MachineId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TranscriptChunks");

            migrationBuilder.DropTable(
                name: "Transcripts");

            migrationBuilder.DropTable(
                name: "Machines");
        }
    }
}
