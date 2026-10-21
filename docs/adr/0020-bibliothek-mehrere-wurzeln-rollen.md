# ADR-0020: Bibliothek mit mehreren Wurzeln und Rollen (Eingang / Archiv)

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0003 (Catalog-Modell), ADR-0005 (Migrationen), ADR-0012 (Operations-Protokoll), ADR-0018, ADR-0019

## Kontext

Der Alltag besteht aus zwei Ordnerarten: neues Material landet in Eingangs-Ordnern (Kamera-Import, Handy-Upload,
Download), sortiert wird es in einen Archiv-Ordner verschoben. Bisher entsprach eine Bibliothek genau einem
`Libraries.RootPath` (ADR-0003). Eingang und Archiv als zwei Bibliotheken zu führen, trennt Galerie, Suche und
Ordnerbaum, und „Verschieben nach…“ aus der einen in die andere entfernt die Datei aus dem Katalog (Ziel außerhalb der
Wurzel) — Favoriten, Bewertungen und Vorschaubilder gehen verloren. Gebraucht wird ein Arbeitsablauf „Eingang →
Archiv“ innerhalb EINER Bibliothek mit stabilen FileIds.

## Entscheidung

Eine Bibliothek hat **mehrere Wurzelordner** in der neuen Tabelle `LibraryRoots (Id, LibraryId, Path, Role, Name,
SortOrder)` mit der Rolle **Archiv (1) — genau eine je Bibliothek** oder **Eingang (0) — beliebig viele**. Jeder Ordner
hängt über `Folders.RootId` an seiner Wurzel; „archiviert“ ist eine Eigenschaft des Ordners (Rolle seiner Wurzel), keine
Spalte an `Files`. `Libraries.RootPath` bleibt als Anzeige-/Kompatibilitätsfeld und zeigt immer auf die Archiv-Wurzel.

## Begründung

- Status über `Folders.RootId → LibraryRoots.Role` braucht keine Pflege beim Verschieben: der `FileOperationService`
  hängt die Datei an einen Ordner der Zielwurzel, damit ist sie archiviert; Undo hängt sie zurück. Kein zweiter Zustand,
  der auseinanderlaufen kann.
- Die Filter „Nicht archiviert“/„Archiviert“ laufen als `IN`-Subquery über die kleine Folders-Tabelle (Index
  `IX_Folders_Root`), die Count-Query bleibt ohne Join.
- Wurzeln dürfen sich nicht überlappen (gleich, innerhalb oder oberhalb einer Wurzel derselben Bibliothek; gleich einer
  Wurzel einer anderen Bibliothek) — sonst katalogisierte der Scanner Dateien doppelt.
- `RootPath` beizubehalten hält Sortieren/Flatten, Statusleiste und Altcode lauffähig, ohne sie im selben Schritt
  umzubauen.

## Konsequenzen

### Positiv
- „Ins Archiv…“ (Galerie: Aktionsleiste, Strg+M, Kontextmenü; Lightbox: Strg+M über `IArchiveMoveService`) verschiebt
  protokolliert (ADR-0012) mit stabilen FileIds; Favoriten, Bewertungen und Vorschaubilder bleiben.
- Galerie-Filter und Badge „Eingang“ zeigen, was noch zu sortieren ist; der Ordnerbaum zeigt die Wurzeln mit Rolle als
  oberste Knoten.
- Weitere Eingänge (zweite Kamera, Netzlaufwerk) kommen ohne Schemaänderung dazu.

### Negativ
- Der Scanner prüft Erreichbarkeit, Offline- und Mass-Missing-Guard **je Wurzel** (PLAN Kap. 12). Fresh/ClearAndRescan
  starten nur, wenn alle Wurzeln erreichbar sind; der Incremental-Scan überspringt eine nicht erreichbare Wurzel,
  gleicht die übrigen ab und meldet `LibraryOffline` mit dem Pfad der fehlenden Wurzel.
- Eine Wurzel zu entfernen löscht ihre Katalogdaten (Ordner, Dateien, Vorschaubilder, FTS) — nur aus dem Katalog, keine
  Datei auf der Platte. Die Archiv-Wurzel kann nicht entfernt, nur per Rollentausch ersetzt werden.

### Neutral
- `Folders.RootId` ist ein logischer Fremdschlüssel ohne DB-Constraint (wie alle Verweise des Schemas) und nullable;
  Zeilen ohne `RootId` zählen weder als Eingang noch als Archiv.
- Ordner anlegen (`MkDir`-Batch, nicht rückgängig) und umbenennen (`RenameFolder`-Batch: eine Ordner-Operation plus je
  enthaltener Datei eine Operation, `Directory.Move` in einem Schritt, rückgängig machbar) laufen über den
  `IFileOperationService`. Wurzelordner selbst werden nicht umbenannt.
- Die Migration `AddLibraryRoots` übernimmt jede `Libraries.RootPath` als Archiv-Wurzel (Name = letztes Pfadsegment) und
  setzt `Folders.RootId` per Pfad-Präfix (ADR-0005: Bestandsdaten bleiben).

## Alternativen

- **Zwei Bibliotheken (Eingang, Archiv)** → verworfen: getrennte Galerie/Suche, Verschieben zwischen Bibliotheken
  verliert Katalogdaten.
- **Spalte `Files.IsArchived`** → verworfen: zweiter Zustand neben dem Ordner, muss bei jedem Verschieben, Undo, Scan
  und Ordner-Umbenennen gepflegt werden.
- **Status aus dem Pfad-Präfix zur Laufzeit** (ohne `RootId`) → verworfen: `LIKE`-Präfixvergleiche je Datei im
  Galerie-SQL, nicht indexierbar.
- **`Libraries.RootPath` entfernen** → vertagt: Sortieren/Flatten und Statusleiste lesen ihn; das Feld wird mit der
  Archiv-Wurzel synchron gehalten.

## Referenzen

- `src/Lumora.Domain/Entities/LibraryRoot.cs`, `Data/Migrations/*_AddLibraryRoots.cs`
- `Services/Library/LibraryService.cs` (Wurzelverwaltung), `Services/Scanning/ScanService.cs` (Scan je Wurzel),
  `Services/Operations/FileOperationService.cs` (Ziel unter irgendeiner Wurzel, Ordner anlegen/umbenennen)
- `Wpf/Gallery/ArchiveTarget/**` (Dialog „Ins Archiv…“), `Wpf/Gallery/Data/GalleryPageSource.cs` (Status-Filter)
- CHANGELOG 0.2.5
