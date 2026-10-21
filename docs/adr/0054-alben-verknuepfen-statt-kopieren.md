# ADR-0054: Alben — Fotos in Ordner und Alben verknüpfen statt kopieren, eigene und intelligente Alben

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Konzept, freigegeben 2026-10-21), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** [ADR-0012](0012-operation-logging.md) (Protokoll/Rückgängig), [ADR-0023](0023-neu-aufbauen-uebernimmt-benutzerdaten.md)/[ADR-0028](0028-neu-aufbauen-in-einer-transaktion.md) („Neu aufbauen“),
  [ADR-0034](0034-ordner-uebernahme-mit-leerketten-zusammenzug.md) (Ordner übernehmen), [ADR-0041](0041-galerie-filter-unabhaengig-kombinierbar.md) (Galerie-Filter),
  [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bibliotheks-Gruppen), [ADR-0049](0049-versteckte-bibliotheken.md) (versteckter Katalog)

## Kontext

Herbert (2026-10-21): „Ich habe Ordner mit Fotos, z. B. ‚Urlaub Sommer 2024‘. Wenn ich Fotos, die dieselben sind, auch in
anderen Ordnern haben möchte, ohne sie zu duplizieren — wäre es denkbar, über die Ordner eine Art ‚Album‘-Schicht
drüberzulegen, sodass mir bei Ordnern der reale Inhalt, aber auch der Album-Inhalt angezeigt wird?“ Freigegeben: ein Foto
liegt physisch nur einmal (FileId stabil), erscheint über Verknüpfungen in beliebig vielen Ordneransichten und Alben;
eigene und intelligente Alben; Verknüpfung entfernen löscht nie ein Foto; Verschieben/Umbenennen erhalten Verknüpfungen;
keine Windows-Hardlinks; Alben je Bibliotheks-Gruppe; spätere Storys entstehen aus einem Album.

## Entscheidung

1. **Schema** (Migration `AddAlbums`, nur neue Tabellen, Details PLAN Kap. 4): `Albums` (je `GroupId`, Name NOCASE
   eindeutig je Gruppe, `Kind` eigenes/intelligent, `FilterJson`, `CoverFileId` ON DELETE SET NULL), `AlbumItems`
   (AlbumId, FileId), `FolderLinks` (FolderId, FileId), `FileLinkUndo` (gesicherte Verknüpfungen entfernter Dateien).
   Alle Zuordnungen hängen per ON DELETE CASCADE an Files/Folders/Albums — keine Zeile zeigt je ins Leere.
2. **Dienste** (Services/Albums, WPF-frei): `IAlbumService` (Alben lesen/anlegen/umbenennen/löschen/Titelbild, Fotos
   hinzufügen/entfernen, `GetFileIdsAsync` = Dateien eines Albums im Bereich, neueste Aufnahme zuerst — die Schnittstelle
   für spätere Storys, Export) und `IFolderLinkService` (verknüpfen, entfernen). Schreiben über
   `CatalogFileTargets` (Schreibsperre abwarten, FileIds nach „Neu aufbauen“ umgesetzt, ADR-0030). Nachrichten
   `AlbumsChangedMessage`, `FolderLinksChangedMessage`.
3. **Intelligente Alben** speichern einen Galerie-Filter als JSON (`SmartAlbumFilter`): alle Teile von
   `GalleryFilterState` (Dateiart, Favoriten, Ablage, Bewertung, Ort) plus Suche, Tags (+Modus), Personen (+Modus,
   Vorschläge), Beziehung, Kategorien (+„ohne Wert“), Motive (+Modus), Ordner (+Unterordner) und eigenes Album.
   **Begriffe stehen über ihren Namen, nicht über Ids** — Verknüpfen führt gleichnamige Begriffe zusammen, Lösen kopiert
   sie mit neuen Ids, „Neu aufbauen“ vergibt neue Ordner-Ids; ein Namensbezug bleibt gültig. Ein Name, den es nicht mehr
   gibt, wird zur Id -1 und trifft nichts (ein gelöschtes Tag weitet das Album nie auf alles aus). Nicht gespeichert:
   Kartenausschnitt und Bildinhalt-Suche (vorübergehend, braucht das Modell). Ausgewertet wird mit **derselben SQL wie die
   Galerie** (`GallerySmartAlbumEvaluator` im Wpf-Projekt hinter dem Services-Vertrag `ISmartAlbumEvaluator`, wie der
   WIC-Renderer). Öffnen lädt den Filter in die Galerie; Bearbeiten = Filter ändern und unter demselben Namen neu
   speichern (Rückfrage „ersetzen“; ein eigenes Album wird nie überschrieben; ein leerer Filter wird abgelehnt).
