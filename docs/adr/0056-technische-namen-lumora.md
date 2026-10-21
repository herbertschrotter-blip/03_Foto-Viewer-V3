# ADR-0056: Auch technische Namen heißen Lumora — Projekte, Namespaces, Datenordner, Variablen, Protokolle

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter („alle Hinweise zu PhotoViewer in Lumora ändern“, Umfang „alles“ per Auswahl),
  Claude (Umsetzung und Detailentscheidungen)
- **Bezogen auf:** ersetzt ADR-0047 Punkt 3 („Bewusst NICHT umbenannt“); ADR-0026 (Speicherort des Katalogs,
  `database-location.json`, absolute `Thumbnails.CachePath`)

## Kontext

Seit 0.2.49 heißt die App sichtbar Lumora (ADR-0047). Projekte, Namespaces, Solution, Datenordner, Umgebungsvariablen und
Protokolle hießen weiter `PhotoViewerV3`, weil ein Umbenennen einen Daten-Umzug und Konflikte mit parallelen
Agenten-Zweigen bedeutet hätte. Zum Stand 0.4.2 gibt es keine offenen Zweige mehr, Lumora ist noch in der Testphase
(Daten sind Kopien), und das GitHub-Repo heißt inzwischen ebenfalls `Lumora`. Herbert will den zweiten Namen loswerden.

## Entscheidung

1. **Projekte, Ordner und Namespaces** `PhotoViewerV3.*` → `Lumora.*` (`src/Lumora.Domain|Data|Services|Wpf`,
   `tests/Lumora.Tests|Wpf.Tests|PerfTests`, Wpf-`RootNamespace` `Lumora`), Solution `Lumora.slnx`,
   `AddPhotoViewerServices` → `AddLumoraServices`. Die Programmdatei bleibt `Lumora.exe` (Assembly `Lumora`), die
   übrigen Assemblys heißen `Lumora.Domain.dll` usw. Die EF-Migrationen behalten Namen und Ids; ihre Designer-Dateien und
   der Snapshot nennen die neuen Typnamen (`has-pending-model-changes` meldet keine Änderung).
2. **Datenordner** `%LocalAppData%\PhotoViewerV3` → `%LocalAppData%\Lumora`. Beim Start, bevor Logger und Datenbank
   ihn öffnen, benennt `LegacyAppDataMigration` den alten Ordner einmal um (gleiches Laufwerk, ein `Directory.Move`).
   Danach werden `database-location.json` (gewählter Datenordner und vorgemerkte Löschungen, sofern sie im alten Ordner
   lagen) und die absoluten `Thumbnails.CachePath` (Präfix alt → neu, wie beim Katalog-Umzug) umgeschrieben. Ein
   gewählter Datenordner außerhalb (ADR-0026) bleibt, wo er ist.
   - Lässt sich der Ordner nicht umbenennen (Dateien gesperrt, typisch: eine ältere Lumora läuft noch), arbeitet dieser
     Start mit dem alten Ordner weiter und protokolliert eine Warnung; der nächste Start versucht es erneut.
   - Gibt es beide Ordner, gilt der neue; der alte bleibt unangetastet liegen (Warnung im Protokoll). Ein **leerer**
     neuer Ordner zählt nicht — er wird entfernt und der alte umbenannt (sonst startete Lumora mit leerem Katalog).
   - Mit `LUMORA_APPDATA` (Tests, Rauchtests) findet kein Umzug statt.
3. **Umgebungsvariablen** `PHOTOVIEWERV3_*` und `PV3_*` → `LUMORA_*` (ohne Rückfall auf die alten Namen). Die
   Rauchtest-Skripte (`build/run-smoke.ps1`, `Lumora-Werkzeuge\scripts\run-smoke-v.ps1`) setzen die alten Rauchtest-Variablen
   zusätzlich, damit ein älteres Paket nie den echten Katalog nimmt.
4. **Protokolle** `logs\photoviewer-*.log` → `logs\lumora-*.log`; alte Protokolle ziehen mit dem Ordner um und laufen wie
   bisher nach der Aufbewahrungsfrist aus. Weitere technische Namen: Rauchtest-Arbeitsordner `%TEMP%\Lumora-smoke-*`,
   Test-Ordner `%TEMP%\Lumora.Tests`, Schreibprobe `.lumora-schreibtest-*`, Design-Time-DB `%TEMP%\Lumora-DesignTime`.
