using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BorgStudio.Server.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// BorgStudio starts dark. Until 0.1.1 new accounts got "follow the system" as an unchosen default;
    /// those accounts switch to dark once (ThemeMode: 0 = System, 2 = Dark).
    /// </summary>
    public partial class DarkThemeByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"ThemeMode\" = 2 WHERE \"ThemeMode\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Which accounts had chosen dark themselves is not known; nothing to undo.
        }
    }
}
