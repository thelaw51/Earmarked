using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastBookRecs.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Count",
                table: "WorkerCommands",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Count",
                table: "WorkerCommands");
        }
    }
}
