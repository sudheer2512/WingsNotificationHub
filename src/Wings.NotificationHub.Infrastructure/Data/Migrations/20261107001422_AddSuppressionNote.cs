using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wings.NotificationHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuppressionNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Suppressions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Note",
                table: "Suppressions");
        }
    }
}
