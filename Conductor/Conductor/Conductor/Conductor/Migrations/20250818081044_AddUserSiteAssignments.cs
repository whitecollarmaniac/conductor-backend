using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conductor.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSiteAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Sites",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Sites",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "UserSiteAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    AssignedByUserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSiteAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSiteAssignments_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSiteAssignments_Users_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSiteAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sites_CreatedByUserId",
                table: "Sites",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSiteAssignments_AssignedByUserId",
                table: "UserSiteAssignments",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSiteAssignments_SiteId",
                table: "UserSiteAssignments",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSiteAssignments_UserId_SiteId",
                table: "UserSiteAssignments",
                columns: new[] { "UserId", "SiteId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Sites_Users_CreatedByUserId",
                table: "Sites",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sites_Users_CreatedByUserId",
                table: "Sites");

            migrationBuilder.DropTable(
                name: "UserSiteAssignments");

            migrationBuilder.DropIndex(
                name: "IX_Sites_CreatedByUserId",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Sites");
        }
    }
}
