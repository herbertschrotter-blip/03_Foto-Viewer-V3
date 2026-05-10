# Review Runde 2 — Foto-Viewer V2: Vertiefung & Constraint-Validierung

## Gesprächsformat

Wir setzen das Review aus Runde 1 fort. Du bist weiter der erfahrene **.NET-/WPF-Architekt** im Gespräch mit Claude.

- Sprich direkt zu Claude, nicht zum User
- Schreibe deine GESAMTE Antwort in **Canvas mit Titel "Review Runde 2"**
- Fasse am Ende zusammen: ✅ Einigkeit | ⚠️ Widerspruch | ❓ Rückfragen

## Repo-Zugriff

- **Repo:** `herbertschrotter-blip/03_Foto-Viewer-V2`
- **Branch: `main`** (PowerShell-Stand v1.0.0, der C#-Port ist noch nicht im Repo)

---

## Was passiert ist

Deine Runde-1-Antwort war stark. Claude geht mit fast allen Punkten mit (siehe ✅ unten). Der User hat 4 Architektur-Entscheidungen getroffen, die jetzt als **harte Constraints** für Runde 2 gelten — und bittet dich für die wichtigsten Punkte um eine **tiefere Begründung**, bevor er commitet.

## Neue harte Constraints (vom User)

| Constraint | Wert | Konsequenz |
|---|---|---|
| **Bibliotheks-Größe** | 50.000–500.000 Dateien | groß — FTS5, pHash-Index, sorgfältige Indizes, batched Enqueue, Memory-Disziplin Pflicht |
| **Formate** | nur JPG/PNG/MP4 (kein HEIC, kein RAW) | WIC reicht ohne Codec-Pack-Komplikation |
| **Plan-Apply-Modus** | nur Move im MVP, Copy später | Plan-Apply-Logik vereinfacht |
| **Catalog-Modell** | zentrale DB unter `%LocalAppData%\FotoViewerV2\library.db` für MVP | portable Catalogs später (Schema soll das aber nicht ausschließen) |

## Defaults, die Claude für GPT-5s offene Rückfragen vorschlägt

Bitte Stellung nehmen: stimmst du jedem Default zu?

| GPT-5-Rückfrage | Claudes Default-Vorschlag |
|---|---|
| Mehrere Libraries verwalten? | **Nein für MVP** (eine aktive Library), aber `Libraries`-Tabelle und `LibraryId`-FKs schon im V0.1-Schema |
| Apply: nur Move oder Copy auch? | **Nur Move für MVP** ✓ (User-Entscheidung) |
| Undo nach Papierkorb-Löschen | **MVP: nur "Papierkorb öffnen"-Link**. Echtes Auto-Restore in Phase 8 (Shell-API komplex, kann fehlschlagen) |
| HEIC/RAW | **Nein** ✓ (User-Entscheidung) |
| Cache global oder portable | **Global pro Windows-User** für MVP. Portable Catalog optional später |
| Mehrere Monitore / Vollbild | **Ja, unterstützt** — Lightbox als eigenes Window dank deiner Empfehlung sowieso |
| Feature-Parität vs. Kern | **Schlanker Foto-/Video-Kern wichtiger als Parität.** PowerShell-Features kommen nur zurück, wenn sie sich im neuen Modell sauber einfügen |

## ✅ Übernommen aus Runde 1 (kein Widerspruch von Claude/User)

- WPF + .NET 10 + CommunityToolkit.Mvvm + WPF-UI als UI-Layer (mit Isolations-Interface)
- LibVLCSharp als Default-Player
- SQLite + EF Core (Raw SQL für FTS5/Hashes/Bulk)
- Plan-Ordner als zentrales neues Konzept (statt File-Sorter-Engine portieren)
- WIC/WPF native für Bild-Thumbnails (statt ImageSharp default)
- `Libraries` als Anker, `FileHashes` separat, `Thumbnails` mit Kind/Format/ErrorCode
- `OperationBatches` + `FileOperations` für sichere Operationen + Block-Undo
- `PlanAssignments` mit `UNIQUE(PlanId, FileId)` — 1:1 für Apply-MVP
- Multi-Channel Queue + Dedupe-Dictionary mit Versioning für Thumbnail-Generation
- Animated Hover-Thumbs on-demand mit LRU-Cache + Größengrenze (kein Vorab-Generate für alle Videos)
- Lightbox als eigenes Window (rahmenlos)
- Phase 8/9 entzerrt: Face Recognition als separates Epic, Sorter erst nach Plan-Ordner-MVP
- 5 Spikes vor Implementation (virtualisiertes Grid, LibVLCSharp, WIC-Benchmark, SQLite WAL, FFmpeg cancel/timeout)
- "Tiefster Ordner zeigt leeres Grid" → Container-State mit Subfolder-Kacheln

## Eine kleine Differenzierung von Claude

- **`Persons` + `Faces`-Tabellen schon im V0.1-Schema anlegen** (auch wenn leer und ungenutzt), damit später keine Migration nötig wird. Frühphase erlaubt zwar "DB löschen", aber wenn der User dann schon Tags/Plans hat, will er die nicht verlieren.

---

## Aufgabe für Runde 2

### 1. Tiefe Begründung für 5 strittige Architekturpunkte

Der User möchte für die folgenden Punkte deine **detaillierte Begründung mit Code/Beispielen/Zahlen**, bevor er commitet. Bitte konkret werden, **explizit gegen die Bibliotheks-Größe 50k–500k argumentieren**.

#### 1a. WIC vs. ImageSharp

- Konkrete Performance-Zahlen aus deiner Erfahrung: wie schnell ist WIC `BitmapDecoder` + `TransformedBitmap` vs. ImageSharp bei 100 JPEG-Bildern à ~5 MB (typische Smartphone-Fotos)?
- ImageSharp v3 Lizenz-Situation 2026: ist es wirklich problematisch für eine private Foto-App, oder ist die "Six Labors Split License" handhabbar?
- Memory-Leak-Risiko: WIC mit `BitmapCacheOption.OnLoad` reicht wirklich, oder gibt es bekannte Fallen bei 500k Files Bulk-Generierung?
- Was ist mit **GIF/WebP-Generierung** für animierte Hover-Thumbs? WIC kann WebP-Encoding nicht nativ. Brauchen wir hier ImageSharp oder externes FFmpeg auch für statische Bilder?

#### 1b. Multi-Channel Queue für Thumbnail-Generation

Bitte den **vollständigen Service** zeigen (kein Pseudo-Code), inklusive:

- 4 Channels (Critical/Viewport/Current/Background)
- ConcurrentDictionary-basierte Dedupe mit `ThumbKey` und `Version`
- Repriorisierung: wenn `Background`-Job kommt während gleicher Key in `Viewport`, was passiert?
- Stale-Job-Detection beim Dequeue
- Worker-Pool mit unterschiedlichen Limits (Image-Worker `CPU-2`, FFmpeg-Worker max 1–2)
- FFmpeg-Job mit `CancellationTokenSource.CancelAfter(3s)`
- Wie skaliert das auf 500.000 Files? Memory-Footprint des Dedupe-Dictionaries (500k × ~50 bytes = 25 MB — OK, aber bei Re-Scan?)
- Was passiert beim **App-Crash mitten in der Generation**? Reicht der DB-Vergleich `Files.Modified > Thumbnails.GeneratedAt`, oder brauchen wir zusätzlich `Thumbnails.Status = 'pending'`?

#### 1c. OperationBatches: Crash-Recovery-UI

Du hast das Schema gezeigt. Jetzt bitte das **Recovery-UX-Detail**:

- Wenn beim App-Start ein `Status = 'applying'`-Batch gefunden wird: was zeigt die UI dem User?
- "Fortsetzen" — wie genau? Resume ab letzter `done`-Operation, oder nochmal alle pending durchgehen?
- "Reparieren" — was wird repariert? Inkonsistenzen DB ↔ Dateisystem?
- Was, wenn der User mitten im Plan-Apply die App killt und beim Neustart die Quelldateien manuell verschoben hat? Wie gehen wir mit **partiell ausgeführten Batches** um, deren Quellpfad nicht mehr existiert?
- Sollte bei jedem App-Start automatisch ein **Konsistenz-Check** laufen (DB → Dateisystem-Existenz), oder nur on-demand?

#### 1d. Animated Hover-Thumbs on-demand

Du sagst on-demand mit `GenerateOnHoverDelayMs: 350` und `MaxAnimatedCacheSizeGB: 2`. Bitte konkret:

- Wie funktioniert das **Lifecycle-Management** in einem virtualisierten Grid? Wenn User 2 Sekunden über Thumb hovert, dann scrollt weiter, dann zurückscrollt — wird der Generation-Job storniert oder weitergeführt?
- Wie verhält sich der LRU-Eviction wenn User langsam durch 1000 Videos hovert? Brauchen wir eine **Mindest-Lebensdauer** im Cache, oder akzeptieren wir Re-Generation?
- Wenn der `MaxAnimatedCacheSizeGB`-Limit erreicht ist während aktiver Hover-Sessions: blockierende Wartezeit oder Skip?
- Soll der animierte Hover-Modus standardmäßig **AN** oder **AUS** sein? Bei 50k–500k Videos ist das eine relevante Default-Entscheidung.

#### 1e. MVP-Schnitt V0.1 / V0.2 / V0.3 — sinnvoll für 50k–500k Bibliothek?

Bei einer 500k-Bibliothek dauert der Erst-Scan möglicherweise Stunden. Stimmt dein V0.1-Schnitt noch?

- Sollte **FTS5-Suche** (oder simple LIKE-Suche) schon ins V0.1, weil ohne Suche bei 500k Files das Grid unbenutzbar ist?
- Sollte **Filter "Bilder/Videos/Favoriten"** auch ins V0.1?
- Reicht der V0.1-Funktionsumfang für einen User, der die App **produktiv** nutzen will, oder ist das Stage-1 nur für Entwickler-Validation?
- Bei **5 technischen Spikes vor V0.1**: wie lange schätzt du realistisch für die Spikes (jeweils)?

### 2. Skalierungs-Gefahren bei 50k–500k Files

Welche **konkreten Gefahren** siehst du bei dieser Größe, die im aktuellen Plan noch nicht adressiert sind? Mögliche Themen (du kannst priorisieren):

- **Initial-Scan-Performance**: 500k `File.GetAttributes` + EF-Inserts — wie lang? Welche Optimierungen Pflicht (Bulk-Insert, EF-Core-`AddRange` vs. Raw SQL `INSERT`)?
- **DB-Größe**: bei 500k Files mit allen Tabellen (Files, Exif, FileTags, Thumbnails-Metadaten, OperationBatches-Historie, …) — Faustregel?
- **DB-Locking**: WAL + Channel-Writer reicht wirklich, oder bei 500k-Re-Scan zusätzliche Maßnahmen?
- **WPF-VirtualPanel-Limits**: ab welcher Item-Anzahl wird selbst das virtualisierte Grid träge? Ist 500.000 Items im selben Container überhaupt machbar, oder müssen wir zwingend nach Ordner / Datum gruppieren und nur Teilmengen rendern?
- **Memory-Leak-Risiken**: häufige Verdächtige bei langer Laufzeit (Bitmap-Streams, EF-Tracking, WeakRef in Bindings)?
- **Erst-Indexierung als Background-Service**: was, wenn der User mitten im Scan einen Ordner öffnet, der noch nicht indiziert ist?

### 3. Ordnung des MVP-Plans

Mit den neuen Constraints (50k–500k, JPG/PNG/MP4, Move-only):

- **Schiebst du Phasen um**? Sollte z. B. ein simpler Filter (Bilder/Videos/Favoriten) und Schnellsuche schon ins V0.1?
- **Was würdest du aus V0.1 streichen**, wenn ich pragmatisch in 4 Wochen einen MVP haben will?
- **Welche Spikes** würdest du **vorziehen** (vor V0.1 statt parallel)?
- Gibt es **versteckte Abhängigkeiten**, die du in Runde 1 noch nicht erwähnt hast und die mit den neuen Constraints relevant werden?

### 4. Eine offene Frage von Claude

Du hast `MaxImageWorkers = max(1, CPU - 2)` und `MaxVideoWorkers = 1` empfohlen. Bei einer **500k-Erst-Indexierung im Hintergrund** wird der User die App trotzdem benutzen wollen. Wie stark sollte der Background-Worker bei UI-Aktivität gedrosselt werden? Reicht `PauseBackgroundDuringVideoPlayback`, oder brauchen wir ein **Adaptive Throttling** basierend auf UI-Frame-Rate / Eingabe-Aktivität / CPU-Auslastung?

---

Sei wieder direkt und kritisch. Der User commitet, sobald deine Begründung ihn überzeugt — also bitte mit Zahlen, Code, konkreten Erfahrungen, nicht nur Argumenten.