4. **Ordneransicht**: der Ordnerfilter der Galerie (`GalleryPageSource.ResolveFolderClause`) zeigt „echte Dateien ODER in
   den Ordner (bzw. seinen Teilbaum) verknüpfte“ — nur wenn es dort Verknüpfungen gibt; sonst bleibt die Abfrage unverändert
   (gleiche Pläne, Test `Ordner_ohne_Verknuepfungen_behaelt_den_Abfrageplan`). Schalter „mit Verknüpfungen“ (Standard) /
   „nur echte Dateien“ und Zähler „12 Dateien · 5 verknüpft“ unter dem Titel, nur in Ordnern mit Verknüpfungen;
   Kennzeichen „verknüpft“ (Ketten-Symbol) auf Kachel, Collage und Liste — die Kachel bleibt ein Automation-Blatt.
   **Ordner-Verknüpfungen nur innerhalb einer Bibliothek** (Ordner und Datei derselben Bibliothek, in der Datenbank geprüft).
5. **Galerie**: Chip „Album“ in der Zeile „Inhalt“ (eigenes Album = Bedingung „Album …“ in „Es gilt“, × hebt sie auf;
   intelligentes = lädt den Filter), „Als Album speichern…“ in der „Es gilt“-Zeile, Kontextmenü „Zu Album hinzufügen“
   (Liste + „Album wählen oder neu anlegen…“), Aktionsleiste „Album“, **Strg+L** (Name eingeben: vorhandenes oder neues
   Album), „Hier verknüpfen…“ (Ordner wählen; fehlt die Katalogzeile unter einer Wurzel, wird sie angelegt),
   „Verknüpfung entfernen“, in der Album-Ansicht „Aus Album entfernen“ und „Als Titelbild des Albums“. Seite **„Alben“**
   (Navigation zwischen Ordner und Personen): Kacheln mit Titelbild und Anzahl, anlegen/umbenennen/löschen,
   Titelbild zurücksetzen, „Album exportieren…“. Ordner-Seite: Liste „Alben“ unter dem Baum (Klick öffnet, Ablegen fügt hinzu).
6. **Ziehen und Tasten-Konvention** (`GalleryDragData`): Galerie-Kacheln auf einen Ordner im Ordnerbaum ziehen — ohne
   Taste, mit Alt oder Strg = **Verknüpfen** (Standard, nichts bewegt sich), mit **Umschalt = Verschieben** (Rückfrage,
   protokolliert, rückgängig machbar) — wie im Explorer (Umschalt verschiebt, Alt verknüpft); Lumora kopiert nie in die
   Bibliothek, deshalb verknüpft auch Strg. Auf ein Album der Ordner-Seite ziehen = hinzufügen. Eine Mehrfachauswahl
   bleibt beim Ziehen erhalten (Klick auf eine gewählte Kachel wählt sie erst beim Loslassen allein).
