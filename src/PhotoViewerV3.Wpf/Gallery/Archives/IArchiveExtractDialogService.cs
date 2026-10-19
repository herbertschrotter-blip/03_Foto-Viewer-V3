using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using PhotoViewerV3.Services.Archive;

namespace PhotoViewerV3.Gallery.Archives;

/// <summary>Ein zu entpackendes Archiv (Katalog-Id, Pfad, Anzeigename).</summary>
public sealed record ArchiveExtractTarget(int FileId, string Path, string Name);

/// <summary>Auftrag für den Entpacken-Dialog: Archive, aktive Bibliothek (für den Scan danach) und deren Root (Startordner der Ordnerauswahl).</summary>
public sealed record ArchiveExtractDialogRequest(IReadOnlyList<ArchiveExtractTarget> Targets, int LibraryId, string? LibraryRootPath);

/// <summary>
/// Entpacken-Dialog (Wpf/Gallery/Archives, ADR-0022) hinter einem Interface, damit das ViewModel kein Fenster kennt und
/// der Rauchtest ohne Dialog auskommt. Der Dialog führt das Entpacken selbst aus (Fortschritt, Abbruch, Ergebnis) und
/// liefert die Ergebnisse je Archiv — leer, wenn der Benutzer vor dem Start abgebrochen hat.
/// </summary>
public interface IArchiveExtractDialogService
{
    Task<IReadOnlyList<ArchiveExtractResult>> ShowAsync(ArchiveExtractDialogRequest request, Window? owner);
}
