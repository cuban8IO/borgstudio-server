using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BorgStudio.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevicesKeysAndSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SshPublicAddress",
                table: "ServerSettings",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAccessAt",
                table: "Repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAccessBy",
                table: "Repositories",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAccessUserId",
                table: "Repositories",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PairingRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PairingCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserCode = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ApprovedUserId = table.Column<string>(type: "TEXT", nullable: true),
                    IsDenied = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PairingRequests_AspNetUsers_ApprovedUserId",
                        column: x => x.ApprovedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositorySessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SshKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositorySessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositorySessions_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SshKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Blob = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SshKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SshKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SshKeys_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SshKeys_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "ServerSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SshPublicAddress",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TokenHash",
                table: "Devices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_UserId",
                table: "Devices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PairingRequests_ApprovedUserId",
                table: "PairingRequests",
                column: "ApprovedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PairingRequests_PairingCodeHash",
                table: "PairingRequests",
                column: "PairingCodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PairingRequests_UserCode",
                table: "PairingRequests",
                column: "UserCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySessions_RepositoryId",
                table: "RepositorySessions",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_DeviceId",
                table: "SshKeys",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_Fingerprint",
                table: "SshKeys",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_RepositoryId",
                table: "SshKeys",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_UserId",
                table: "SshKeys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairingRequests");

            migrationBuilder.DropTable(
                name: "RepositorySessions");

            migrationBuilder.DropTable(
                name: "SshKeys");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropColumn(
                name: "SshPublicAddress",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LastAccessAt",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "LastAccessBy",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "LastAccessUserId",
                table: "Repositories");
        }
    }
}
