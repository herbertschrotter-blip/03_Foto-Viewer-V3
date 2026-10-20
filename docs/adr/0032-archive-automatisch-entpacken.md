# ADR-0032: Neue Archive in Eingangsordnern optional automatisch entpacken

- **Status:** Accepted (Entscheidung 1 „Einstellung global“ abgelöst durch [ADR-0033](0033-auto-entpacken-je-bibliothek.md): je Bibliothek, 0.2.31)
- **Datum:** 2026-10-20
- **Entscheider:** Claude (Auftrag ohne Rückfragemöglichkeit — Annahmen unten, Abnahme durch Herbert Schrotter offen)
- **Bezogen auf:** ADR-0012 (Operation-Logging), ADR-0017 (Nachrichten), ADR-0018 (Datei-Operationen nur über Service),
  ADR-0019 (Incremental-Scan), ADR-0020 (Wurzeln mit Rollen), ADR-0022 (Archive als Katalog-Einträge),
  ADR-0025 (Scan-Sperre, eigene Schreibvorgänge), PLAN Kap. 1 („Auto-Extract“)

## Kontext

Seit 0.2.8 (ADR-0022) sind Archive Katalog-Einträge und lassen sich in der App entpacken — aber nur manuell über den
Entpacken-Dialog. V2 entpackte Archive beim Scan automatisch; ADR-0022 hat das als Einstellung „Standard aus“
zurückgestellt. Offen war, wann ein Archiv als „neu“ gilt, wo das Entpacken ansetzt (der ScanService wird parallel für
„Neu aufbauen“ umgebaut), wie es mit der nicht wiedereintrittsfähigen Scan-Sperre (ADR-0025) und der Ordnerüberwachung
zusammenspielt und wie verschachtelte Archive keine Endlosschleife auslösen.

## Entscheidung

1. **Einstellung global, Standard aus**: `archives.autoExtract.enabled` und `archives.autoExtract.recycle` in der
   Settings-Tabelle (`ArchiveAutoExtractSettingsStore`), Abschnitt „Archive“ auf der Einstellungsseite. „Papierkorb danach“
   ist ein eigener Schalter, ebenfalls aus.
2. **Neu gefunden** heißt: ein **Incremental-Scan** hat die Datei in den Katalog aufgenommen. Der neue
   `IArchiveAutoExtractService` (Services/Archive) sammelt dazu die Ids der `FilesChangedMessage` „Added“ zwischen
   `IScanGate.Acquired` und der `ScanCompletedMessage` derselben Bibliothek; eine `ScanAbortedMessage` (Abbruch, Fehler,
   offline) verwirft sie. Fresh-Scan und „Neu aufbauen“ senden keine Added-Ids und lösen nichts aus.
3. **Nur Archive unter Eingangs-Wurzeln** (Rolle Inbox, über `Folders.RootId`), nicht fehlend, nie unter der
   Archiv-Wurzel.
4. **Ziel** ist der Standard-Zielordner neben dem Archiv (`IArchiveService.GetDefaultTargetFolder`, wie beim manuellen
   Entpacken). **Existiert er schon** (Datei oder Ordner), bleibt das Archiv unberührt und wird gemeldet — die App befüllt
   nie automatisch einen vorhandenen Ordner (deckt auch ein aus dem Papierkorb zurückgeholtes Archiv ab, das sonst gleich
   wieder entpackt und weggeworfen würde).
5. **Optionen**: alles entpacken (`MediaOnly = false`, damit beim Papierkorb nichts verloren geht, was nur im Archiv lag),
   vorhandene Dateien überspringen, verschachtelte Archive über das bestehende Runden-System bis Tiefe 3.
