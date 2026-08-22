using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractDesiredApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractDesiredApps",
                columns: table => new
                {
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppTemplateId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractDesiredApps", x => new { x.ContractId, x.AppTemplateId });
                    table.ForeignKey(
                        name: "FK_ContractDesiredApps_AppTemplates_AppTemplateId",
                        column: x => x.AppTemplateId,
                        principalTable: "AppTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractDesiredApps_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractDesiredApps_AppTemplateId",
                table: "ContractDesiredApps",
                column: "AppTemplateId");

            // Preserve every existing one-to-many assignment while moving desired-state membership
            // to its own many-to-many join table.
            migrationBuilder.Sql(
                """
                INSERT INTO "ContractDesiredApps" ("ContractId", "AppTemplateId")
                SELECT "ContractId", "Id"
                FROM "AppTemplates"
                WHERE "ContractId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractDesiredApps");
        }
    }
}
