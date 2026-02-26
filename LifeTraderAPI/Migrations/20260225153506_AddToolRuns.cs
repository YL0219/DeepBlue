using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeTraderAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddToolRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ThreadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ArgumentsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    IsSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToolRuns_CreatedAtUtc",
                table: "ToolRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ToolRuns_ThreadId",
                table: "ToolRuns",
                column: "ThreadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolRuns");
        }
    }
}
