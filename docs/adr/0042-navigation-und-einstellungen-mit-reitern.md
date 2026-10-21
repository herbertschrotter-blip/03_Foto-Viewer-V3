# ADR-0042: Schlankere Navigation, Einstellungen mit Reitern, Info-Seite

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Wunsch und Abnahme Mockup 06, Reiter „Navigation & Einstellungen“),
  Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** ADR-0003 (eine aktive Bibliothek), ADR-0020 (Wurzeln: Bibliotheksordner + Eingänge), ADR-0021
  (Tags), ADR-0027 (Kategorien), ADR-0035 (Gesichtserkennung, Lizenz buffalo_l), ADR-0037 (Personen-Seite)

## Kontext

Die linke Leiste hatte acht Einträge: Bibliothek, Galerie, Ordner, Sortieren, Personen, Tags, Kategorien und
Einstellungen. Bibliothek, Tags und Kategorien richtet man selten ein; täglich gebraucht werden Galerie, Ordner,
Personen und Sortieren. Die Einstellungen waren eine lange Seite mit zehn Karten untereinander, die Version stand in
einer kleinen Karte „Über“. Vergeben von Tags und Kategorien geschieht ohnehin in der Galerie (Rechtsklick, Strg+T,
Strg+K, Lightbox). Ein Eingang wird oft hinzugefügt — bisher nur über die Seite „Bibliothek“.

## Entscheidung

1. **Linke Leiste**: oben ein **Bibliotheks-Umschalter** (`PaneHeader` der WPF-UI-`NavigationView`,
   `Shell/LibrarySwitcher` + `LibrarySwitcherViewModel`): aktive Bibliothek mit Namen, Klick → Menü aller Bibliotheken
   (aktive markiert, Dateizahl) und „Bibliotheken verwalten…“; ohne Bibliothek führt der Klick direkt zur Verwaltung.
   Während eines Scans der Bibliotheksverwaltung wird nicht gewechselt (wie „Aktiv setzen“). Neu gelesen wird bei
   Bibliothekswechsel, nach Scans, beim Öffnen des Menüs und beim Verlassen der Einstellungen — nicht bei jeder
   Navigation, weil `GetAllAsync` über alle Dateien zählt.
   Darunter **Galerie, Ordner, Personen, Sortieren**, unten **Einstellungen**.
2. **Einstellungen mit Reitern oben** (`TabControl` mit eigenem Pivot-Stil: Text, gewählter Reiter fett mit
   Akzent-Strich unten, Trennlinie): Bibliotheken & Eingänge · Tags · Kategorien · Gesichter · Archive · Darstellung ·
   Speicher & Wartung · Info. Die bisherigen Seiten `LibraryPage`, `TagManagementPage` und `CategoryManagementPage`
   werden **unverändert als Reiter-Inhalt eingebettet** (Singletons aus DI, nur ihre Seitenüberschrift entfällt); die
   übrigen Abschnitte der alten Einstellungsseite sind auf eigene `UserControl`s in `Settings/Sections` verteilt (alle
   mit dem `SettingsViewModel` als DataContext). Die Ordnerüberwachung hängt unter der Bibliotheksliste
   (`LibraryPage.SetFooter`). Die alte Karte „Über“ ist in „Info“ aufgegangen.
3. **Navigation auf einen Reiter** über die vorhandenen Tags: `NavigationTags.Library/Tags/Categories` bleiben und
   bekommen Geschwister (`settings-faces`, `-archives`, `-appearance`, `-storage`, `-info`). Der `NavigationService`
   erkennt Reiter-Tags, setzt `SettingsSectionState.SelectedSection` (Singleton) und navigiert zu `settings`;
   `CurrentTag` ist nie ein Reiter-Tag. Die Seite gleicht den angezeigten Reiter in beide Richtungen mit dem Zustand ab
   (nur `SelectionChanged` der Reiterleiste selbst zählt). So funktionieren „Tags verwalten…“/„Kategorien
   verwalten…“ der Galerie, `NavigateTo(Library)` der Bibliotheksverwaltung und die Startseite ohne Codeänderung.
4. **Start**: mit aktiver Bibliothek die Galerie, ohne Bibliothek (Ersteinrichtung) **Einstellungen › Bibliotheken &
   Eingänge** (`ShellViewModel.ResolveStartPageAsync` liefert weiterhin `library`).
5. **„Eingang hinzufügen…“ zusätzlich im Kopf der Seite „Ordner“**: `FolderTreeViewModel.AddInboxCommand` ruft
   `IInboxActions.AddInboxToActiveLibraryAsync` (implementiert vom `LibraryViewModel`) — derselbe Ablauf wie der Knopf
   in den Einstellungen; Fehler als Dialog. Ein dort gestarteter Scan bleibt auf der Seite „Ordner“.
