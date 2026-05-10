# Runde 1 — ChatGPT (GPT-5) Antwort

> Original-Antwort von GPT-5 im Canvas "Review Runde 1"
> Datum: 2026-05-10

---

Claude,
ich sehe den Plan insgesamt als solide Richtung, aber an mehreren Stellen ist er noch zu web-/service-denkend und teilweise zu breit für ein MVP. Der bestehende PowerShell-Stand bestätigt das: `start.ps1` ist aktuell Launcher, Config-/State-Loader, HTTP-Server, Route-Dispatcher, Runspace-Orchestrator und teilweise UI-Komposition in einem. Die App hat reale Features, aber keine Architektur, die man portieren sollte. Der C#-Port sollte eine fachliche Neumodellierung werden, kein technischer Nachbau.

## Kernempfehlungen vorab

- Stack: WPF + .NET 10 + CommunityToolkit.Mvvm + SQLite/EF Core ist für diesen User-Fall richtig.
- Video: LibVLCSharp als Player ist die richtige Default-Wahl, nicht `MediaElement`.
- Bild-Thumbnails: WPF/WIC zuerst, ImageSharp nur gezielt für Formate/Operationen, bei Lizenz/Footprint bewusst entscheiden.
- DB: Schema ist grundsätzlich tragfähig, aber `Plans`, `History`, `Thumbnails`, `Hashes`, `Library/Catalog` brauchen klarere Grenzen.
- Plan-Ordner: strikt 1 Datei → maximal 1 Ziel pro Plan. Kein M:N für Apply-MVP.
- MVP: viel kleiner schneiden. Erst „Ordner wählen → scannen → Grid → Lightbox → sichere Dateioperationen → Thumbnail-Queue". Plan-Ordner erst danach.

## ✅ Einigkeit
- Native Desktop statt HTTP-Server ist die richtige Richtung
- WPF + .NET 10 ist für Windows-only und Medien-/Dateisystem-Fokus die beste pragmatische Wahl
- CommunityToolkit.Mvvm passt
- LibVLCSharp ist für gemischte Codecs die richtige primäre Player-Wahl
- SQLite + EF Core gerechtfertigt, wenn Raw SQL für Spezialfälle erlaubt bleibt
- Plan-Ordner als neues zentrales Konzept ist stark und besser als die alte File-Sorter-Engine
- Thumbnail-Prioritäten sind wichtig und sollten nicht als Nebensache behandelt werden
- Papierkorb statt Direktlöschen ist korrekt

## ⚠️ Widerspruch
- `MediaElement` nicht als ernsthafte Alternative für diesen Codec-Mix
- `PriorityQueue<T,P>` allein zu schwach für Preemption/Dedupe/Repriorisierung
- `History.PlanId` reicht nicht für Undo eines Plan-Apply → `OperationBatch` + Einzeloperationen
- Plan-Ordner und Albums nicht in eine Tabelle zusammenlegen
- M:N-Zuweisung innerhalb eines Plans für Apply-MVP falsch → `UNIQUE(PlanId, FileId)`
- Animierte GIFs für alle Videos vorab als Default zu speicherintensiv
- "Container-Ordner zeigt leeres Grid" ist UX-riskant
- ImageSharp nicht automatisch als Default; WIC/WPF-native zuerst benchmarken
- Phase 8/9 zu voll, mehrere Features als separate Epics

## ❓ Rückfragen
1. Soll die App langfristig mehrere Libraries/Catalogs verwalten können?
2. Ist „Plan anwenden" fachlich immer Move, oder muss Copy als gleichwertiger Modus früh rein?
3. Soll Undo nach Papierkorb-Löschen nur „Papierkorb öffnen" bedeuten, oder wirklich automatisiertes Restore?
4. Wie groß ist die Zielbibliothek realistisch: 10k / 100k / 500k Dateien?
5. Gibt es echtes HEIC/RAW-Material?
6. Cache global pro User, oder später portable Catalogs inklusive Cache neben Library?
7. Hat der User mehrere Monitore/Vollbild-Workflows?
8. C#-Port bewusst alle PowerShell-Features ersetzen, oder ist ein schlankerer Foto-/Video-Kern wichtiger als Feature-Parität?

## Konkrete Schema-Korrektur (MVP-Start)

Inkl. `Libraries`, `Folders`, `Files`, `Thumbnails`, `OperationBatches`, `FileOperations` —
Plan-Tabellen erst V0.3, aber `OperationBatches` früh.

## Vorgeschlagene Phasen (V0.1–V0.3+)

- **V0.1**: App startet nativ + Ordner wählen + Scan + FolderTree + Grid + Lightbox-Bilder + Löschen-Papierkorb + Thumbnail-Queue + Settings
- **V0.2**: Video-Metadaten + LibVLCSharp Lightbox + Move/Copy/Rename + OperationBatch/Undo
- **V0.3**: Plan-Ordner (CRUD + Zuweisung + Preflight + Apply + Undo)
- **Spätere Epics**: Suche/Tags/Favoriten, Sorter, Archive, Animated Hover, Flatten, Faces

## Spikes vor Implementation
1. WPF virtualisiertes Wrap/Grid mit 50.000 Dummy-Items
2. LibVLCSharp in eigenem Lightbox-Window mit WMV3/MPEG-2 Testdateien
3. WIC Thumbnail vs. ImageSharp Benchmark auf 1.000 realen Bildern
4. SQLite WAL + parallele Reader + serialisierte Writes
5. FFmpeg cancel/timeout bei kaputtem/großem Video
