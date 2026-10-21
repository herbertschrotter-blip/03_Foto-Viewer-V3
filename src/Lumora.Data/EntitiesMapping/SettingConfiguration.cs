using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

internal sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> b)
    {
        b.ToTable("Settings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).IsRequired();
    }
}