6. **Ablauf nach dem Scan, nie während er die Sperre hält.** Die Nachrichten-Handler laufen synchron auf dem Scan-Thread
   und merken sich nur Ids; der Lauf wird eingereiht und auf dem Thread-Pool abgearbeitet (ein Lauf nach dem anderen).
   Je Archiv: Sperre mit `AcquireForFileOperationAsync` belegen (wartet auf das Ende des Scans; löst `Acquired` nicht
   aus), entpacken, Sperre freigeben; danach optional `IFileOperationService.RecycleAsync` (protokolliert, holt die
   Sperre selbst; nur nach fehlerfreiem Entpacken mit mindestens einer Datei); am Ende **ein** Incremental-Scan über
   `IScanService`, der die Sperre selbst holt.
7. **Keine Schleife**: `ArchiveService.ExtractAsync` sendet nach den Runden eine `ArchiveExtractedMessage` (Archiv,
   Zielordner) — manuell wie automatisch. Der Dienst merkt sich diese Ordner (Sitzung) und fasst Archive darin nie an;
   verschachtelte Archive werden damit höchstens nach der bestehenden Regel (Runden bis Tiefe 3) behandelt, Reste an der
   Tiefengrenze bleiben liegen. Bereits gemeldete Archive sind beim nächsten Scan nicht mehr neu.
8. **Ordnerüberwachung**: der Dienst meldet jeden Zielordner vor dem ersten Byte als eigenen Ordnerbaum an
   (`IOwnChangeScope.AddTree`, neu in `OwnChangeRegistry`: der Ordner und alles darunter gilt als eigen). Der Scope läuft
   bis nach dem Folge-Scan, danach die übliche Nachlaufzeit (10 s). So löst die Überwachung keinen zweiten Scan aus;
   Ereignisse vor dem Folge-Scan verwirft sie ohnehin bei dessen `Acquired`.
9. **Meldung**: `ArchiveAutoExtractStatusMessage` (Running je Archiv, Scanning, Finished mit `ArchiveAutoExtractSummary`)
   → Statusleiste: Fortschritt mit Ring, Ergebnis „Automatisch entpackt: n Archive · m Dateien · k im Papierkorb“;
   passwortgeschützte, beschädigte, leere, mit Fehlern entpackte oder übersprungene Archive als Warnung mit Tooltip
   (Name und Grund), 60 s sichtbar und im Log. Ausschalten während eines Laufs beendet ihn nach dem aktuellen Archiv.
10. **Außerhalb des ScanService**: keine Änderung am ScanService; der Dienst beobachtet nur Sperre und Nachrichten
    (`AddArchives()` registriert ihn, `App.xaml.cs` startet ihn nach der Ordnerüberwachung).

## Begründung

- **Global statt je Bibliothek**: die Settings-Tabelle ist global (wie die Ordnerüberwachung), und die Rollen der
  Wurzeln grenzen bereits ein — eine Bibliothek ohne Eingang ist nie betroffen. Je Bibliothek bräuchte eine Schemaspalte
  oder Bibliotheks-Schlüssel plus Bedienung auf der Bibliotheksseite; der Nutzen ist ohne konkreten Bedarf gering.
  Nachrüstbar, ohne die Logik zu ändern (Prüfung vor dem Einreihen).
- **Incremental-Ids statt Wasserstand**: ein gespeicherter „höchste geprüfte FileId“-Stand je Bibliothek wäre
  persistent, verlangt aber Initialisierung beim Einschalten und für neue Bibliotheken. Die Added-Ids des Scans sind
  genau „vom Scan neu gefunden“ und brauchen keinen Zustand; ein Absturz zwischen Scan und Entpacken lässt Archive
  liegen (konservativ: nichts passiert ungefragt doppelt).
- **Sperre nur fürs Entpacken**: die Sperre ist nicht wiedereintrittsfähig (ADR-0025) — wer sie hält, darf weder
  scannen noch einen Datei-Batch starten. Papierkorb und Folge-Scan laufen deshalb nach der Freigabe.
- **Alles entpacken**: mit „nur Medien“ würde der Papierkorb Begleitdateien (Texte, Sidecars) mitnehmen, die nur im
  Archiv lagen; wer nur Medien möchte, entpackt manuell.

