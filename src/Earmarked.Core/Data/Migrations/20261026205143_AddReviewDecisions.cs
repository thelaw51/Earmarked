using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Earmarked.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LookupKey",
                table: "WorkerCommands",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Doubts",
                table: "BookMentions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ReviewDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LookupKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Verdict = table.Column<int>(type: "INTEGER", nullable: false),
                    Pick = table.Column<string>(type: "TEXT", nullable: true),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewDecisions_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewDecisions_BookId",
                table: "ReviewDecisions",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewDecisions_LookupKey",
                table: "ReviewDecisions",
                column: "LookupKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewDecisions");

            migrationBuilder.DropColumn(
                name: "LookupKey",
                table: "WorkerCommands");

            migrationBuilder.DropColumn(
                name: "Doubts",
                table: "BookMentions");
        }
    }
}
