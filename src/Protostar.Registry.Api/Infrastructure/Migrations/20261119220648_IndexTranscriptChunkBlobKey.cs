using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class IndexTranscriptChunkBlobKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TranscriptChunks_BlobKey",
                table: "TranscriptChunks",
                column: "BlobKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TranscriptChunks_BlobKey",
                table: "TranscriptChunks");
        }
    }
}
