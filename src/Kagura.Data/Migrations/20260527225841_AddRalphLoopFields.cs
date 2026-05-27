using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kagura.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRalphLoopFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RalphLoopActive",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RalphLoopHaltReason",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastFailureReason",
                table: "AgentTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryAttempts",
                table: "AgentTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RalphLoopActive",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "RalphLoopHaltReason",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "LastFailureReason",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "RetryAttempts",
                table: "AgentTasks");
        }
    }
}
