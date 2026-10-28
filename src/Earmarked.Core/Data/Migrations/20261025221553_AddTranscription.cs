using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Earmarked.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "Transcripts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Transcripts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublishesTranscripts",
                table: "PodcastFeeds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Feeds that already have episodes with transcript links get the late-transcript grace period straight away.
            migrationBuilder.Sql(
                "UPDATE PodcastFeeds SET PublishesTranscripts = 1 WHERE EXISTS " +
                "(SELECT 1 FROM Episodes WHERE Episodes.PodcastFeedId = PodcastFeeds.Id AND Episodes.TranscriptLinks <> '[]');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Model",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Transcripts");

            migrationBuilder.DropColumn(
                name: "PublishesTranscripts",
                table: "PodcastFeeds");
        }
    }
}