7. **Sicherheit und Datei-Operationen** (kleine Haken in `FileOperationService`): Verschieben, Umbenennen, Ordner
   umbenennen, Sortierer behalten die FileId → Verknüpfungen bleiben ohne Zutun. Wer eine Files-Zeile entfernt
   (Papierkorb, aus der Bibliothek heraus verschieben, Reste einer Ordner-Übernahme), sichert deren Verknüpfungen zuvor in
   `FileLinkUndo` zur protokollierten `FileOperation` (mit Pfad, Größe, Änderungszeit). **Rückgängig** eines Verschiebens
   nach außen legt eine neue Files-Zeile an und stellt die Verknüpfungen darauf her. Für den Papierkorb hat Lumora kein
   eigenes Rückgängig (der Windows-Papierkorb stellt her): kommt die Datei an ihren alten Pfad zurück und nimmt ein Scan
   sie auf, stellt `AlbumService` die Verknüpfungen her — **nur bei gleicher Größe und Änderungszeit** (ein neuer Import
   gleichen Namens erbt nichts). „Ordner übernehmen“ hängt Verknüpfungen in übernommene Quellordner auf die Zielordner um
   (`FolderLinkAdoption`). „Neu aufbauen“ trägt AlbumItems, FolderLinks (Ordner über den Pfad), Titelbilder und gesicherte
   Ordner-Ziele mit (`UserDataCarryOver.Albums.cs`). Album löschen entfernt nur Verknüpfungen (und deren Sicherungen).
8. **Export** („Album exportieren…“): echte Kopien in einen gewählten Ordner, Fortschritt, Abbrechen (eine halb kopierte
   Datei wird entfernt), gleichnamige Dateien bekommen „ (2)“, nichts wird überschrieben, Änderungszeit bleibt. Liegt das
   Ziel in einer Bibliothekswurzel → Rückfrage (Kopien wären beim nächsten Scan Doppelte). Kein OperationBatch: der
   Export ändert weder Katalog noch Bibliotheksdateien. In einer **versteckten Bibliothek gesperrt** (Kopien lägen
   außerhalb des Containers, ADR-0049).
9. **Bibliotheks-Trennung** (Regeln aus ADR-0048): Alben gehören der Gruppe (`TermTables`, `DeleteGroupIfEmpty`,
   `AdoptOrphans`, `RehomeIfGroupGoneAsync`) und zeigen/zählen/exportieren nur Dateien des Bereichs; ein Titelbild aus einer
   Bibliothek außerhalb des Bereichs wird nicht gezeigt. Hinzufügen nur von Dateien aus Bibliotheken der Album-Gruppe (in
   der Datenbank geprüft). **Verknüpfen**: gleichnamige eigene Alben werden eins (Fotos vereinigt, Zielgruppe behält Name
   und Titelbild); ist bei gleichem Namen ein intelligentes Album beteiligt, bekommt das hinzukommende den
   Bibliotheksnamen als Zusatz („Urlaub (Zweite)“) — keine Rückfrage, Regel wie bei Tags. **Lösen**: die herausgelöste
   Bibliothek bekommt Kopien aller Alben (eigene mit ihren Fotos, intelligente mit demselben Filter), neue Ids über dem
   AUTOINCREMENT-Zähler. Versteckte Bibliothek: Alben leben nur in ihrem eigenen Katalog.

## Begründung

- **Verknüpfungstabellen statt Hardlinks/Kopien**: Hardlinks scheitern an OneDrive, Laufwerksgrenzen und würden beim
  Scan als Doppelte auftauchen; Kopien vervielfachen Speicher und Bearbeitungen. Eine Zeile je Verknüpfung kostet nichts
  und folgt der FileId durch alle Operationen.
- **Ordner-Verknüpfungen getrennt von Alben**: Herbert denkt in Ordnern („bei Ordnern der reale Inhalt plus der
  Album-Inhalt“); eine eigene Tabelle hält die Ordneransicht einfach (Teilbaum-Bedingung) und lässt Alben ordnerlos.
  Nur innerhalb einer Bibliothek, weil ein Ordner physisch zu genau einer gehört — beim Lösen gäbe es sonst nichts
  Verlustfreies.
