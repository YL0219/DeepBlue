using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LifeTraderAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketDataAssets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Interval = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    ParquetPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastIngestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastDataEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProviderUsed = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RowsWritten = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDataAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataAssets_Symbol_Interval",
                table: "MarketDataAssets",
                columns: new[] { "Symbol", "Interval" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_Symbol",
                table: "WatchlistItems",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketDataAssets");

            migrationBuilder.DropTable(
                name: "WatchlistItems");
        }
    }
}
