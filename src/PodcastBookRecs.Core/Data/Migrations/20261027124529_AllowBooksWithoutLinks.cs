using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastBookRecs.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowBooksWithoutLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "InfoUrl",
                table: "Books",
                type: "TEXT",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 300);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "InfoUrl",
                table: "Books",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 300,
                oldNullable: true);
        }
    }
}
