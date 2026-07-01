using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProvisioningTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProvisioningTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsageLocation = table.Column<string>(type: "text", nullable: false),
                    UpnDomain = table.Column<string>(type: "text", nullable: true),
                    DefaultJobTitle = table.Column<string>(type: "text", nullable: true),
                    DefaultDepartment = table.Column<string>(type: "text", nullable: true),
                    LicenseSkuIds = table.Column<string>(type: "jsonb", nullable: false),
                    GroupIds = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisioningTemplates_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningTemplates_ContractId",
                table: "ProvisioningTemplates",
                column: "ContractId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisioningTemplates");
        }
    }
}
