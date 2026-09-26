using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RoomBooking.Server.Infrastructure.Persistence;

#nullable disable

namespace RoomBooking.Server.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260924230000_AddResourceImage")]
public sealed class AddResourceImage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "ImageData",
            table: "Resources",
            type: "varbinary(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImageContentType",
            table: "Resources",
            type: "nvarchar(40)",
            maxLength: 40,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ImageData", table: "Resources");
        migrationBuilder.DropColumn(name: "ImageContentType", table: "Resources");
    }
}
