# PhotoViewer V3 — Projektprofil für Claude

Native Windows-Desktop-App (C# / .NET 10 / WPF / WPF-UI) zum Ansehen, Bewerten, Sortieren und
Verschieben von Foto- und Video-Bibliotheken (50k–500k Dateien) mit SQLite-Katalog. Nachfolger
der PowerShell-Web-App V2 (`../03_Foto-Viewer-V2`), die bis zur Ablösung im Alltag läuft.

- Plan: `PLAN.md` · Entscheidungen: `docs/adr/` · Messungen: `docs/spikes/` · Mockups: `mockups/`
- Doku, Kommentare und Commit-Messages auf Deutsch; Code-Bezeichner auf Englisch.

## Skill-Profil

Branch-Policy: current

### Commit
- Module: Domain, Data, Services, Wpf, Tests, Docs, Deps, Build
- Versionsquelle: changelog:CHANGELOG.md
- Versionsregel: Vorabversion 0.x — jede eingespielte Änderung zählt hoch; Feature → MINOR nur beim Abschluss einer Welle/eines Releases, sonst PATCH
- Push-Policy: nie automatisch (Repo liegt in OneDrive, Push erzeugt Sync-Konflikte in `.git/objects`)
- Pre-Commit-Checks: build, unit
- Doku-Check:
  - CHANGELOG.md → bei jedem Commit (oberster Eintrag = Version)
  - docs/adr/ → neue oder revidierte Architekturentscheidung (neues ADR; altes auf „Superseded“, nie überschreiben)
  - docs/adr/README.md → neues ADR in die Übersicht
  - PLAN.md → Scope-, Phasen- oder Schema-Änderung
  - README.md → neue Bedienung oder Voraussetzungen

### Checks
- build: `dotnet build PhotoViewerV3.slnx -c Debug --nologo -v q` (src/**, tests/**, *.props, *.slnx)
- unit: `dotnet test tests/PhotoViewerV3.Tests --nologo -v q` (src/**, tests/PhotoViewerV3.Tests/**)

## Code-Profil
- Stacks: csharp-wpf
- Build/Lint: siehe Checks (`build`)
- Pflicht-Docs: PLAN.md Kap. 3 (Solution-Layout), 4 (Schema), 12 (Offline-vs-Missing), 13 (Thumbnails) → betroffene ADRs in docs/adr/ → docs/spikes/ bei Performance-Fragen
- Aufgabenquelle: Auftrag im Chat bzw. Brief des Agenten (Ziel + Akzeptanzkriterien); kein ClickUp
- Schichten / Kopplung: Domain (keine Abhängigkeiten) ← Data (EF Core CRUD + RawSql für Bulk/FTS) ← Services (WPF-frei: Interfaces + Implementierungen, `AddPhotoViewerServices`) ← Wpf (Views/ViewModels mit CommunityToolkit.Mvvm, DI in `App.xaml.cs`). Entkopplung zwischen Modulen über `WeakReferenceMessenger` mit den Records aus `Services/Messaging`. Neuer Service = Interface + Registrierung; neues Modul registriert sich in einer eigenen Datei `XyzServiceCollectionExtensions.cs` (`AddXyz()`), die in `App.xaml.cs` bzw. `AddPhotoViewerServices` mit einer Zeile aufgerufen wird.
- Tests: unit (xUnit + FluentAssertions, `tests/PhotoViewerV3.Tests`); Perf- und Spike-Harness: `tests/PhotoViewerV3.PerfTests`
- Auslieferung: keine nach jedem Commit (Alltag: lokaler Debug-Build). Portables Paket bei Bedarf bzw. zum Release: `pwsh -File build/publish.ps1` (self-contained win-x64 → `artifacts/Lumora-<Version>-win-x64` + ZIP), danach `pwsh -File build/run-smoke.ps1 -Exe <Paket>\Lumora.exe -TestLib <Testbibliothek>`. Produktname „Lumora“ (ADR-0047); Projekt-/Namespace-Namen, Datenordner `%LocalAppData%\PhotoViewerV3` und `PHOTOVIEWERV3_*` bleiben
- Mockup-Pflicht: nein (HTML-Mockups in `mockups/` sind die Design-Vorlage)
- Notiz-Ort: PLAN.md Kap. 15 (Risiken) bzw. CHANGELOG.md Abschnitt „Offen“
- Entscheidungs-Ort: docs/adr/

## Regeln für Arbeit im Repo
- Kein `git push` ohne ausdrücklichen Auftrag.
- Keine WPF-Referenz in Domain/Data/Services (WIC-Renderer lebt deshalb im Wpf-Projekt hinter einem Services-Interface).
- Seiten-Namespaces dürfen keine Entitätsnamen verdecken (deshalb `PhotoViewerV3.Libraries`, nicht `.Library`).
- Thumbnail-Dateien sind FileId-basiert (`thumbs\{libraryId}\{fileId}_{size}_still.jpg`). FileIds bleiben stabil; wer Files-Zeilen löscht, entfernt auch Thumbnails-Zeile, Cache-Datei und FTS-Eintrag.
- `FilesFts` ist eine FTS5-External-Content-Tabelle (`content='Files'`, `rowid = Files.Id`) ohne Trigger. Nach INSERT: `INSERT INTO FilesFts(rowid, Name, Extension) …`. Vor DELETE/UPDATE: `INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) VALUES('delete', …)` mit den ALTEN Werten. Notnagel: `INSERT INTO FilesFts(FilesFts) VALUES('rebuild')`.
- SQLite: WAL, `busy_timeout` 5000; lange Schreibläufe in einer Transaktion; nur eine schreibende Verbindung zur Zeit (Single-Writer, z. B. `SemaphoreSlim`).
- Datei-Operationen immer mit `OperationBatch` + `FileOperation` protokollieren (ADR-0012); Papierkorb statt Hard-Delete.
- Keine externen Sample-Assets im Produktivpfad (nur Spike-Code darf laden). Ausnahme: Laufzeit-Modelle (Gesichtserkennung buffalo_l), die der Benutzer nach Bestätigung selbst herunterlädt — nie ins Repo, nie in die portable Version (Lizenz nur nicht-kommerziell, ADR-0035).
- Keine `Wpf.Ui.Controls.MessageBox` verwenden (Layout-Schleife bei umbrechendem Text mit Eingabefeld, v0.2.0-Hänger). Dialoge laufen über `IDialogService` bzw. `Shell/Dialogs/AppDialogWindow`.
- Fenster erben von `Shell/Dialogs/AppFluentWindow` statt direkt von `FluentWindow` (WPF-UI-4-TitleBar wirft sonst „Hwnd mit dem Wert Null“, wenn ein Fenster vor dem ersten Zeichnen geschlossen wird; 0.2.13).
- Galerie-Kacheln bleiben im UI-Automation-Baum Blätter (`GalleryListBox`/`GalleryListBoxItem`); viele Vorschaubilder gebündelt mit Background-Priorität an den UI-Thread geben. Sonst blockiert `AutomationPeer.UpdateSubtree` den UI-Thread, sobald ein Automation-Client lauscht (Visual Studio Live Visual Tree).
