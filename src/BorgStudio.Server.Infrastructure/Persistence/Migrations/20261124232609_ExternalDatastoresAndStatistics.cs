using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BorgStudio.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalDatastoresAndStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "By",
                table: "RepositorySessions",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "UsedBefore",
                table: "RepositorySessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "RepositorySessions",
                type: "TEXT",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "FolderById",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FreeBytes",
                table: "Datastores",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "Datastores",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostKey",
                table: "Datastores",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Datastores",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "Datastores",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Port",
                table: "Datastores",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteBorg",
                table: "Datastores",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SpaceMeasuredAt",
                table: "Datastores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalBytes",
                table: "Datastores",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                table: "Datastores",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    ArchiveName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OriginalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CompressedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DeduplicatedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Files = table.Column<long>(type: "INTEGER", nullable: true),
                    Archives = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalOriginalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalCompressedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    UniqueCompressedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesReceived = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesSent = table.Column<long>(type: "INTEGER", nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    UsedBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedAfter = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Subject = table.Column<int>(type: "INTEGER", nullable: false),
                    SubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UsedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    FreeBytes = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageSamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                    CpuPercentPeak = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryUsedPeak = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryTotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskReadBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskWrittenBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkReceivedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkSentBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskReadPeak = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskWrittenPeak = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkReceivedPeak = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkSentPeak = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppReports_RepositoryId_StartedAt",
                table: "AppReports",
                columns: new[] { "RepositoryId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionRecords_RepositoryId_StartedAt",
                table: "ConnectionRecords",
                columns: new[] { "RepositoryId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionRecords_StartedAt",
                table: "ConnectionRecords",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StorageSamples_Subject_SubjectId_Day",
                table: "StorageSamples",
                columns: new[] { "Subject", "SubjectId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemSamples_Resolution_At",
                table: "SystemSamples",
                columns: new[] { "Resolution", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppReports");

            migrationBuilder.DropTable(
                name: "ConnectionRecords");

            migrationBuilder.DropTable(
                name: "StorageSamples");

            migrationBuilder.DropTable(
                name: "SystemSamples");

            migrationBuilder.DropColumn(
                name: "By",
                table: "RepositorySessions");

            migrationBuilder.DropColumn(
                name: "UsedBefore",
                table: "RepositorySessions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "RepositorySessions");

            migrationBuilder.DropColumn(
                name: "FolderById",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "FreeBytes",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "Host",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "HostKey",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "Port",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "RemoteBorg",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "SpaceMeasuredAt",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "TotalBytes",
                table: "Datastores");

            migrationBuilder.DropColumn(
                name: "UserName",
                table: "Datastores");
        }
    }
}
