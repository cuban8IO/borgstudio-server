using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BorgStudio.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CheckForUpdates",
                table: "ServerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ChangelogSeenVersion",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ServerSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "CheckForUpdates",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckForUpdates",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "ChangelogSeenVersion",
                table: "AspNetUsers");
        }
    }
}
