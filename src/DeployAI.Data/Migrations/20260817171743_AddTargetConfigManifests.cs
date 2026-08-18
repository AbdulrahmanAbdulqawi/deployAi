using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetConfigManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "target_config_manifests",
                columns: table => new
                {
                    DeployTargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RequiredKeysJson = table.Column<string>(type: "text", nullable: false),
                    ValueFingerprintsJson = table.Column<string>(type: "text", nullable: false),
                    WasInconclusive = table.Column<bool>(type: "boolean", nullable: false),
                    InconclusiveReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_target_config_manifests", x => x.DeployTargetId);
                    table.ForeignKey(
                        name: "FK_target_config_manifests_deploy_targets_DeployTargetId",
                        column: x => x.DeployTargetId,
                        principalTable: "deploy_targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_target_config_manifests_ProjectId",
                table: "target_config_manifests",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "target_config_manifests");
        }
    }
}
