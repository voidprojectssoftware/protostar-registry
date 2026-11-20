using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptFoldedKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FoldedKey",
                table: "Transcripts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                computedColumnSql: "lower(\"Key\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_OwnerId_FoldedKey",
                table: "Transcripts",
                columns: new[] { "OwnerId", "FoldedKey" })
                .Annotation("Npgsql:IndexOperators", new[] { null, "text_pattern_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transcripts_OwnerId_FoldedKey",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "FoldedKey",
                table: "Transcripts");
        }
    }
}
