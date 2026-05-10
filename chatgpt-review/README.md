# ChatGPT-Review — Audit-Trail

Dieser Ordner enthält das Original-Material aus 3 Review-Runden mit GPT-5 zur
Architektur-Validierung des C#-Ports.

## Wichtiger Hinweis: Projekt-Umbenennung

Das Projekt hieß während dieser Reviews noch **"Foto-Viewer V2"**. Nach Abschluss der
Reviews wurde es in **"PhotoViewer V3"** umbenannt. Die Inhalte in diesem Ordner
**bleiben absichtlich unverändert** — als historischer Audit-Trail.

Alle anderen Dokumente (`PLAN.md`, `docs/adr/`, `mockups/`) wurden auf das neue
Naming aktualisiert.

## Aufbau pro Runde

Jede Runde hat 4 Artefakte:

```
runde-N-prompt.md             — Was Claude an GPT-5 geschickt hat
runde-N-chatgpt-response.md   — GPT-5s Antwort
runde-N-claude-analysis.md    — Claudes Einschätzung (Stufe A)
runde-N-user-decisions.md     — Herberts Entscheidungen + Begründung
```

## Inhalt der Runden

- **Runde 1:** Stack-Auswahl, DB-Schema, UI/UX, Migrations-Strategie, erweiterte
  Features (Plan-Ordner, Token-Sorter, Auto-Extract, animierte Thumbs, Gesichtserkennung)
- **Runde 2:** WIC-Tiefe, Multi-Channel-Queue, OperationBatches-Recovery,
  Animated-Hover-Lifecycle, MVP-Schnitt für 50k–500k, WorkBudgetService
- **Runde 3:** Aufwand-Reality-Check (8–10 Wochen), Storage-Profile-Detection,
  Migration-Disziplin, finale 15 ADRs, Tag-1–3-Plan, Offline-vs-Missing-Guard

## Ergebnis

Konsolidiert in [PLAN.md](../PLAN.md) und [docs/adr/](../docs/adr/).
