using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class OperationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "WorkflowRuns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "WorkflowRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDisplayName",
                table: "WorkflowRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetId",
                table: "WorkflowRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OffboardingPolicy",
                table: "Contracts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_TenantId_TargetId_StartedAt",
                table: "WorkflowRuns",
                columns: new[] { "TenantId", "TargetId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_TenantId_TargetId_StartedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "TargetDisplayName",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "OffboardingPolicy",
                table: "Contracts");
        }
    }
}
