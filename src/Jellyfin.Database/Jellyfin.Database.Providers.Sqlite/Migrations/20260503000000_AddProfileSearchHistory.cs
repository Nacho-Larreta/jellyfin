using System;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(JellyfinDbContext))]
    [Migration("20260503000000_AddProfileSearchHistory")]
    public partial class AddProfileSearchHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileSearchHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SearchTerm = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SearchTermNormalized = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    HitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DateCreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSearchedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSearchHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSearchHistoryEntries_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileSearchHistoryEntries_Users_ProfileUserId",
                        column: x => x.ProfileUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSearchHistoryEntries_OwnerUserId_ProfileUserId_LastSearchedUtc",
                table: "ProfileSearchHistoryEntries",
                columns: new[] { "OwnerUserId", "ProfileUserId", "LastSearchedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSearchHistoryEntries_OwnerUserId_ProfileUserId_SearchTermNormalized",
                table: "ProfileSearchHistoryEntries",
                columns: new[] { "OwnerUserId", "ProfileUserId", "SearchTermNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSearchHistoryEntries_ProfileUserId",
                table: "ProfileSearchHistoryEntries",
                column: "ProfileUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileSearchHistoryEntries");
        }
    }
}
