using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDelegatedInstanceRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AuthorizationVersion",
                table: "AppUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<int>(
                name: "InstanceRoles",
                table: "AppUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "UPDATE \"AppUsers\" SET \"InstanceRoles\" = 1 WHERE \"IsSystemAdmin\" = TRUE;");

            migrationBuilder.DropColumn(
                name: "IsSystemAdmin",
                table: "AppUsers");

            migrationBuilder.CreateTable(
                name: "InstanceAuthorizationStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceAuthorizationStates", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "InstanceAuthorizationStates",
                columns: new[] { "Id", "Revision" },
                values: new object[] { 1, 1L });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceAuthorizationStates");

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemAdmin",
                table: "AppUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE \"AppUsers\" SET \"IsSystemAdmin\" = TRUE WHERE (\"InstanceRoles\" & 1) = 1;");

            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "InstanceRoles",
                table: "AppUsers");
        }
    }
}
