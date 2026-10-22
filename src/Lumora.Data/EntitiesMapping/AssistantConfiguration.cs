using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// Story-Assistent (ADR-0063, Migration AddStoryAssistant). Nur neue Tabellen — kein Neuaufbau bestehender
// (FK-Kaskaden-Lehre aus ADR-0045/0048). GroupId ohne Fremdschlüssel wie Tags/Alben/Storys (ADR-0048).

internal sealed class AssistantHistoryConfiguration : IEntityTypeConfiguration<AssistantHistoryEntry>
{
    public void Configure(EntityTypeBuilder<AssistantHistoryEntry> b)
    {
        b.ToTable("AssistantHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Sentence).IsRequired();
        b.Property(x => x.IntentJson).IsRequired();
        b.Property(x => x.GroupId).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.LastUsedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.HasIndex(x => new { x.GroupId, x.LastUsedAtUtc }).HasDatabaseName("IX_AssistantHistory_Group_Used");
    }
}

internal sealed class AssistantAliasConfiguration : IEntityTypeConfiguration<AssistantAlias>
{
    public void Configure(EntityTypeBuilder<AssistantAlias> b)
    {
        b.ToTable("AssistantAliases");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Term).IsRequired();
        b.Property(x => x.GroupId).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.HasIndex(x => new { x.GroupId, x.Term, x.PersonId }).IsUnique().HasDatabaseName("UX_AssistantAliases_Group_Term_Person");

        // Person gelöscht → gemerkte Antwort weg.
        b.HasOne<Person>()
            .WithMany()
            .HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.PersonId).HasDatabaseName("IX_AssistantAliases_Person");
    }
}
