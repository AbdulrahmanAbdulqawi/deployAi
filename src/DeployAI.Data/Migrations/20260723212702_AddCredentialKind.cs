using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialKind : Migration
    {
        // This migration also carries the projects/notification_preferences schema that
        // 20260711154500_AddAutoDeployHealthAndNotifications was meant to add. That file was
        // hand-written without a .Designer.cs, so it has no [Migration] attribute, is invisible
        // to the migrator, and has never appeared in __EFMigrationsHistory — its columns were
        // never created anywhere. That dead file has been deleted; these operations replace it.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "provider_credentials",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Deployment");

            migrationBuilder.AddColumn<bool>(
                name: "AutoDeployEnabled",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "GitHubWebhookId",
                table: "projects",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthJson",
                table: "projects",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailOnSuccess = table.Column<bool>(type: "boolean", nullable: false),
                    EmailOnFailure = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_notification_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_projects_GitHubRepoFullName_AutoDeployEnabled",
                table: "projects",
                columns: new[] { "GitHubRepoFullName", "AutoDeployEnabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropIndex(
                name: "IX_projects_GitHubRepoFullName_AutoDeployEnabled",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "AutoDeployEnabled",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "GitHubWebhookId",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "HealthJson",
                table: "projects");
        }
    }
}
