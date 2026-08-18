using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectVerificationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_check_states",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeployTargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SuggestedAction = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastConclusiveStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastConclusiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FirstObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    ConsecutiveInconclusive = table.Column<int>(type: "integer", nullable: false),
                    LastNotifiedStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_check_states", x => new { x.ProjectId, x.CheckId });
                    table.ForeignKey(
                        name: "FK_project_check_states_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_verification_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SweepErrored = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    PassedChecks = table.Column<int>(type: "integer", nullable: false),
                    FailedChecks = table.Column<int>(type: "integer", nullable: false),
                    WarningChecks = table.Column<int>(type: "integer", nullable: false),
                    SkippedChecks = table.Column<int>(type: "integer", nullable: false),
                    InconclusiveChecks = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_verification_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_verification_runs_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_verification_check_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeployTargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SuggestedAction = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_verification_check_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_verification_check_results_project_verification_run~",
                        column: x => x.RunId,
                        principalTable: "project_verification_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_check_states_ProjectId",
                table: "project_check_states",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_check_results_ProjectId_CheckId_Observ~",
                table: "project_verification_check_results",
                columns: new[] { "ProjectId", "CheckId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_check_results_RunId",
                table: "project_verification_check_results",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_runs_ProjectId_StartedAt",
                table: "project_verification_runs",
                columns: new[] { "ProjectId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_check_states");

            migrationBuilder.DropTable(
                name: "project_verification_check_results");

            migrationBuilder.DropTable(
                name: "project_verification_runs");
        }
    }
}
