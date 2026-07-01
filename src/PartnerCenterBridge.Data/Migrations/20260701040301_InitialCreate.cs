using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Secrets",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    ProtectedValue = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Secrets", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "AppTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Publisher = table.Column<string>(type: "text", nullable: true),
                    InstallCommandLine = table.Column<string>(type: "text", nullable: false),
                    UninstallCommandLine = table.Column<string>(type: "text", nullable: false),
                    ContentVersion = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Assignments = table.Column<string>(type: "jsonb", nullable: true),
                    Content = table.Column<string>(type: "jsonb", nullable: true),
                    DetectionRules = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppTemplates_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    DefaultDomain = table.Column<string>(type: "text", nullable: true),
                    GdapRelationshipId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tenants_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Deployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntuneAppId = table.Column<string>(type: "text", nullable: true),
                    CommittedContentVersionId = table.Column<string>(type: "text", nullable: true),
                    DeployedTemplateVersion = table.Column<int>(type: "integer", nullable: false),
                    AssignmentIds = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deployments_AppTemplates_AppTemplateId",
                        column: x => x.AppTemplateId,
                        principalTable: "AppTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deployments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppTemplates_ContractId",
                table: "AppTemplates",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_AppTemplateId",
                table: "Deployments",
                column: "AppTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_TenantId_AppTemplateId",
                table: "Deployments",
                columns: new[] { "TenantId", "AppTemplateId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_ContractId",
                table: "Tenants",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_TenantId",
                table: "Tenants",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deployments");

            migrationBuilder.DropTable(
                name: "Secrets");

            migrationBuilder.DropTable(
                name: "AppTemplates");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "Contracts");
        }
    }
}
