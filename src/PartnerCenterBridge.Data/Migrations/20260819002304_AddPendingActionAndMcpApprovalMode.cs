using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingActionAndMcpApprovalMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "McpApprovalMode",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PendingActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "text", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    PreviewSummary = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutionError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingActions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingActions_TenantId_Status",
                table: "PendingActions",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingActions");

            migrationBuilder.DropColumn(
                name: "McpApprovalMode",
                table: "Tenants");
        }
    }
}
