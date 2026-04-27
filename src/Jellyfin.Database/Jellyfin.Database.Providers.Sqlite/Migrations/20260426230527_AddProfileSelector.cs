using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileSelector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileSelectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoSelectSingleProfile = table.Column<bool>(type: "INTEGER", nullable: false),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateModified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSelectors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSelectors_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileSelectorDeviceStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileSelectorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ActiveProfileUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastActivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSelectorDeviceStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSelectorDeviceStates_ProfileSelectors_ProfileSelectorId",
                        column: x => x.ProfileSelectorId,
                        principalTable: "ProfileSelectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileSelectorDeviceStates_Users_ActiveProfileUserId",
                        column: x => x.ActiveProfileUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProfileSelectorMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileSelectorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    PinHash = table.Column<string>(type: "TEXT", maxLength: 65535, nullable: true),
                    FailedPinAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PinLockoutUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFailedPinAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSelectorMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSelectorMembers_ProfileSelectors_ProfileSelectorId",
                        column: x => x.ProfileSelectorId,
                        principalTable: "ProfileSelectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileSelectorMembers_Users_ProfileUserId",
                        column: x => x.ProfileUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorDeviceStates_ActiveProfileUserId",
                table: "ProfileSelectorDeviceStates",
                column: "ActiveProfileUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorDeviceStates_ProfileSelectorId_DeviceId",
                table: "ProfileSelectorDeviceStates",
                columns: new[] { "ProfileSelectorId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorMembers_ProfileSelectorId_ProfileUserId",
                table: "ProfileSelectorMembers",
                columns: new[] { "ProfileSelectorId", "ProfileUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorMembers_ProfileUserId",
                table: "ProfileSelectorMembers",
                column: "ProfileUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectors_OwnerUserId",
                table: "ProfileSelectors",
                column: "OwnerUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileSelectorDeviceStates");

            migrationBuilder.DropTable(
                name: "ProfileSelectorMembers");

            migrationBuilder.DropTable(
                name: "ProfileSelectors");
        }
    }
}
