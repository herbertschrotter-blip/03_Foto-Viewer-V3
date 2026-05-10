# Runde 3 — User-Entscheidungen (Final)

> Datum: 2026-05-10

## Stufe-A-Antworten

| Frage | Antwort | Konsequenz |
|---|---|---|
| V0.1-Scope | **8–10 Wochen, robust release-fähig** | Vollständiger Scope, LibVLC produktiv, Adaptive Throttling, Recovery-UI |
| LibVLC-Fallback bei rotem Spike | **Ja, "Extern öffnen" akzeptabel** | Risiko-Mitigation: nach 5 PT Spike-Cap |
| Testdaten | **Reale Daten verfügbar** | Spikes auf echter Hardware (SSD/HDD/NAS) |
| Distribution | **Portable Ordner** (`dotnet publish`) | kein Installer-Aufwand |

## Default für offene NAS-Frage (von Claude gesetzt)

NAS Background-Thumbnails:
- **Bilder**: drosseln, aber erlaubt (NAS-Profil)
- **Videos**: nur on-demand, nicht im Background
- `AggressiveBackgroundThumbs = false` (wie GPT-5 vorschlug)

## Status — Review abgeschlossen

- ✅ 3 Runden mit GPT-5
- ✅ Alle Architektur-Entscheidungen geklärt
- ✅ 15 finale ADRs
- ✅ V0.1-Scope geschnitten (Pflicht / Cutline / Streichen)
- ✅ Tag 1–3 Implementation-Plan
- ✅ Multi-Project Solution-Layout
- ✅ NuGet-Pakete mit Versionen
- ✅ Spike-Reihenfolge definiert (3 blockierend zuerst)
- ✅ Offline-vs-Missing-Guard mit Threshold
- ✅ 3 Storage-Profile (SSD/HDD/NAS)
- ✅ Migration-Disziplin definiert
- ✅ Cache-Strategie geklärt
