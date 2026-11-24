using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BorgStudio.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepositoriesAndDatastores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Datastores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Datastores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserStoragePolicies",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    MayCreateRepositories = table.Column<bool>(type: "INTEGER", nullable: false),
                    QuotaBytes = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserStoragePolicies", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserStoragePolicies_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: true),
                    FormerOwnerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DatastoreId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    QuotaBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UsedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UsageMeasuredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MoveTargetDatastoreId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MoveError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Repositories_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Repositories_Datastores_DatastoreId",
                        column: x => x.DatastoreId,
                        principalTable: "Datastores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryShares",
                columns: table => new
                {
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Access = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryShares", x => new { x.RepositoryId, x.UserId });
                    table.ForeignKey(
                        name: "FK_RepositoryShares_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RepositoryShares_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Datastores_Name",
                table: "Datastores",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Datastores_Path",
                table: "Datastores",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_DatastoreId",
                table: "Repositories",
                column: "DatastoreId");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_OwnerId",
                table: "Repositories",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryShares_UserId",
                table: "RepositoryShares",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryShares");

            migrationBuilder.DropTable(
                name: "UserStoragePolicies");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropTable(
                name: "Datastores");
        }
    }
}
