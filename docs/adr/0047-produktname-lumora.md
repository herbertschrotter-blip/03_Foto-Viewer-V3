# ADR-0047: Produktname Lumora mit Schriftzug und App-Symbol

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Name „Lumora“ und Design „Gewählt · A + B + C“ in Mockup 07),
  Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** ADR-0042 (Navigation mit Bibliotheks-Umschalter, Info-Seite), ADR-0026 (Speicherort des Katalogs,
  `database-location.json`)

## Kontext

„PhotoViewer V3“ ist ein Arbeitstitel: er beschreibt die Technik-Generation, nicht das Produkt, und kollidiert mit
„Windows-Fotoanzeige“. Herbert hat den Namen **Lumora** gewählt (*Lumen* = Licht + *Memoria* = Erinnerung) und in
Mockup 07 (`mockups/07-lumora-schriftzug.html`, Abschnitt „Gewählt · A + B + C“) Schriftzug und App-Symbol abgenommen.
Gleichzeitig hängen an „PhotoViewerV3“ Dinge, die nicht brechen dürfen: Herberts Katalog unter
`%LocalAppData%\PhotoViewerV3` (bzw. der Ordner aus `database-location.json`), die Umgebungsvariablen
`PHOTOVIEWERV3_*` der Rauchtests, die Projekt-/Namespace-Struktur und die Skripte paralleler Agenten.

## Entscheidung

1. **Sichtbar heißt die App „Lumora“**: Fenstertitel und Titelleiste des Hauptfensters, Taskleiste/Alt+Tab, Lightbox
   („IMG_0001.jpg – Lumora“ im Fenstertitel; die schlanke Titelleiste der Lightbox zeigt weiter nur den Dateinamen),
   Info-Seite, Versionstexte („Lumora 0.2.49“), Fehlermeldung beim Start, Status- und Dialogtexte (Umzug,
   Modell-Download, Lizenzen, „kein Lumora-Katalog“). Dialoge behalten ihre eigenen Titel („Tags zuweisen“).
   Konstante: `Shell/Branding/LumoraBrand.ProductName`; Info › Entwicklung › Vorgänger:
   „PhotoViewer V3 (bis 0.2.48) · PhotoViewer V2 (PowerShell-Web-App)“.
2. **Programmdatei `Lumora.exe`**: `AssemblyName`, `Product`, `AssemblyTitle` (= Dateibeschreibung, Name in Taskleiste
   und Task-Manager) = „Lumora“. `build/publish.ps1` baut `artifacts\Lumora-<Version>-win-x64` und prüft den
   Produktnamen; `build/run-smoke.ps1` leitet den Prozessnamen aus der übergebenen EXE ab (ältere Pakete laufen weiter).
   Ein Build-Ziel löscht veraltete `PhotoViewerV3.exe/.dll/.pdb/.deps.json/.runtimeconfig.json` aus dem
   Ausgabeordner, damit keine Verknüpfung die alte Version startet (gesperrte Dateien bleiben liegen).
3. **Bewusst NICHT umbenannt**: Projekt-, Namespace-, Solution- und Ordnernamen (`PhotoViewerV3.*`,
   `PhotoViewerV3.slnx`), der Datenordner `%LocalAppData%\PhotoViewerV3`, `database-location.json`, die
   Umgebungsvariablen `PHOTOVIEWERV3_*`, der Protokollname `logs\photoviewer-*.log`, die Rauchtest-Arbeitsordner
   (`%TEMP%\PhotoViewerV3-smoke-*`).
4. **Schriftzug** als wiederverwendbares Steuerelement `Shell/Branding/LumoraWordmark`: ein TextBlock „lum“ + Linse
   (`LumoraLens`, `InlineUIContainer` auf der Grundlinie) + „ra“ in Segoe UI Variable Display (Ersatz Segoe UI). Der
   Verlauf Indigo → Violett → Koralle → Bernstein liegt als **absoluter** Pinsel über dem ganzen TextBlock, damit „lum“ und
   „ra“ einen gemeinsamen Verlauf haben. Die Linse misst sich am echten „o“ der Schrift (Breite = Vorschub, Höhe und
   Überhang = Glyphen-Umriss) und sitzt damit pixelgenau wie ein Buchstabe (Test: Ober-/Unterkante und Wortbreite wie mit
   echtem „o“). Strichstärke nach Größe: bis 24 px normal (kräftige Linse ohne Innenring = `lensSparkBold`), bis 40 px
   halbleicht (350), darüber leicht (300). Eingesetzt oben in der linken Leiste über dem Bibliotheks-Umschalter (22 px,
   daneben das Symbol 26 px) und im Info-Kopf (30 px, Symbol 56 px).
