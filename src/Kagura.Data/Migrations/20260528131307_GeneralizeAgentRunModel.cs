using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kagura.Data.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeAgentRunModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_AgentTasks_AgentTaskId",
                table: "AgentRuns");

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
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill WorkItemId from the linked AgentTask. Existing rows are all task-agent
            // runs (Kind=0 by default), so the join is total over non-null AgentTaskId.
            migrationBuilder.Sql(@"
                UPDATE AgentRuns
                SET WorkItemId = (
                    SELECT t.WorkItemId FROM AgentTasks t WHERE t.Id = AgentRuns.AgentTaskId
                )
                WHERE AgentTaskId IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_Kind",
                table: "AgentRuns",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_WorkItemId",
                table: "AgentRuns",
                column: "WorkItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_AgentTasks_AgentTaskId",
                table: "AgentRuns",
                column: "AgentTaskId",
                principalTable: "AgentTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

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
                name: "FK_AgentRuns_AgentTasks_AgentTaskId",
                table: "AgentRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_WorkItems_WorkItemId",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_Kind",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_WorkItemId",
                table: "AgentRuns");

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

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_AgentTasks_AgentTaskId",
                table: "AgentRuns",
                column: "AgentTaskId",
                principalTable: "AgentTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
