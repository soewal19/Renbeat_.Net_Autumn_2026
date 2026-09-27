using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoomBooking.Server.Infrastructure.Persistence.Entities;

namespace RoomBooking.Server.Infrastructure.Persistence.Configurations;

public sealed class AiSkillConfiguration : IEntityTypeConfiguration<AiSkill>
{
    public void Configure(EntityTypeBuilder<AiSkill> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.InstructionsJson).IsRequired();
        builder.Property(x => x.ExamplesJson).IsRequired();
        builder.Property(x => x.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasIndex(x => x.IsActive);
    }
}
