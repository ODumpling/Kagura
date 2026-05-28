using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kagura.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGrillFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GrillStatus",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OriginalBody",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkItemComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemComments_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_WorkItemId_CreatedAt",
                table: "WorkItemComments",
                columns: new[] { "WorkItemId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemComments");

            migrationBuilder.DropColumn(
                name: "GrillStatus",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "OriginalBody",
                table: "WorkItems");
        }
    }
}
