# ADR-0052: Motive — Fotos lokal nach Inhalt erkennen (SigLIP 2), Begriffe frei, Treffer filter- und suchbar

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md) (Modell per Benutzer-Download, ONNX Runtime DirectML,
  Hintergrund-Index), [ADR-0023](0023-neu-aufbauen-uebernimmt-benutzerdaten.md) („Neu aufbauen“ übernimmt Benutzerdaten),
  [ADR-0025](0025-scan-sperre-fuer-datei-operationen.md) (Scan-Sperre), [ADR-0041](0041-galerie-filter-unabhaengig-kombinierbar.md)
  (Filterkarte, „Es gilt“), [ADR-0043](0043-vorschaubilder-kurze-kante-und-bildmasse.md) (Vorschaubilder), [Spike #14](../spikes/14-motive.md)

## Kontext

Herbert will Fotos nach ihrem Inhalt finden — „Sonnenuntergang“, „Meer“, „Hund“, „Torte“, „Quittung“ — ohne jedes Foto
selbst zu verschlagworten und ohne dass Fotos den Rechner verlassen. Kataloge haben 50 000–500 000 Dateien; die Liste
der Begriffe soll er selbst pflegen können, Änderungen sollen sofort wirken, ohne alle Fotos neu zu rechnen. Die
Grafikkarte (GTX 1080 Ti) nutzt schon die Gesichtserkennung; beide dürfen sich nicht gegenseitig ausbremsen. Begriffe,
Treffer und Zähler gehören zum jeweiligen Bibliotheks-Bereich (harte Regel „Bibliotheks-Trennung“), versteckte
Bibliotheken dürfen nichts außerhalb ihres Katalogs ablegen.

## Entscheidung

1. **Modell: SigLIP 2 base (google/siglip2-base-patch16-224, Apache-2.0)** als ONNX (onnx-community): Bild- und
   Sprachmodell bilden Bilder und Texte in denselben 768-dimensionalen Raum ab; SigLIP 2 ist mehrsprachig (deutsche
   Begriffe funktionieren direkt). Bildmodell **fp32 auf der Grafikkarte** (DirectML, Rückfall CPU nach Probelauf; fp16
   ist auf Pascal-Karten langsam), Sprachmodell **int8 auf dem Prozessor** (nur beim Anlegen/Ändern von Begriffen und für
   die Freitext-Suche; nach 2 min Ruhe freigegeben). Download nur nach Bestätigung (Dateien, Quelle, Größen, Lizenz,
   ≈ 690 MB) in `<AppData>\models\siglip2-base-patch16-224\`, **SHA-256 je Datei fest im Code** (LFS-Prüfsummen der
   Hugging-Face-API, kleine Dateien gegen die Git-Prüfsumme abgeglichen), nie im Repo oder der portablen Version.
   `LUMORA_MOTIF_MODEL` zeigt für Entwicklung/Tests auf einen anderen Ordner.
2. **Vorverarbeitung exakt nach `preprocessor_config.json`**: 224 × 224 ohne Seitenverhältnis/Zuschnitt, Pillow-BILINEAR
   nachgebildet (Dreiecksfilter mit Träger × Skalierung, 22-Bit-Festkomma, erst waagrecht in ein 8-Bit-Zwischenbild,
   dann senkrecht — `SiglipPreprocessor`), dann (x/255 − 0,5)/0,5. Eingang ist das fertige Vorschaubild (kurze Kante bis
   400 px, Orientierung angewandt; ≈ 1 ms statt ≈ 45 ms) oder — solange es fehlt — das per WIC auf längste Kante 448
   dekodierte Original. Videos nur über ihr Vorschaubild.
3. **Tokenizer selbst** (`GemmaTokenizer`, kein neues NuGet): BPE aus `tokenizer.json` (Normalisierer „ “ → „▁“, Merges
   nach Rang, Byte-Fallback, `<eos>`, Auffüllen auf 64 mit `<pad>`), Kleinschreibung wie beim Training.
   `Microsoft.ML.Tokenizers` liest Gemma nur aus dem SentencePiece-Modell, das nicht zum Download gehört.
4. **Wahrscheinlichkeit wie SigLIP**: p = σ(cos · e^logit_scale + logit_bias) mit den Werten des Modells
   (logit_scale = 4,724453, logit_bias = −16,77172, aus `model.safetensors` gelesen). Jeder Begriff wird für sich bewertet
   (kein Softmax) — ein Foto kann viele oder keine Motive haben. **Standard-Schwelle 1 %**, je Begriff einstellbar
   (0,3 %–10 %). Die Werte sind für kurze Begriffe klein (Stichprobe: höchste je Foto im Median 0,7 %), deshalb ist 1 %
   schon deutlich (Spike 14).
5. **Begriffe: Wort + Beschreibung.** Der Text-Vektor eines Begriffs ist der Mittelwert aus dem deutschen Wort und seiner
   Beschreibung (`Prompt`, für die 82 Standard-Begriffe eine englische Foto-Vorlage „a photo of a dog“). Gemessen an
   Referenzbildern trennte „Wort + englische Vorlage“ am besten (mittlerer Abstand zum besten falschen Kandidaten 0,041;
   Wort allein 0,034; deutsche Vorlage „ein Foto von einem Hund“ nur 0,017). Eigene Begriffe ohne Beschreibung nutzen
   nur ihr Wort. Die Start-Liste wird einmal je Bibliothek angelegt (gelöschte kommen nicht wieder).
6. **Schema** (Migration `AddMotifs`): `MotifTerms` (je `LibraryId`, NOCASE-eindeutig, Text-Vektor + Modell-Version),
   `FileEmbeddings` (Bild-Vektor je Datei als **float16**, 1 536 Bytes — halbiert den Katalog, Kosinus-Fehler < 0,001;
   Vektor als letzte Spalte, abdeckender Index zum Zählen), `FileMotifs` (nur Treffer, `IX_FileMotifs_Term_File`).
   Begriffe ändern rechnet **nur die Treffer** aus den gespeicherten Vektoren neu (abschnittsweise 2 000 Dateien je
   Transaktion), nie die Bilder.
7. **Hintergrund-Index** (`IMotifIndexService`, Muster ADR-0035): aktive Bibliothek, neueste zuerst, 3 Bilder vorab,
   niedrige Priorität, stapelweise schreiben (Vektor + Treffer in einer Transaktion, Single-Writer), Pause/Fortsetzen/
   Abbrechen in der Statusleiste, startet 15 s nach dem App-Start (bestehende Kataloge werden so nachgeholt), nach jedem
   Scan, bei neuen Dateien, nach dem Download. **Ruht, solange die Scan-Sperre die Bibliothek belegt** (`IScanGate.IsBusy`,
   wie das Nachtragen der Aufnahmedaten). Standard **an** — er läuft ohnehin erst, wenn der Benutzer das Modell bewusst
   geladen hat.
8. **Gemeinsame GPU-Sperre** `IGpuGate` (Services/Infrastructure): Gesichts- und Motiv-Index halten sie für die
   Inferenz genau eines Fotos, sofern sie DirectML nutzen — beide wechseln sich Foto um Foto ab, rechnen nie gleichzeitig
   auf der Karte. CPU-Analysen brauchen sie nicht.
9. **Oberfläche**: neuer Einstellungs-Reiter **„Motive“** (statt Abschnitt bei „Gesichter“ — eigenes Thema mit langer
   Begriffsliste): Schalter, Grafikkarte, Modell/Download, Index + „Neu analysieren“, globale Schwelle, Begriffe mit
   Treffern, Beschreibung, eigener Schwelle, Umbenennen, Löschen. Galerie: Chip **„Motive“** in der Zeile „Inhalt“ (Flyout
   mit Begriffen und Anzahl, „eines der gewählten“/„alle zusammen“, Bildinhalt frei beschreiben), Bedingung in „Es gilt“,
   Suche findet Begriffe (UNION-Zweig, nur Begriffe des Bereichs), **„Motiv: Hund“** sucht nur Motive; Listenspalte
   „Motive“; Lightbox-Info „MOTIVE“. **Bildinhalt-Suche** (Freitext „Hund am Strand“): Text-Vektor gegen alle Bild-Vektoren
   des Bereichs, die 200 ähnlichsten mit Kosinus ≥ 0,05 werden zur Bedingung.
10. **Trennung und Datenschutz**: alle Abfragen über `Files.LibraryId` bzw. `MotifTerms.LibraryId`; Vektoren liegen im
    Katalog (bei versteckten Bibliotheken im Container-Katalog) — außerhalb wird nichts abgelegt (Modellordner enthält
    nur das Modell). Logs mit Info-Level nennen nur Zahlen und Ids.

## Konsequenzen

- „Neu aufbauen“ parkt `FileEmbeddings`/`FileMotifs` wie die Gesichter (`UserDataCarryOver.Motifs.cs`); an Ort und
  Stelle geänderte Dateien verlieren Vektor und Treffer im Incremental-Scan und werden neu gerechnet.
- Speicher: 500 000 Fotos ≈ 0,8 GB Vektoren im Katalog (float16).
- Laufzeit (Spike 14, GTX 1080 Ti): ≈ 26 ms/Foto Bildmodell, mit Laden der Originale ≈ 40 ms/Foto; 50 000 Fotos ≈
  23–33 min, 500 000 ≈ 3,8–5,5 h (DirectML, aus Vorschaubildern bzw. Originalen); CPU ≈ 125–200 ms/Foto (500 000 ≈
  17–28 h).
- Die Standard-Schwelle ist an 200 unmarkierten Fotos und Referenzbildern gewählt; echte Kalibrierung je Begriff braucht
  Herberts Rückmeldung (Schwelle je Begriff in den Einstellungen).
- Die Bibliotheks-Trennung stellt `MotifTerms.LibraryId` beim Zusammenbau auf ihre Gruppen-Logik um.

## Verworfen

- **CLIP (OpenAI) / Chinese-CLIP**: englisch bzw. schwächer mehrsprachig, Softmax-Vergleich erzwingt genau ein Motiv.
- **Bildklassifikator mit fester Klassenliste** (ImageNet/Places): keine eigenen Begriffe, deutsche Namen nur über Tabellen.
- **Treffer bei jeder Abfrage live rechnen** statt `FileMotifs`: 500 000 × 82 Skalarprodukte je Filter-Klick — zu langsam für
  Zähler und Kombination mit den übrigen Filtern in SQL.
- **fp16-Bildmodell**: auf der GTX 1080 Ti (keine schnellen fp16-Einheiten) langsamer als fp32.
