using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class SearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TranscriptId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChunkId = table.Column<Guid>(type: "uuid", nullable: true),
                    MemoryFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    MemoryFileUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Segment = table.Column<int>(type: "integer", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    IndexVersion = table.Column<int>(type: "integer", nullable: false),
                    IndexedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('english', \"Text\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SearchEntries_MemoryFiles_MemoryFileId",
                        column: x => x.MemoryFileId,
                        principalTable: "MemoryFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SearchEntries_TranscriptChunks_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "TranscriptChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SearchEntries_Transcripts_TranscriptId",
                        column: x => x.TranscriptId,
                        principalTable: "Transcripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SearchEntries_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchEntries_ChunkId_Segment",
                table: "SearchEntries",
                columns: new[] { "ChunkId", "Segment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchEntries_MemoryFileId_Segment",
                table: "SearchEntries",
                columns: new[] { "MemoryFileId", "Segment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchEntries_OwnerId",
                table: "SearchEntries",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchEntries_SearchVector",
                table: "SearchEntries",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_SearchEntries_TranscriptId",
                table: "SearchEntries",
                column: "TranscriptId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchEntries");
        }
    }
}
