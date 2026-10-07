using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class TenantAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantAuditRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AuditName = table.Column<string>(type: "TEXT", nullable: false),
                    Operator = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    EngineVersion = table.Column<string>(type: "TEXT", nullable: false),
                    Health = table.Column<string>(type: "TEXT", nullable: false),
                    ChecksRequested = table.Column<int>(type: "INTEGER", nullable: false),
                    ChecksCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    ChecksUnavailable = table.Column<int>(type: "INTEGER", nullable: false),
                    ChecksErrored = table.Column<int>(type: "INTEGER", nullable: false),
                    FailCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarnCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnknownCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InfoCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PassCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AffectedSubjects = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAuditRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantAuditRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAuditRuns_BatchId",
                table: "TenantAuditRuns",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAuditRuns_TenantId_StartedAt",
                table: "TenantAuditRuns",
                columns: new[] { "TenantId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantAuditRuns");
        }
    }
}
