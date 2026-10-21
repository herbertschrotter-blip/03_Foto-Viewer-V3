using System.IO;
using Lumora.Gallery.ArchiveTarget;

namespace Lumora.Wpf.Tests.ArchiveTarget;

/// <summary>Vorschlag „Jahr/Monat“ für „Ins Archiv…“: Format yyyy\yyyy-MM, Rückfall auf das ältere Dateidatum ohne EXIF.</summary>
public sealed class ArchiveDateSuggestionTests
{
    [Fact]
    public void RelativePathFor_liefert_Jahr_und_Jahr_Monat()
        => ArchiveDateSuggestion.RelativePathFor(new DateTime(2024, 7, 3)).Should().Be(Path.Combine("2024", "2024-07"));

    [Fact]
    public void Ohne_EXIF_zaehlt_das_aeltere_von_Erstell_und_Aenderungsdatum()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var file = ArchiveTestDb.WriteFile(Path.Combine(dir, "kopie.jpg"));
            File.SetCreationTime(file, new DateTime(2025, 1, 10));
            File.SetLastWriteTime(file, new DateTime(2021, 3, 5)); // Kopie: Änderungsdatum bleibt original

            ArchiveDateSuggestion.SuggestFor(file).Should().Be(Path.Combine("2021", "2021-03"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Fehlende_Datei_liefert_keinen_Vorschlag()
        => ArchiveDateSuggestion.SuggestFor(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg")).Should().BeNull();
}
