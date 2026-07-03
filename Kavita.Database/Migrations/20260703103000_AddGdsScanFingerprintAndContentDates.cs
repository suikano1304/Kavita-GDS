using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kavita.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DataContext))]
    [Migration("20260703103000_AddGdsScanFingerprintAndContentDates")]
    public partial class AddGdsScanFingerprintAndContentDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FileCreated",
                table: "MangaFile",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "FileCreatedUtc",
                table: "MangaFile",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "ContentLastModified",
                table: "Series",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "ContentLastModifiedUtc",
                table: "Series",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "GdsScanFingerprint",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GdsScanFingerprintVersion",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE MangaFile
                SET FileCreated = Created
                WHERE FileCreated = '0001-01-01 00:00:00';
                """);

            migrationBuilder.Sql("""
                UPDATE MangaFile
                SET FileCreatedUtc = CreatedUtc
                WHERE FileCreatedUtc = '0001-01-01 00:00:00';
                """);

            migrationBuilder.Sql("""
                UPDATE Series
                SET ContentLastModified = COALESCE((
                    SELECT MAX(CASE WHEN mf.LastModified > mf.FileCreated THEN mf.LastModified ELSE mf.FileCreated END)
                    FROM Volume v
                    JOIN Chapter c ON c.VolumeId = v.Id
                    JOIN MangaFile mf ON mf.ChapterId = c.Id
                    WHERE v.SeriesId = Series.Id
                ), LastModified),
                ContentLastModifiedUtc = COALESCE((
                    SELECT MAX(CASE WHEN mf.LastModifiedUtc > mf.FileCreatedUtc THEN mf.LastModifiedUtc ELSE mf.FileCreatedUtc END)
                    FROM Volume v
                    JOIN Chapter c ON c.VolumeId = v.Id
                    JOIN MangaFile mf ON mf.ChapterId = c.Id
                    WHERE v.SeriesId = Series.Id
                ), LastModifiedUtc);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileCreated",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "FileCreatedUtc",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "ContentLastModified",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "ContentLastModifiedUtc",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "GdsScanFingerprint",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "GdsScanFingerprintVersion",
                table: "Series");
        }
    }
}
