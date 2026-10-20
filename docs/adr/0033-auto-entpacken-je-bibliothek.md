# ADR-0033: Automatisches Entpacken je Bibliothek, Schalter auf der Einstellungsseite für die aktive Bibliothek

- **Status:** Accepted
- **Datum:** 2026-10-20
- **Entscheider:** Claude (Auftrag ohne Rückfragemöglichkeit — Annahmen unten, Abnahme durch Herbert Schrotter offen)
- **Bezogen auf:** [ADR-0032](0032-archive-automatisch-entpacken.md) (löst dessen Entscheidung 1 „Einstellung global“ ab),
  ADR-0003 (eine aktive Bibliothek), ADR-0020 (Wurzeln mit Rollen), ADR-0024 (`archive.lastTarget.{LibraryId}`)

## Kontext

ADR-0032 (0.2.25) hat das automatische Entpacken neuer Archive in Eingangsordnern als **globale** Einstellung eingeführt
und „je Bibliothek“ ausdrücklich zurückgestellt. Bibliotheken unterscheiden sich aber: ein Download-Eingang soll
entpacken, ein Kamera-Eingang einer anderen Bibliothek mit gezippten Projektordnern nicht. Außerdem blieben Archive,
die schon vor dem Einschalten im Katalog standen, dauerhaft liegen — ohne Weg, sie bewusst nachzuholen.

## Entscheidung

1. **Je Bibliothek, Standard aus**: Schlüssel `archives.autoExtract.enabled.{LibraryId}` und
   `archives.autoExtract.recycle.{LibraryId}` in der Settings-Tabelle (Muster wie `archive.lastTarget.{LibraryId}`) —
   keine Schema-Änderung, keine Migration. Beide Schalter (Entpacken, Papierkorb danach) gelten je Bibliothek.
2. **Übernahme der globalen Einstellung**: Beim ersten Start (`IArchiveAutoExtractService.StartAsync` →
   `IArchiveAutoExtractSettingsStore.MigrateGlobalDefaultAsync`) erhält jede **bestehende** Bibliothek ohne eigenen
   Eintrag die Werte der globalen Schlüssel `archives.autoExtract.enabled` / `.recycle`; danach werden die globalen
   Schlüssel gelöscht (eine Transaktion, ihr Fehlen ist der Marker — die Übernahme läuft genau einmal). Bibliotheken,
   die danach entstehen, starten mit „aus“.
3. **Bedienung auf der Einstellungsseite für die aktive Bibliothek**: Abschnitt „Archive“ zeigt „Gilt für die aktive
   Bibliothek „Name““ und die beiden Schalter dieser Bibliothek (gelesen beim Anzeigen der Seite); ohne aktive Bibliothek
   ist der Abschnitt deaktiviert. Keine Bedienung auf der Bibliotheksseite.
4. **„Vorhandene Archive in Eingängen jetzt entpacken…“** (Knopf im selben Abschnitt, nur bei eingeschaltetem Schalter):
   zählt die Archive der aktiven Bibliothek unter Eingangs-Wurzeln (nicht fehlend, Zielordner existiert noch nicht, nicht
   in einem in dieser Sitzung entpackten Ordner), fragt nach und reiht **denselben Lauf** wie nach einem Scan ein
   (`ExtractExistingAsync`: Sperre je Archiv, Papierkorb nach Einstellung, ein Folge-Scan, Statusleiste).
5. Die Logik aus ADR-0032 bleibt sonst unverändert; geprüft wird jetzt „an für **diese** Bibliothek“ beim Belegen der
   Scan-Sperre, beim Einreihen und vor jedem Archiv (Ausschalten beendet den Lauf dieser Bibliothek nach dem aktuellen Archiv).

## Begründung

- **Settings-Schlüssel mit LibraryId statt Spalte in `Libraries`**: keine Schema-Migration (Data/Migrations bleibt
  unberührt), dasselbe Muster wie `archive.lastTarget.{LibraryId}`; LibraryIds werden dank `AUTOINCREMENT` nicht
  wiederverwendet, eine neue Bibliothek erbt also nie den Schalter einer gelöschten.