5. **Unverändert:** Historie (CHANGELOG-Einträge bis 0.4.2, ADRs bis 0055, Spikes, ChatGPT-Reviews) beschreibt den Stand
   ihrer Zeit; Info › Vorgänger nennt weiter „PhotoViewer V3 (bis 0.2.48) · PhotoViewer V2“. Versteckte Bibliotheken
   (`<Bibliothek>\.lumora`) waren nie betroffen.

## Begründung

- Ein Name für Produkt, Repo, Code und Daten: wer Ordner, Protokoll oder Quelltext öffnet, findet „Lumora“.
- Jetzt ist der günstigste Zeitpunkt: keine offenen Agenten-Zweige, Testphase, vor Welle 6.
- Umbenennen statt Kopieren: gleiches Laufwerk, sofort, kein doppelter Platz; ein Fehlschlag lässt alles, wie es war.
- Umschreiben der absoluten Vorschaubild-Pfade statt Neu-Rendern: sonst würde der Vorschaubild-Dienst alle Bilder neu
  erzeugen (bei großen Bibliotheken Stunden).
- Kein Rückfall auf `PHOTOVIEWERV3_*`: die Variablen setzen nur Skripte und Tests im Repo bzw. in den Werkzeugen, die
  mit umgestellt sind; ein Rückfall hielte den alten Namen dauerhaft im Code.

## Konsequenzen

### Positiv
- Kein zweiter Name mehr im Code, im Datenordner und in den Protokollen.
- Vorhandene Kataloge, Vorschaubilder, Modelle und Einstellungen ziehen ohne Zutun mit.

### Negativ
- Verknüpfungen auf `src\PhotoViewerV3.Wpf\bin\…\Lumora.exe` zeigen ins Leere; der Debug-Build liegt jetzt unter
  `src\Lumora.Wpf\bin\Debug\net10.0-windows\Lumora.exe`. Der alte Ordner `src\PhotoViewerV3.Wpf` (nur Build-Reste) kann
  gelöscht werden.
- Eigene Umgebungsvariablen `PHOTOVIEWERV3_*` (z. B. `PHOTOVIEWERV3_FACE_MODELS`) müssen auf `LUMORA_*` umgestellt werden.
- Ältere Pakete (bis 0.4.2) finden nach dem Umzug ihren Katalog nicht mehr und legen einen neuen, leeren unter
  `%LocalAppData%\PhotoViewerV3` an — dann gibt es beide Ordner, und die neue Lumora nutzt weiter `Lumora`.

### Neutral
- Ein Start während eine ältere Lumora läuft, arbeitet mit dem alten Ordner; umgezogen wird beim ersten Start ohne sie.
- Das Wpf-Projekt verliert das Build-Ziel, das veraltete `PhotoViewerV3.exe`-Reste löschte (der neue Ausgabeordner hat
  keine).

## Alternativen

- **Nur Texte und Doku** → verworfen (Herberts Wahl „alles“).
- **Datenordner kopieren statt umbenennen** → verworfen: doppelter Platz (Vorschaubilder, Modelle), langsam, und die
  Frage, wann der alte gelöscht wird.
- **Alten Datenordner weiter benutzen, nur neue Installationen unter `Lumora`** → verworfen: zwei Orte auf Dauer,
  Doku und Support-Hinweise müssten beide nennen.
- **Umgebungsvariablen mit Rückfall auf die alten Namen** → verworfen (siehe Begründung).

## Referenzen

- Code: `src/Lumora.Services/Infrastructure/LegacyAppDataMigration.cs`, `src/Lumora.Wpf/App.xaml.cs`
  (`LogLegacyMigration`), `src/Lumora.Wpf/Shell/AppDataRootOverride.cs`
- Tests: `tests/Lumora.Tests/Database/LegacyAppDataMigrationTests.cs`
- Skripte: `build/run-smoke.ps1`, `build/publish.ps1`, `build/make-icon.ps1`, `build/geonames/Build-GeoNames.ps1`
