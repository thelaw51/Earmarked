using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Earmarked.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BookId",
                table: "BookMentions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LookupKey",
                table: "BookMentions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchState",
                table: "BookMentions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MatchedAt",
                table: "BookMentions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatcherVersion",
                table: "BookMentions",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Books",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CatalogId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Authors = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FirstPublishYear = table.Column<int>(type: "INTEGER", nullable: true),
                    InfoUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    CoverUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookMentions_BookId",
                table: "BookMentions",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookMentions_LookupKey_MatcherVersion",
                table: "BookMentions",
                columns: new[] { "LookupKey", "MatcherVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_BookMentions_MatcherVersion",
                table: "BookMentions",
                column: "MatcherVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Provider_CatalogId",
                table: "Books",
                columns: new[] { "Provider", "CatalogId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BookMentions_Books_BookId",
                table: "BookMentions",
                column: "BookId",
                principalTable: "Books",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookMentions_Books_BookId",
                table: "BookMentions");

            migrationBuilder.DropTable(
                name: "Books");

            migrationBuilder.DropIndex(
                name: "IX_BookMentions_BookId",
                table: "BookMentions");

            migrationBuilder.DropIndex(
                name: "IX_BookMentions_LookupKey_MatcherVersion",
                table: "BookMentions");

            migrationBuilder.DropIndex(
                name: "IX_BookMentions_MatcherVersion",
                table: "BookMentions");

            migrationBuilder.DropColumn(
                name: "BookId",
                table: "BookMentions");

            migrationBuilder.DropColumn(
                name: "LookupKey",
                table: "BookMentions");

            migrationBuilder.DropColumn(
                name: "MatchState",
                table: "BookMentions");

            migrationBuilder.DropColumn(
                name: "MatchedAt",
                table: "BookMentions");

            migrationBuilder.DropColumn(
                name: "MatcherVersion",
                table: "BookMentions");
        }
    }
}