- **Filter über Namen**: Ids ändern sich bei Verknüpfen, Lösen und „Neu aufbauen“; Namen sind das, was Herbert sieht.
- **Eine Auswertung**: intelligentes Album und Galerie nutzen dieselbe SQL — es kann nichts anderes zeigen als die
  Galerie mit demselben Filter (Test `Intelligentes_Album_zeigt_genau_den_gespeicherten_Galerie_Filter_im_Bereich`).
- **Rückkehr nur bei gleicher Größe/Änderungszeit**: Pfad allein wäre gefährlich (Kamera-Import mit wiederkehrenden
  Namen); Windows „Wiederherstellen“ behält beides bei.

## Konsequenzen

### Positiv
- Ein Foto in vielen Ordnern/Alben ohne Duplikate; Verschieben/Umbenennen/Sortieren/Übernehmen brechen nichts.
- Storys können `IAlbumService.GetFileIdsAsync` nutzen (eigene und intelligente Alben, im Bereich).

### Negativ
- Ordner mit Verknüpfungen zählen/laden mit einer ODER-Bedingung (zwei Index-Zugriffe statt einem); ohne Verknüpfungen
  unverändert. Nicht an 500 000 Dateien gemessen.
- Die Seite „Alben“ wertet intelligente Alben einzeln aus (Zähler, Titelbild) — bei vielen intelligenten Alben auf großen
  Bibliotheken spürbar; das Galerie-Flyout wertet sie nicht aus.
- Papierkorb hat weiterhin kein Lumora-Rückgängig; Verknüpfungen kommen nur mit der Datei an ihrem alten Ort zurück.

### Neutral
- Der Bestätigungsdialog „Verknüpfen“ der Bibliotheken nennt die Alben nicht (wie die Motiv-Begriffe).
- Export protokolliert nicht in OperationBatches (keine Katalog-/Bibliotheksänderung).

## Alternativen

- **Windows-Hardlinks/Junctions** → verworfen (OneDrive, Laufwerksgrenzen, Doppelte beim Scan).
- **Eine Tabelle für Alben und Ordner (Ordner = automatisches Album)** → verworfen: Ordneransicht müsste Album-Logik kennen,
  Unterordner-Teilbaum wäre umständlich, Lösen von Bibliotheken unklar.
- **Filter über Ids speichern und beim Verknüpfen/Lösen umschreiben** → verworfen: JSON in SQL umschreiben ist
  fehleranfällig, „Neu aufbauen“ hätte es ebenfalls gebraucht.
- **Gleichnamige Alben beim Verknüpfen nach Rückfrage zusammenführen** → verworfen zugunsten der Tag-Regel ohne Rückfrage;
  intelligente Alben werden nie vermischt.
- **Lumora-eigenes Rückgängig für den Papierkorb** (Wiederherstellen über die Shell) → nicht in diesem Schritt
  (Shell-Verben sind sprachabhängig); die Rückkehr der Datei stellt die Verknüpfungen her.

## Referenzen

- Code: `Services/Albums/*` (`IAlbumService`, `AlbumService`, `IFolderLinkService`, `FolderLinkService`, `SmartAlbumFilter`,
  `ISmartAlbumEvaluator`, `FileLinkUndoStore`, `AlbumGroupStore`, `FolderLinkAdoption`), `Services/Scanning/UserDataCarryOver.Albums.cs`,
  `Data/Migrations/*_AddAlbums.cs`, `Wpf/Albums/*`, `Wpf/Gallery/Data/GalleryPageSource.Albums.cs`,
  `Wpf/Gallery/ViewModels/GalleryViewModel.Albums.cs`, `Wpf/Gallery/Views/GalleryView.Albums.cs`, `Wpf/Folders/FolderTreePage.xaml(.cs)`
- Tests: `Albums/AlbumServiceTests`, `Albums/AlbumScopingTests` (Services), `Albums/GalleryAlbumQueryTests`,
  `Albums/BoolOptionConverterTests` (Wpf); Rauchtest-Schritt „Alben“
