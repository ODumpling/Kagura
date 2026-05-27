using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kagura.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewNotes",
                table: "AgentTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewNotes",
                table: "AgentTasks");
        }
    }
}