## Konsequenzen

### Positiv
- Kamera-/Download-Archive im Eingang erscheinen ohne Klick als Fotos in der Galerie; das Archiv verschwindet auf Wunsch
  protokolliert (Undo über den Papierkorb von Windows).
- Kein Eingriff in den ScanService; manueller Weg, Scan-Sperre und Ordnerüberwachung bleiben unverändert gültig.

### Negativ
- Nur Archive, die ein Incremental-Scan neu aufnimmt: Archive, die schon vor dem Einschalten im Katalog standen, und
  die eines Fresh-Scans (neue Bibliothek) oder von „Neu aufbauen“ bleiben manuell.
- Die gemerkten Zielordner gelten nur für die Sitzung; nach einem Neustart sind Reste an der Tiefengrenze aber schon im
  Katalog und damit nicht mehr neu.
- Während der Lauf läuft, zählt die Ordnerüberwachung fremde Änderungen innerhalb der Zielordner nicht (der Folge-Scan
  sieht sie trotzdem; Lücke nur in der Nachlaufzeit).
- Ein großes Archiv hält die Sperre der Bibliothek für die Dauer des Entpackens; „Aktualisieren“ und Datei-Operationen
  warten so lange (wie bei jedem Datei-Batch).

### Neutral
- Kein Schema, keine Migration (zwei Settings-Schlüssel).

## Annahmen (ohne Rückfrage getroffen)

- „Neu gefunden“ = vom Incremental-Scan aufgenommen (inkl. eines Scans mit Mass-Missing-Rückfrage, weil neue Dateien
  dort normal gebucht werden); abgebrochene oder offline beendete Scans lösen nichts aus.
- Vorhandener Zielordner → nicht entpacken (statt zusammenführen).
- Leeres Archiv oder nur fehlerhafte Einträge → als „nicht entpackt“ gemeldet, leerer Zielordner wird entfernt.
- ADR-Nummer 0032 statt der nächsten freien 0028, weil parallel weitere Agenten ADRs anlegen (Reihenfolge der
  reservierten Versionen 0.2.21–0.2.26); beim Zusammenführen ggf. umnummerieren.

## Alternativen

- **Im ScanService nach dem Persistieren entpacken:** verworfen — der Scan hält dann die Sperre, das Entpacken liefe
  mitten im Scan; außerdem wird der ScanService parallel umgebaut.
- **Ordnerüberwachung entpackt selbst:** verworfen — deckt manuelles „Aktualisieren“ nicht ab und vermischt Zuständigkeiten.
- **Einstellung je Bibliothek:** zurückgestellt (siehe Begründung).
- **Zielordner zusammenführen (Überspringen vorhandener Dateien):** verworfen — ein wiederhergestelltes Archiv würde
  „nichts entpackt, fehlerfrei“ melden und gleich wieder im Papierkorb landen.

## Referenzen

- `src/PhotoViewerV3.Services/Archive/ArchiveAutoExtractService.cs`, `IArchiveAutoExtractService.cs`,
  `ArchiveAutoExtractModels.cs`, `ArchiveAutoExtractSettingsStore.cs`, `ArchiveMessages.cs` (`ArchiveExtractedMessage`,
  `ArchiveAutoExtractStatusMessage`), `ArchiveService.cs`; `src/PhotoViewerV3.Services/Watch/OwnChangeRegistry.cs` (`AddTree`)
- `src/PhotoViewerV3.Wpf/Settings/SettingsPage.xaml` (Abschnitt „Archive“), `Shell/StatusBarViewModel.cs`, `Shell/StatusBar.xaml`, `App.xaml.cs`
- Tests: `tests/PhotoViewerV3.Tests/Archive/ArchiveAutoExtractServiceTests.cs`, `Watch/OwnChangeRegistryTests.cs`,
  `tests/PhotoViewerV3.Wpf.Tests/Settings/SettingsAutoExtractTests.cs`
