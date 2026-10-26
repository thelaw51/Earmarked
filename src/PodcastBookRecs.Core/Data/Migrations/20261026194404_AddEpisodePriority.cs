using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastBookRecs.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodePriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PrioritisedAt",
                table: "Episodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrioritisedAt",
                table: "Episodes");
        }
    }
}
