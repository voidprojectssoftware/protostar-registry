using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Protostar.Registry.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionAgentsAndAgentSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Summary_Agents",
                table: "Transcripts",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AlterColumn<Guid>(
                name: "MachineId",
                table: "SearchEntries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "SearchEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentVersionId",
                table: "SearchEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchEntries_AgentId_Segment",
                table: "SearchEntries",
                columns: new[] { "AgentId", "Segment" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SearchEntries_Agents_AgentId",
                table: "SearchEntries",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SearchEntries_Agents_AgentId",
                table: "SearchEntries");

            migrationBuilder.DropIndex(
                name: "IX_SearchEntries_AgentId_Segment",
                table: "SearchEntries");

            migrationBuilder.DropColumn(
                name: "Summary_Agents",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "SearchEntries");

            migrationBuilder.DropColumn(
                name: "AgentVersionId",
                table: "SearchEntries");

            migrationBuilder.AlterColumn<Guid>(
                name: "MachineId",
                table: "SearchEntries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
