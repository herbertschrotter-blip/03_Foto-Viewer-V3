using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// GPS-Spuren (ADR-0062, Migration AddGpsTracks). Nur eine neue Tabelle — kein Neuaufbau bestehender (FK-Kaskaden-Lehre aus
// ADR-0045/0048). GroupId ohne Fremdschlüssel wie Tags/Alben (ADR-0048).
internal sealed class GpsTrackConfiguration : IEntityTypeConfiguration<GpsTrack>
{
    public void Configure(EntityTypeBuilder<GpsTrack> b)
    {
        b.ToTable("GpsTracks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.GroupId).IsRequired();
        b.Property(x => x.StartUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.EndUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.ImportedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.Source).HasConversion<int>();
        b.Property(x => x.Checksum).IsRequired();
        b.Property(x => x.Points).IsRequired();

        // Doppel-Import: dieselben Punkte je Gruppe nur einmal.
        b.HasIndex(x => new { x.GroupId, x.Checksum }).IsUnique().HasDatabaseName("UX_GpsTracks_Group_Checksum");

        // Zeitraum-Abfrage „welche Aktivität deckt diese Foto-Zeit ab“ (Start ≤ t + Toleranz, Ende ≥ t − Toleranz).
        b.HasIndex(x => new { x.GroupId, x.StartUtc, x.EndUtc }).HasDatabaseName("IX_GpsTracks_Group_Time");
    }
}
