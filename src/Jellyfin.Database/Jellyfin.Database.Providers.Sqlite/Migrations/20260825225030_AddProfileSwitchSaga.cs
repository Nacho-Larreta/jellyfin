using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Server.Implementations.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileSwitchSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "RowVersion",
                table: "ProfileSelectors",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "RowVersion",
                table: "ProfileSelectorMembers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "RowVersion",
                table: "ProfileSelectorDeviceStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileSwitchId",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProfileSelectorSwitchOperations",
                columns: table => new
                {
                    SwitchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileSelectorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CallerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CallerCredentialRecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetProfileUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ActiveDeviceId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Client = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RemoteEndPoint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PinProofHash = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthenticationDeviceRecordId = table.Column<int>(type: "INTEGER", nullable: true),
                    DateCreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PreparedExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetainUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSelectorSwitchOperations", x => x.SwitchId);
                    table.ForeignKey(
                        name: "FK_ProfileSelectorSwitchOperations_ProfileSelectors_ProfileSelectorId",
                        column: x => x.ProfileSelectorId,
                        principalTable: "ProfileSelectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileSelectorPlaybackStopReports",
                columns: table => new
                {
                    ReportKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SwitchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CallerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    PlaySessionId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    DateCreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSelectorPlaybackStopReports", x => x.ReportKey);
                    table.ForeignKey(
                        name: "FK_ProfileSelectorPlaybackStopReports_ProfileSelectorSwitchOperations_SwitchId",
                        column: x => x.SwitchId,
                        principalTable: "ProfileSelectorSwitchOperations",
                        principalColumn: "SwitchId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ProfileSwitchId",
                table: "Devices",
                column: "ProfileSwitchId",
                unique: true,
                filter: "ProfileSwitchId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorPlaybackStopReports_SwitchId",
                table: "ProfileSelectorPlaybackStopReports",
                column: "SwitchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorSwitchOperations_CallerUserId_DeviceId_RetainUntilUtc",
                table: "ProfileSelectorSwitchOperations",
                columns: new[] { "CallerUserId", "DeviceId", "RetainUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorSwitchOperations_ProfileSelectorId_ActiveDeviceId",
                table: "ProfileSelectorSwitchOperations",
                columns: new[] { "ProfileSelectorId", "ActiveDeviceId" },
                unique: true,
                filter: "ActiveDeviceId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSelectorSwitchOperations_RetainUntilUtc",
                table: "ProfileSelectorSwitchOperations",
                column: "RetainUntilUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileSelectorPlaybackStopReports");

            migrationBuilder.DropTable(
                name: "ProfileSelectorSwitchOperations");

            migrationBuilder.DropIndex(
                name: "IX_Devices_ProfileSwitchId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ProfileSelectors");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ProfileSelectorMembers");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ProfileSelectorDeviceStates");

            migrationBuilder.DropColumn(
                name: "ProfileSwitchId",
                table: "Devices");
        }
    }
}