- **Übernahme statt Neustart bei „aus“**: wer das Entpacken global eingeschaltet hatte, soll nach dem Update nicht
  plötzlich ohne dastehen; neue Bibliotheken bleiben trotzdem aus (Vorgabe ADR-0032: nichts passiert ungefragt).
- **Einstellungsseite statt Bibliotheksseite**: der Schalter lebt seit 0.2.25 dort, die Ordnerüberwachung (ebenfalls
  Verhalten der aktiven Bibliothek) auch; die Bibliotheksseite zeigt Scan-Fortschritt und Wurzeln und wird parallel an
  der Warte-Anzeige umgebaut. Eine Bibliothek ist ohnehin „aktiv“ (ADR-0003), gearbeitet wird in ihr.
- **Nachholen nur bei „an“**: dieselben Optionen (Papierkorb) gelten, und „Ausschalten beendet den Lauf“ bleibt
  konsistent; wer ohne Automatik entpacken will, nutzt den Entpacken-Dialog.
- **Schon entpackte Archive nicht melden**: beim Nachholen würde sonst jedes früher manuell entpackte Archiv als
  „übersprungen — Zielordner existiert“ in der Statusleiste landen.

## Konsequenzen

### Positiv
- Automatik pro Bibliothek; Bibliotheken ohne Bedarf bleiben unberührt, auch wenn eine andere entpackt.
- Archive aus der Zeit vor dem Einschalten lassen sich mit einem Klick nachholen, mit Rückfrage und Anzahl.

### Negativ
- Die Einstellung einer nicht aktiven Bibliothek ist nur nach Wechsel der aktiven Bibliothek sichtbar/änderbar.
- Einträge gelöschter Bibliotheken bleiben in der Settings-Tabelle liegen (folgenlos, je zwei Zeilen).
- Beim Nachholen werden Archive in Ordnern, die in einer **früheren** Sitzung aus einem Archiv entstanden sind, nicht
  erkannt (die gemerkten Zielordner gelten je Sitzung, ADR-0032) — betrifft nur Reste an der Tiefengrenze 3.

### Neutral
- `IArchiveAutoExtractService`: `IsEnabled`/`RecycleAfterExtract` entfallen zugunsten von `GetSettings(libraryId)`;
  `SetEnabledAsync`/`SetRecycleAfterExtractAsync` nehmen die LibraryId; neu `CountExistingAsync`/`ExtractExistingAsync`.
- Fresh-Scan und „Neu aufbauen“ lösen weiterhin nichts aus.

## Annahmen (ohne Rückfrage getroffen)

- Auch „Papierkorb danach“ gilt je Bibliothek (gleiche Übernahme wie der Hauptschalter).
- Die globale Vorgabe wird auch übernommen, wenn sie „aus“ war (explizite Einträge „false“ — gleiches Verhalten wie ohne).

## Alternativen

- **Spalte `Libraries.AutoExtractInbox`**: verworfen — Schema-Migration für zwei Booleans; die Settings-Tabelle reicht.
- **Schalter auf der Bibliotheksseite je Bibliothek**: verworfen (siehe Begründung); nachrüstbar über dieselbe Service-API.
- **Globale Einstellung als Fallback behalten** (je Bibliothek nur Überschreibung): verworfen — zwei Ebenen sind schwer
  zu erklären, und neue Bibliotheken würden ungefragt entpacken.
- **Nachholen auch bei „aus“**: verworfen (siehe Begründung).

## Referenzen

- `src/PhotoViewerV3.Services/Archive/ArchiveAutoExtractSettingsStore.cs`, `ArchiveAutoExtractService.cs`,
  `IArchiveAutoExtractService.cs`, `ArchiveAutoExtractModels.cs`
- `src/PhotoViewerV3.Wpf/Settings/SettingsViewModel.cs`, `SettingsPage.xaml` (Abschnitt „Archive“),
  `Shell/Smoke/SmokeScenario.AutoExtract.cs` (Rauchtest-Schritt „Auto-Entpacken“)
- Tests: `tests/PhotoViewerV3.Tests/Archive/ArchiveAutoExtractPerLibraryTests.cs`, `ArchiveAutoExtractServiceTests.cs`,
  `tests/PhotoViewerV3.Wpf.Tests/Settings/SettingsAutoExtractTests.cs`
