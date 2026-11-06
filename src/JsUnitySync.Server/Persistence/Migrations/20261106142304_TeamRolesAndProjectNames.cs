using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsUnitySync.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamRolesAndProjectNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_TeamId_Name",
                table: "Projects");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Projects",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE \"Projects\" SET \"NormalizedName\" = upper(\"Name\");");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Memberships",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TeamId_NormalizedName",
                table: "Projects",
                columns: new[] { "TeamId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_TeamId_NormalizedName",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Memberships");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TeamId_Name",
                table: "Projects",
                columns: new[] { "TeamId", "Name" },
                unique: true);
        }
    }
}
