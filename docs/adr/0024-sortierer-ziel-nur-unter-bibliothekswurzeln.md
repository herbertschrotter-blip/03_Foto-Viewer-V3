# ADR-0024: Sortierer und Flatten — Quelle und Ziel nur unter den Wurzeln der Bibliothek

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** ADR-0012 (Operation-Logging), ADR-0018 (Datei-Operationen nur über den Service), ADR-0020 (mehrere Wurzeln), ADR-0021 (Tags)

## Kontext

Seit 0.2.9 hat eine Bibliothek eine Archiv-Wurzel und beliebig viele Eingangs-Wurzeln (ADR-0020). Der aus V2
portierte File Sorter und „Flatten & Move“ kannten nur `Libraries.RootPath` (= Archiv-Wurzel): Ein Quellordner im
Eingang wurde als „gehört zu keiner Bibliothek“ abgelehnt, und eine Zielwurzel außerhalb von `RootPath` galt als
„außerhalb der Bibliothek“ — mit einer Warnung, aber ausführbar. Der Arbeitsablauf ist aber gerade: Dateien aus einem
Eingang in neue Ordner unter dem Archiv sortieren. Ein Ziel außerhalb aller Wurzeln entfernt die Dateien aus dem
Katalog (`FileOperationService` löscht Files-Zeile, Thumbnails und FTS-Eintrag); ein späteres Rückgängig legt sie mit
**neuen** FileIds wieder an — Bewertung, Favorit und Tags (seit 0.2.10 an FileIds gebunden) sind dann weg.

## Entscheidung

Quelle ist jede Wurzel einer Bibliothek (Eingang oder Archiv) bzw. ein Ordner darunter. Das Ziel muss unter einer
erreichbaren Wurzel **derselben** Bibliothek liegen; Ziele außerhalb aller Wurzeln oder unter einer Offline-Wurzel sind
**gesperrt** (alle Plan-Einträge „ungültiges Ziel“, `SorterPlan.TargetError`), nicht nur mit einer Warnung versehen.

## Begründung

- Der Sortierer verspricht „alles protokolliert und rückgängig machbar“ (ADR-0012). Bei einem Ziel außerhalb hält das
  Versprechen nur für die Dateien, nicht für die Katalogdaten — eine Warnung im Bestätigungsdialog übersieht man leicht,
  der Verlust von Tags und Bewertungen wäre still.
- Wer Dateien bewusst aus der Bibliothek herausnehmen will, hat dafür die Galerie („Verschieben nach…“) oder
  den Explorer; der Sortierer ist ein Werkzeug zum Ordnen innerhalb des Katalogs.
- Über Wurzelgrenzen hinweg braucht es keinen neuen Mechanismus: `IFileOperationService.MoveAsync` legt die
  Zielordner-Kette unter der passenden Wurzel an (`Folders.RootId`), die FileId bleibt, der FTS-Eintrag bleibt gültig
  (der Name ändert sich nicht; Flatten-Umbenennungen pflegen FTS wie bisher), und Undo stellt die Zuordnung zur
  Eingangs-Wurzel wieder her.

## Konsequenzen

### Positiv
- Eingang → Archiv ist der Standard der UI (Quelle: erster erreichbarer Eingang, Ziel: Archiv-Wurzel); der
  Galerie-Filter „Nicht archiviert“ leert sich durch das Sortieren.
- Kein Katalogverlust durch den Sortierer; Rückgängig stellt FileIds, Bewertungen, Favoriten und Tags vollständig her.
- Sortieren und Flatten lesen `Libraries.RootPath` nicht mehr (ADR-0020 hatte das Entfernen u. a. deshalb vertagt).

### Negativ
- V2 erlaubte jedes Ziel; ein Export in einen beliebigen Ordner geht mit dem Sortierer nicht mehr.

### Neutral
- Wurzeln werden beim Aufräumen leerer Quellordner nie gelöscht (Eingang wie Archiv).
- Die Wurzel-Auswahl der Seite lädt bei `ActiveLibraryChangedMessage` und `LibraryRootsChangedMessage` neu; ändern sich
  die Wurzeln, wird eine vorhandene Analyse verworfen (sie kennt die alten Wurzeln).

## Alternativen

- **Warnung statt Sperre (bisheriges Verhalten)** → verworfen: stiller Verlust von Tags/Bewertungen nach Rückgängig.
- **Ziel außerhalb als neue Eingangs-Wurzel anbieten** → verworfen: verändert die Bibliothek als Nebenwirkung des
  Sortierens; Wurzeln werden bewusst auf der Bibliotheksseite verwaltet.

## Referenzen

- `src/PhotoViewerV3.Services/Sorter/SorterRoots.cs` (`SorterTargetCheck`), `SorterCatalog.RequireLocationAsync`,
  `FileSorterService.BuildPlan`, `FlattenService.BuildPlan`
- `src/PhotoViewerV3.Wpf/Sorter/ViewModels/SorterRootSelection.cs`, `Sorter/Views/SorterLocationView.xaml`
- Tests: `tests/PhotoViewerV3.Tests/Sorter/SorterRootsTests.cs`, `tests/PhotoViewerV3.Wpf.Tests/Sorter/SorterRootSelectionTests.cs`
- CHANGELOG 0.2.16
