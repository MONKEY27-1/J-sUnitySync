using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JsUnitySync.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamsAndSocial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Friendships",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    UserLowId = table.Column<string>(nullable: false),
                    UserHighId = table.Column<string>(nullable: false),
                    RequesterId = table.Column<string>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    Revision = table.Column<long>(nullable: false),
                    UpdatedUnixSeconds = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friendships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    Name = table.Column<string>(maxLength: 64, nullable: false),
                    CreatedById = table.Column<string>(nullable: false),
                    Revision = table.Column<long>(nullable: false),
                    CreatedUnixSeconds = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    TeamId = table.Column<string>(nullable: false),
                    UserId = table.Column<string>(nullable: false),
                    Role = table.Column<int>(nullable: false),
                    JoinedUnixSeconds = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.TeamId, x.UserId });
                    table.ForeignKey(
                        name: "FK_Memberships_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Memberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    TeamId = table.Column<string>(nullable: false),
                    Name = table.Column<string>(maxLength: 96, nullable: false),
                    CreatedById = table.Column<string>(nullable: false),
                    Revision = table.Column<long>(nullable: false),
                    IsArchived = table.Column<bool>(nullable: false),
                    CreatedUnixSeconds = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamInvitations",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    TeamId = table.Column<string>(nullable: false),
                    InviterId = table.Column<string>(nullable: false),
                    InviteeId = table.Column<string>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    ExpiresUnixSeconds = table.Column<long>(nullable: false),
                    Revision = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_UserLowId_UserHighId",
                table: "Friendships",
                columns: new[] { "UserLowId", "UserHighId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_UserId",
                table: "Memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TeamId_Name",
                table: "Projects",
                columns: new[] { "TeamId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_InviteeId",
                table: "TeamInvitations",
                column: "InviteeId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_TeamId_InviteeId",
                table: "TeamInvitations",
                columns: new[] { "TeamId", "InviteeId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_CreatedById",
                table: "Teams",
                column: "CreatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Friendships");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "TeamInvitations");

            migrationBuilder.DropTable(
                name: "Teams");
        }
    }
}