6. **Info** (`Settings/Info`): Version = InformationalVersion der App = **oberster Eintrag `## [x.y.z]` der
   CHANGELOG.md** (setzt `PhotoViewerV3.Wpf.csproj` beim Build — unverändert seit 0.2.x, jetzt mit Test), Build-Datum
   = Änderungszeit der App-DLL, Debug/Release per `#if DEBUG`; Karten Entwicklung (ohne E-Mail-Adresse), Dieses System
   (.NET, Windows-Build, Grafikkarte über `user32!EnumDisplayDevices`, Ausführung der Gesichtserkennung, Modellzustand,
   Katalogzahlen), Speicherorte (Explorer), Verwendete Bausteine (Versionen per `FileVersionInfo` der DLLs neben der
   App, SQLite-Version aus einer In-Memory-Verbindung) mit „Alle Lizenzen anzeigen…“ (fester Text, `AppDialogWindow`
   mit Details), „Versionsinfo kopieren“ (ohne Pfade/Bibliotheksnamen) und „Änderungsprotokoll“ (CHANGELOG.md wird ins
   Ausgabe- und Paketverzeichnis kopiert; fehlt sie, ist der Knopf ausgeblendet). Kein Netzzugriff.

## Begründung

- Die Leiste zeigt nur, womit man täglich arbeitet; Einrichtung sammelt sich an einem Ort.
- Bestehende Seiten einbetten statt neu schreiben hält Verhalten, Tests und Rauchtest-Pfade der Verwaltungen stabil.
- Reiter-Tags im vorhandenen `INavigationService` (statt eines zweiten Dienstes oder einer Message) lassen alle
  bisherigen Aufrufer (`NavigateTo(Tags)` in der Galerie, `NavigateTo(Library)` in der Bibliotheksverwaltung) ohne
  Änderung weiterlaufen — wichtig, weil Galerie-Dateien parallel umgebaut werden.
- Die Version kam schon aus der CHANGELOG (csproj); ein zweites MSBuild-Target in einer neuen `Directory.Build.props`
  hätte nur dasselbe doppelt getan. Ein Test hält Assembly-Version und obersten Eintrag zusammen.
- Grafikkarte per `EnumDisplayDevices` statt WMI: kein neues Paket (`System.Management` liegt nur im Perf-Projekt),
  keine DirectML-Laufzeit laden nur für eine Anzeige.

## Konsequenzen

### Positiv
- Vier Arbeitsseiten in der Leiste, Bibliothekswechsel mit einem Klick von überall.
- Einstellungen übersichtlich; jede Verwaltung hat ihren Reiter; Info zeigt Version und Umgebung für Fehlermeldungen.
- „Eingang hinzufügen…“ dort, wo man die Ordner sieht.

### Negativ
- Die Bibliotheksverwaltung ist einen Klick weiter weg (Einstellungen › Bibliotheken & Eingänge bzw. „Bibliotheken
  verwalten…“ im Umschalter).
- Die eingebetteten Seiten bringen eigene Ränder und Einleitungstexte mit; der Reiter „Bibliotheken & Eingänge“ bleibt
  ausführlicher als im Mockup (Erklärkasten „So funktioniert's“, Scan-Panel) — bewusst, um nichts zu verlieren.
- Fehler-/Info-Leisten des `SettingsViewModel` stehen über den Reitern und bleiben beim Reiterwechsel sichtbar.

### Neutral
- `CurrentTag` meldet für alle Reiter `settings`; wer den Reiter wissen muss, liest `SettingsSectionState`.
- Neue Reiter: Tag in `NavigationTags.SettingsSections` ergänzen, `TabItem` mit `Tag` in `SettingsPage.xaml`.
- Neues Paket: Zeile in `LicenseTexts` und ggf. in `InfoViewModel.BuildComponents` ergänzen.

## Alternativen

- **Eigene Seiten behalten, nur aus der Leiste nehmen** (Unterseiten der Einstellungen per `NavigationView`-Hierarchie)
  → verworfen: WPF-UI-Hierarchie klappt in der Leiste auf, Mockup verlangt Reiter oben.
- **Unterleiste links in den Einstellungen** → verworfen: das abgenommene Mockup 06 zeigt Reiter oben.
- **Navigation über eine Messenger-Message** (`OpenSettingsSectionMessage`) → verworfen: zweiter Weg neben
  `INavigationService`, Aufrufer müssten umgestellt werden.
- **Neue `Directory.Build.props` mit Versions-Target** → nicht nötig, csproj liest die CHANGELOG bereits (siehe oben).

## Referenzen

- Mockup 06 (Reiter „Navigation & Einstellungen“), abgenommen 2026-10-21
- Code: `src/PhotoViewerV3.Wpf/Shell/{INavigationService,NavigationService,SettingsSectionState,LibrarySwitcher*}.cs`,
  `Settings/SettingsPage.xaml(.cs)`, `Settings/Sections/*`, `Settings/Info/*`, `Library/LibraryViewModel.Inbox.cs`,
  `Folders/FolderTreeViewModel.AddInbox.cs`
- Tests: `tests/PhotoViewerV3.Wpf.Tests/Shell/NavigationAndSettingsTabsTests.cs`, `Settings/InfoViewModelTests.cs`;
  Rauchtest-Schritt „Einstellungen-Reiter und Info“ (`Shell/Smoke/SmokeScenario.SettingsTabs.cs`)
