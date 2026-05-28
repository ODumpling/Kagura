using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kagura.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRunKindAndTriageError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastTriageError",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "AgentTaskId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkItemId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_WorkItemId",
                table: "AgentRuns",
                column: "WorkItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_WorkItems_WorkItemId",
                table: "AgentRuns",
                column: "WorkItemId",
                principalTable: "WorkItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_WorkItems_WorkItemId",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_WorkItemId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "LastTriageError",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "WorkItemId",
                table: "AgentRuns");

            migrationBuilder.AlterColumn<Guid>(
                name: "AgentTaskId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
