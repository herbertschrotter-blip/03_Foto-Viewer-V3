# ADR-0017: Modul-Entkopplung über WeakReferenceMessenger und Modul-Registrierungen

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0001 (CommunityToolkit.Mvvm), ADR-0002 (Multi-Project Solution), ADR-0013 (Status-UI)

## Kontext

Mit Release 0.2.0 entstanden in zwei Wellen parallel die Module Bibliothek, Galerie, Lightbox, Ordnerbaum,
Einstellungen, Statusleiste und Sortierer — gebaut von mehreren Agenten gleichzeitig in getrennten Worktrees. Viele
Ereignisse interessieren mehrere Module: ein abgeschlossener Scan (Galerie, Ordnerbaum, Statusleiste), eine
Datei-Operation (Galerie, Lightbox, Ordnerbaum, Statusleiste), ein Favoriten-Toggle (Galerie und Lightbox zeigen
dieselbe Datei), eine Ordnerauswahl (Ordnerbaum → Galerie). Direkte Abhängigkeiten zwischen ViewModels hätten
zyklische Referenzen, Merge-Konflikte in `App.xaml.cs` und schwer testbare Kopplungen erzeugt.

## Entscheidung

1. **Fachliche Ereignisse sind Nachrichten-Records in `Services/Messaging/Messages.cs`** (plus
   `ScanAbortedMessage` in `Services/Scanning`): `ActiveLibraryChangedMessage`, `ScanProgressMessage`,
   `ScanCompletedMessage`, `ScanAbortedMessage`, `FilesChangedMessage` (Kind `Metadata`/`Moved`/`Removed`/`Added`
   mit den betroffenen FileIds), `FolderSelectedMessage`. Transport ist `WeakReferenceMessenger.Default`
   (CommunityToolkit.Mvvm.Messaging, WPF-frei). Sender sind die Services (`ScanService`, `FileOperationService`,
   `CatalogUpdateService`, `LibraryService`) bzw. der Ordnerbaum; Nachrichten können von Worker-Threads kommen —
   UI-Empfänger dispatchen selbst auf den UI-Thread.
2. **Jedes Modul registriert seine Dienste in einer eigenen Datei `XyzServiceCollectionExtensions.cs`**:
   `AddLibrary`, `AddOperations`, `AddCatalog`, `AddSorter` in Services (gebündelt in `AddLumoraServices`),
   `AddLightbox`, `AddShell`, `AddGallery`, `AddSorterUi` im Wpf-Projekt (je eine Zeile in `App.xaml.cs`).
   Module kennen einander nur über Services-Interfaces und Nachrichten, nie über fremde ViewModels; Dienste, die ein
   Testlauf ersetzen soll, werden per `TryAdd` registriert, damit der Rauchtest seine Fakes vorher setzen kann.

## Begründung

- Parallele Entwicklung: jedes Modul hat seine Registrierungsdatei und reagiert auf Nachrichten, ohne dass ein
  zentrales ViewModel alle Module kennt.
- Services bleiben WPF-frei (ADR-0002); der Messenger aus CommunityToolkit.Mvvm ist bereits im Stack (ADR-0001) und
  braucht keine zusätzliche Abhängigkeit.
- Schwache Referenzen verhindern, dass ein vergessenes `Unregister` ein ViewModel am Leben hält.
- Testbarkeit: Services nehmen den Messenger optional im Konstruktor (Tests sammeln Nachrichten mit einem eigenen
  `WeakReferenceMessenger`), ViewModels lassen sich mit Fakes der Interfaces bauen.

## Konsequenzen

### Positiv
- Galerie, Lightbox, Ordnerbaum und Statusleiste bleiben nach jeder Operation synchron, ohne sich zu kennen.
- Neue Module (z. B. Tags, Plan-Ordner) hängen sich an bestehende Nachrichten an, statt Sender anzufassen.
- `App.xaml.cs` bleibt kurz; Konflikte beim Zusammenführen paralleler Arbeit sind selten.

### Negativ
- Der Nachrichtenfluss ist für den Compiler unsichtbar: Wer eine Nachricht sendet, sieht nicht, wer reagiert.
  Deshalb dokumentiert jede Nachricht Sender, Thread und Bedeutung, und jede Empfängerklasse nennt im
  Klassenkommentar, worauf sie reagiert.
- Keine Reihenfolgegarantie zwischen Empfängern; Empfänger müssen idempotent und debounced sein (Galerie bündelt
  Reloads 300 ms, der Ordnerbaum ebenfalls).
- Schwache Referenzen setzen voraus, dass Empfänger leben (Singleton-ViewModels); transiente Empfänger müssen sich
  explizit abmelden (`LightboxViewModel.Dispose`).

### Neutral
- Nachrichten sind Teil des Vertrags der Services-Schicht; eine Änderung ist eine Vertragsänderung (CHANGELOG).
- Seiten, die denselben Zustand zeigen (Galerie-Seite und Ordner-Seite), teilen ein Singleton-ViewModel innerhalb des
  Moduls — das ist keine Modulkopplung.

## Alternativen

- **.NET-Events auf den Services-Interfaces** → verworfen weil: Abonnenten müssen den Sender kennen und sich
  abmelden; Lebensdauer-Probleme bei transienten ViewModels.
- **Prism/EventAggregator oder MediatR** → verworfen weil: weitere Abhängigkeit, CommunityToolkit reicht.
- **Ein zentrales ShellViewModel, das alle Module verdrahtet** → verworfen weil: Gott-Objekt, bei paralleler
  Entwicklung ständiger Konfliktpunkt.

## Referenzen

- [src/Lumora.Services/Messaging/Messages.cs](../../src/Lumora.Services/Messaging/Messages.cs)
- [src/Lumora.Services/Scanning/ScanMessages.cs](../../src/Lumora.Services/Scanning/ScanMessages.cs)
- [src/Lumora.Services/ServiceCollectionExtensions.cs](../../src/Lumora.Services/ServiceCollectionExtensions.cs)
- [src/Lumora.Wpf/App.xaml.cs](../../src/Lumora.Wpf/App.xaml.cs)
- [CLAUDE.md](../../CLAUDE.md) (Code-Profil „Schichten / Kopplung“)
