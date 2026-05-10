# Runde 3 — Claudes Stufe-A-Einschätzung

> Datum: 2026-05-10

## Kurzfassung

GPT-5 hat den Reality-Check ehrlich gemacht. **4 Wochen sind nicht drin, 6 nur mit Cuts, 8–10 realistisch.** Konkrete Personentage-Schätzung pro Block, klare Cut-Empfehlungen, finale 15 ADRs, Tag-1–3-Plan. Übernahme ohne Vorbehalt.

## Wichtigste Erkenntnisse

| Punkt | Implikation |
|---|---|
| 68–124 PT Gesamtaufwand | bei 5 PT/Woche = 13–25 Wochen brutto, mit Cuts 8–11 |
| Multi-Project ab Tag 1 | Single-Project (Claudes erster Vorschlag) ist falsch |
| 3 blockierende Spikes | SQLite-Bulk + Virtual-Grid + LibVLC sind Architektur-Entscheidungen |
| Offline-vs-Missing-Guard | 20%-Threshold, kritisch für Vertrauen |
| NAS-Cache lokal | kein Polluting, keine Sync-Konflikte |
| Storage-Detection konservativ | bei Unsicherheit als HDD/USB |

## Wo GPT-5 Claude (zu Recht) korrigiert

- Single-Project → Multi-Project ab Tag 1
- Storage-Detection war zu komplex gedacht → konservativer Fallback
- Cache-Strategie bei NAS → klar lokal in `%LocalAppData%`

## User-Entscheidungen aus Stufe A

| Frage | Antwort | Konsequenz |
|---|---|---|
| V0.1-Scope | **8–10 Wochen, robust release-fähig** | Vollständiger Scope inkl. LibVLC + Adaptive Throttling + Recovery-UI |
| LibVLC-Cut bei rotem Spike | **Ja, "Extern öffnen" als Fallback OK** | Risiko-Mitigation eingebaut |
| Testdaten | **Reale Daten verfügbar** | Spike-Qualität robust auf echter Hardware |
| Distribution | **Portable Ordner via `dotnet publish`** | kein Installer-Aufwand im MVP |

## Pragmatischer Default für offene NAS-Frage

GPT-5 fragt: NAS Background-Thumbs deaktivieren oder drosseln?
**Pragmatischer Default**: Bilder dürfen Background mit starker Drosselung (NAS-Profil), Videos nur on-demand. Das hatte GPT-5 im NAS-Profil schon vorgeschlagen (`AggressiveBackgroundThumbs: false`).

## Status

- ✅ Runde 1, 2, 3 abgeschlossen
- ✅ Alle Constraints geklärt
- ✅ 15 ADRs final
- ✅ Tag 1–3 Plan steht
- 🟡 Nächster Schritt: Plan finalisieren (PLAN.md) oder direkt Solution-Skeleton
