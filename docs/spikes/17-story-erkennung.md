# Spike #17 — Story-Erkennung bei 500 000 Dateien: voll, inkrementell, Übersicht

- **Status:** 🟢 Grün
- **Datum:** 2026-10-22 (v0.4.8)
- **Bezogen auf:** [ADR-0058](../adr/0058-story-erkennung.md), `src/Lumora.Services/Stories/**`, Messung
  `tests/Lumora.Tests/Stories/StoryPerformanceTests.cs`

## Messaufbau

- Rechner: Herberts Entwicklungs-PC (siehe Spike 15), Debug-Build der Tests, Katalog in `%TEMP%` (SSD). Andere Builds und
  Tests liefen teils parallel — Werte schwanken, angegeben sind zwei Läufe nach den Optimierungen.
- Synthetischer Katalog (nur Zahlen, keine Fotos): **500 000 Dateien** über die Jahre ab 2006; an etwa jedem dritten Tag
  1–3 Ereignisse (10 % davon an einem anderen Ort), 1,2 % der Tage Beginn einer 4–14-tägigen Reise zu 1–4 von 300 Orten;
  25 % ohne Koordinaten, 5 % Videos; 20 Personen mit 99 845 bestätigten Gesichtern (Vektoren leer — die Erkennung liest
  nur Person, Datei, Zustand). Uhr fest, Zeitzone UTC.
- Aufruf: `LUMORA_STORY_PERF=1` (optional `LUMORA_STORY_PERF_FILES=<n>`), dann
  `dotnet test tests/Lumora.Tests --filter StoryPerformanceTests --logger "console;verbosity=detailed"`.
- Ergebnis der Erkennung: **2 109 Storys** (1 795 Ereignisse, 65 Reisen, 221 Personen, 28 Orte), **612 011 Einträge**.

## Ergebnisse

| Messung | Lauf 1 | Lauf 2 | Ziel |
|---|---:|---:|---|
| Volle Erkennung, alles neu (lesen + erkennen + schreiben) | 4 596 ms (1 133 + 299 + 3 164) | 4 978 ms (1 564 + 315 + 3 099) | < 5 s ✔ (knapp) |
| … danach Titelbilder (Ersatz-Auswahl, 2 109 Storys) | 2 144 ms | 1 997 ms | Hintergrund |
| Volle Erkennung ohne Änderung | 2 273 ms | 2 012 ms | < 5 s ✔ |
| Inkrementell: 20 neue Fotos am Rand eines Ereignisses | 101 ms | 96 ms | < 0,5 s ✔ |
| Inkrementell: 1 Datei, nichts ändert sich | 14 ms | 14 ms | |
| Übersicht laden, warm (Median aus 5) | 52 ms | 56 ms | < 100 ms ✔ |
| Übersicht laden, kalt (Rückblicke + Zuhause berechnen) | 239 ms | 271 ms | |
| Story-Ansicht (Reise, 339 Fotos) + Highlights | 42 ms | 40 ms | |

Bei 50 000 Dateien (517 Storys): voll 0,26 s + Schreiben 0,49 s, ohne Änderung 0,54 s, inkrementell 43–127 ms.

## Was gemessen und geändert wurde

| Schritt | vorher | nachher | Maßnahme |
|---|---:|---:|---|
| Personen lesen (100 000 Gesichter) | 0,76–0,9 s | ≈ 0 (im Lesen der Zeitachse) | nur `PersonId, FileId` über den abdeckenden Index `IX_Faces_Person_State_File`, Zuordnung zur schon gelesenen Zeitachse im Speicher statt 100 000 Sprüngen in `Files` |
| Zeitachse lesen | 1,1–1,3 s | 0,9–1,5 s | ganze Zeitachse als Tabellen-Scan (`+f.LibraryId`), Zeitversatz der Zone je Stunde zwischengespeichert |
| Titelbilder | 4,3–4,5 s | 2,0–2,1 s | Kandidaten aller Storys in einem Zug über den Primärschlüssel; Ersatz-Auswahl über EINE Verbindung (Verbindungen sind nicht gepoolt, je Öffnen einige ms) |
| Schreiben neuer Einträge | 3,7–4,5 s | 3,1–3,4 s | Mehrfach-INSERT (200 Zeilen je Befehl), keine Aggregat-Nachberechnung für neue Storys |
| Inkrementell | 212–346 ms | 96–101 ms | Zuhause aus dem letzten vollen Durchgang (sonst GROUP BY über alle Orte); Ort „zu Hause“ nie als Orte-Kandidat lesen |
| Übersicht warm | 84–103 ms | 52–56 ms | Rückblicke je Tag zwischengespeichert (Nachrichten verwerfen), Dateizahl nur über den Index `(LibraryId, MediaType)` |

- `StoryItems` als WITHOUT-ROWID-Tabelle (Raw-SQL in der Migration) brachte beim Schreiben keinen messbaren Gewinn
  (3,97 s im selben Lauf) → nicht übernommen, Tabelle bleibt EF-generiert.
- Der Rest beim ersten Schreiben sind die B-Baum-Einfügungen (Tabelle, Primärschlüssel, `IX_StoryItems_File`) und die
  Fremdschlüssel-Prüfungen je Zeile; er fällt nur bei der allerersten Erkennung einer Bibliothek an.

## Folgerung

Alle drei Ziele erreicht; die allererste Erkennung einer 500 000er-Bibliothek liegt mit ≈ 4,6–5,0 s an der Grenze und
läuft einmalig im Hintergrund, die Titelbilder folgen danach (Kosten hängen ab ADR-0057 von der echten Highlight-Auswahl
ab). Inkrementelle Durchgänge nach neuen Fotos, Datums-, Orts- und Gesichtsänderungen bleiben im Bereich von 0,1 s.
