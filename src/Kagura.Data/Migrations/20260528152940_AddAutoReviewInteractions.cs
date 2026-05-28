using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kagura.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoReviewInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutoReviewInteractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Response = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoReviewInteractions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoReviewInteractions_AgentRuns_AgentRunId",
                        column: x => x.AgentRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutoReviewInteractions_AgentTasks_AgentTaskId",
                        column: x => x.AgentTaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AutoReviewInteractions_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutoReviewInteractions_AgentRunId_Sequence",
                table: "AutoReviewInteractions",
                columns: new[] { "AgentRunId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AutoReviewInteractions_AgentTaskId",
                table: "AutoReviewInteractions",
                column: "AgentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AutoReviewInteractions_WorkItemId",
                table: "AutoReviewInteractions",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoReviewInteractions");
        }
    }
}
