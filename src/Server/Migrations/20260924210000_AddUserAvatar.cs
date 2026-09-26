using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RoomBooking.Server.Infrastructure.Persistence;

#nullable disable

namespace RoomBooking.Server.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260924210000_AddUserAvatar")]
public partial class AddUserAvatar : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(name: "AvatarData", table: "AspNetUsers", type: "varbinary(max)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "AvatarContentType", table: "AspNetUsers", type: "nvarchar(max)", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AvatarData", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "AvatarContentType", table: "AspNetUsers");
    }
}
