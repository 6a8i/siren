using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyLibraryAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Families that exist before this migration keep reaching every library.
            migrationBuilder.AddColumn<bool>(
                name: "EnableAllFolders",
                table: "Families",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "EnabledFolders",
                table: "Families",
                type: "TEXT",
                maxLength: 65535,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAllFolders",
                table: "Families");

            migrationBuilder.DropColumn(
                name: "EnabledFolders",
                table: "Families");
        }
    }
}
