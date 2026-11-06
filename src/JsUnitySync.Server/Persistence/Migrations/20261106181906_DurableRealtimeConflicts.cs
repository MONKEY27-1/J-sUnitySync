using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsUnitySync.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableRealtimeConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptedById",
                table: "SessionProperties",
                maxLength: 26,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "SessionConflicts",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    SessionId = table.Column<string>(nullable: false),
                    RejectedEventId = table.Column<string>(maxLength: 26, nullable: false),
                    AcceptedById = table.Column<string>(maxLength: 26, nullable: false),
                    RejectedById = table.Column<string>(maxLength: 26, nullable: false),
                    ObjectId = table.Column<string>(maxLength: 26, nullable: false),
                    ComponentType = table.Column<string>(maxLength: 128, nullable: false),
                    PropertyName = table.Column<string>(maxLength: 128, nullable: false),
                    AcceptedRevision = table.Column<long>(nullable: false),
                    AcceptedValue = table.Column<string>(maxLength: 16384, nullable: false),
                    RejectedValue = table.Column<string>(maxLength: 16384, nullable: false),
                    RelatedSequence = table.Column<long>(nullable: false),
                    CreatedUnixSeconds = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionConflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionConflicts_CollaborationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "CollaborationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionConflicts_SessionId_CreatedUnixSeconds",
                table: "SessionConflicts",
                columns: new[] { "SessionId", "CreatedUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionConflicts_SessionId_RejectedEventId",
                table: "SessionConflicts",
                columns: new[] { "SessionId", "RejectedEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionConflicts");

            migrationBuilder.DropColumn(
                name: "AcceptedById",
                table: "SessionProperties");
        }
    }
}
