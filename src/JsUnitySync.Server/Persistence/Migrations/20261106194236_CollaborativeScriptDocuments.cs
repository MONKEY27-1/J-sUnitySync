using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsUnitySync.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CollaborativeScriptDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScriptDocuments",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedById = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptDocuments", x => new { x.ProjectId, x.Path });
                    table.ForeignKey(
                        name: "FK_ScriptDocuments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScriptDocuments");
        }
    }
}
