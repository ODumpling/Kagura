using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kagura.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRalphLoopConfigFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveTriage",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoReviewEnabled",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "RalphLoopWaitingReason",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoApproveTriage",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "AutoReviewEnabled",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "RalphLoopWaitingReason",
                table: "WorkItems");
        }
    }
}
