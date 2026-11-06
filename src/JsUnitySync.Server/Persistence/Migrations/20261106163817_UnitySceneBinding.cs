using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsUnitySync.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnitySceneBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SceneGuid",
                table: "CollaborationSessions",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SceneGuid",
                table: "CollaborationSessions");
        }
    }
}