5. **Farben je Farbschema** über App-Ressourcen: `LumoraBrandDictionary` (in `App.xaml`) trägt
   `LumoraWordmarkBrush` und `LumoraLensRingBrush` für hell (#5B5FC7 → #8B5CF6 → #F97362 → #F5A524) bzw. dunkel
   (#818CF8 → #A78BFA → #FB8A7A → #FBBF24) ein und folgt `ApplicationThemeManager.Changed` (auch „System“ per
   `SystemThemeWatcher`). Der Schriftzug bindet per `SetResourceReference` (DynamicResource); ohne die Ressourcen (Tests)
   gilt die helle Palette. Funke und App-Symbol haben feste Farben.
6. **App-Symbol** als Vektor in `Shell/Branding/LumoraIcon.xaml` (`DrawingGroup` „tileSpark“: Indigo-Kachel, gefächerter
   Fotostapel, Funke rechts oben; unter 32 px „tileSmall“: ein Foto, großer Funke), per `x:Static LumoraIconImages.*`
   ohne App-Ressourcen nutzbar. **`Lumora.ico`** (16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px; 16–24 aus der
   vereinfachten Variante) wird reproduzierbar mit WPF-Bordmitteln erzeugt (`LumoraIconRenderer`: `RenderTargetBitmap`
   je Größe, ICO-Container selbst geschrieben — bis 128 px 32-Bit-DIB mit Alphakanal, 256 px PNG) über
   `build/make-icon.ps1` (Test `LumoraIconFileTests` mit `LUMORA_WRITE_ICON=1`) und eingecheckt. Ohne die Variable
   prüft derselbe Test, dass die eingecheckte Datei den gerenderten Rahmen entspricht. Verwendung: `ApplicationIcon`
   (EXE, Explorer), `EmbeddedResource` → `LumoraAppIcon.WindowIcon` für `Window.Icon` aller Fenster (`AppFluentWindow`,
   Taskleiste/Alt+Tab), Titelleisten-Symbol des Hauptfensters (vereinfachte Variante, 16 px).
7. **Eigene XAML-Ressourcen nie über den Assembly-Namen** (`/PhotoViewerV3;component/…`) einbinden, sondern relativ
   oder per `x:Static` — die sechs Galerie-Filter-Verweise auf `GalleryFilterStyles.xaml` sind relativ;
   `LumoraBrandingTests` wacht darüber.

## Begründung

- Der sichtbare Name ist eine Produktentscheidung; Ordner-, Variablen- und Projektnamen sind technische Schlüssel. Sie
  umzubenennen hätte einen Daten-Umzug (Katalog, Vorschaubilder, `database-location.json`), Änderungen an allen
  Rauchtest-Skripten und an jedem offenen Agenten-Zweig erzwungen — ohne Nutzen für Herbert.
- Der Protokollname bleibt `photoviewer-*.log`, weil die Protokolle im unveränderten Ordner `PhotoViewerV3\logs`
  liegen, `build/run-smoke.ps1` und die Rauchtest-Skripte paralleler Agenten nach `photoviewer-*.log` suchen und ein
  Wechsel die Protokolle des Update-Tages auf zwei Dateinamen verteilen würde.
- Absoluter Verlauf statt zweier relativer: nur so läuft die Farbe über „lum“ → Linse → „ra“ wie im Mockup durch.
- Linse aus den Glyphen-Maßen statt fester Faktoren: unabhängig von Schrift (Variable Display bzw. Ersatz Segoe UI) und
  Größe; ein Faktor (Mockup: 0,52 em) säße je nach Schrift zu hoch oder zu tief.
- ICO selbst schreiben statt Bildbibliothek: das Format ist klein (Verzeichnis + DIB/PNG), WPF rendert und kodiert
  schon, keine neuen NuGet-Pakete. DIB bis 128 px ist mit Explorer, Taskleiste und Ressourcen-Compiler am
  verträglichsten.

## Konsequenzen

### Positiv
- Eigenständiger, gut erkennbarer Name mit Symbol in Taskleiste, Titelleiste, Navigation und Info, hell wie dunkel.
- Herberts Katalog, Einstellungen und Skripte laufen ohne Umzug weiter.

### Negativ
- Verknüpfungen auf `…\PhotoViewerV3.exe` müssen einmalig auf `Lumora.exe` umgestellt werden.
- Code und Ordner heißen weiter `PhotoViewerV3` — für Leser des Repos ein zweiter Name (README erklärt es).

### Neutral
- Neue XAML-Dateien, die eigene Ressourcen einbinden, müssen relative Pfade nutzen (Test schlägt sonst an).
- Ändert sich das Symbol, `build/make-icon.ps1` ausführen und `Lumora.ico` mit einchecken.

## Alternativen

- **Alles umbenennen (Projekte, Namespaces, Datenordner)** → verworfen: Datenmigration, Konflikte mit parallelen
  Zweigen, kein sichtbarer Nutzen.
- **Nur Fenstertitel ändern, EXE bleibt `PhotoViewerV3.exe`** → verworfen: Taskleiste, Task-Manager und Explorer zeigten
  weiter den alten Namen.
- **Protokolle `lumora-*.log`** → verworfen (siehe Begründung); kann später ohne Datenverlust nachgeholt werden.
- **Schriftzug als ein Pfad/Bild** → verworfen: nicht scharf in allen Größen, keine Theme-Farben per DynamicResource,
  Strichstärke nicht anpassbar.
- **ICO mit externer Bibliothek bzw. Werkzeug (ImageMagick)** → verworfen: neues Paket bzw. nicht reproduzierbar im
  Repo.

## Referenzen

- Mockup: `mockups/07-lumora-schriftzug.html` (Abschnitt „Gewählt · A + B + C“)
- Code: `src/PhotoViewerV3.Wpf/Shell/Branding/` (`LumoraBrand`, `LumoraPalette`, `LumoraBrandDictionary`,
  `LumoraWordmark`/`LumoraLens`, `LumoraIcon.xaml`, `LumoraIconRenderer`, `LumoraAppIcon`, `Lumora.ico`),
  `build/make-icon.ps1`
- Tests: `tests/PhotoViewerV3.Wpf.Tests/Shell/LumoraBrandingTests.cs`, `LumoraIconFileTests.cs`; Rauchtest-Schritt
  „Einstellungen-Reiter und Info“
