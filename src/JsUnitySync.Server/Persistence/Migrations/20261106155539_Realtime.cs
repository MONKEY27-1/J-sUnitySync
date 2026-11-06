using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsUnitySync.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Realtime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollaborationSessions",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    ProjectId = table.Column<string>(nullable: false),
                    Name = table.Column<string>(maxLength: 64, nullable: false),
                    CreatedById = table.Column<string>(nullable: false),
                    CreatedUnixSeconds = table.Column<long>(nullable: false),
                    LastSequence = table.Column<long>(nullable: false),
                    Revision = table.Column<long>(nullable: false),
                    IsClosed = table.Column<bool>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollaborationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollaborationSessions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionClientCursors",
                columns: table => new
                {
                    SessionId = table.Column<string>(nullable: false),
                    UserId = table.Column<string>(nullable: false),
                    ClientId = table.Column<string>(maxLength: 36, nullable: false),
                    LastAckedSequence = table.Column<long>(nullable: false),
                    UpdatedUnixSeconds = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionClientCursors", x => new { x.SessionId, x.UserId, x.ClientId });
                    table.ForeignKey(
                        name: "FK_SessionClientCursors_CollaborationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "CollaborationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionEvents",
                columns: table => new
                {
                    SessionId = table.Column<string>(nullable: false),
                    Sequence = table.Column<long>(nullable: false),
                    EventId = table.Column<string>(nullable: false),
                    AuthorId = table.Column<string>(nullable: false),
                    Kind = table.Column<int>(nullable: false),
                    ObjectId = table.Column<string>(maxLength: 26, nullable: false),
                    PayloadJson = table.Column<string>(maxLength: 65536, nullable: false),
                    UtcTimestampMilliseconds = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEvents", x => new { x.SessionId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_SessionEvents_CollaborationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "CollaborationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionProperties",
                columns: table => new
                {
                    SessionId = table.Column<string>(nullable: false),
                    ObjectId = table.Column<string>(maxLength: 26, nullable: false),
                    ComponentType = table.Column<string>(maxLength: 128, nullable: false),
                    PropertyName = table.Column<string>(maxLength: 128, nullable: false),
                    Revision = table.Column<long>(nullable: false),
                    AcceptedValue = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionProperties", x => new { x.SessionId, x.ObjectId, x.ComponentType, x.PropertyName });
                    table.ForeignKey(
                        name: "FK_SessionProperties_CollaborationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "CollaborationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollaborationSessions_ProjectId_CreatedUnixSeconds",
                table: "CollaborationSessions",
                columns: new[] { "ProjectId", "CreatedUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvents_SessionId_EventId",
                table: "SessionEvents",
                columns: new[] { "SessionId", "EventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionClientCursors");

            migrationBuilder.DropTable(
                name: "SessionEvents");

            migrationBuilder.DropTable(
                name: "SessionProperties");

            migrationBuilder.DropTable(
                name: "CollaborationSessions");
        }
    }
}
