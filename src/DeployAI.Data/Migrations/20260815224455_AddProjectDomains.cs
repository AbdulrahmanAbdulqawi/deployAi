using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeployTargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    DisplayHostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    ExpectedAddress = table.Column<string>(type: "text", nullable: true),
                    DnsCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    ZoneId = table.Column<string>(type: "text", nullable: true),
                    ManagedRecordId = table.Column<string>(type: "text", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastConclusiveStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObservationsJson = table.Column<string>(type: "text", nullable: true),
                    CertificateIssuer = table.Column<string>(type: "text", nullable: true),
                    CertificateNotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StatusMessage = table.Column<string>(type: "text", nullable: false),
                    RoutingDeployTriggeredForAttempt = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_domains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_domains_deploy_targets_DeployTargetId",
                        column: x => x.DeployTargetId,
                        principalTable: "deploy_targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_domains_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_domains_DeployTargetId_Hostname",
                table: "project_domains",
                columns: new[] { "DeployTargetId", "Hostname" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_domains_ProjectId",
                table: "project_domains",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_domains");
        }
    }
}
