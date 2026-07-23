using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialKind : Migration
    {
        // Scope note: EF also generated operations for the projects/notification_preferences
        // changes from 20260711154500_AddAutoDeployHealthAndNotifications, because that
        // migration was hand-written without a .Designer.cs and so never reached the model
        // snapshot. Those operations were removed here — that migration already applies them,
        // and duplicating them would fail with "column already exists". The snapshot is
        // correct as of this migration.

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "provider_credentials");
        }
    }
}
