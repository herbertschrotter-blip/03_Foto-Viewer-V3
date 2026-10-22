# ADR-0061: Container-Assistent für versteckte Bibliotheken — BitLocker-VHDX oder VeraCrypt, Kennwörter nur in Windows/VeraCrypt

- **Status:** Accepted
- **Datum:** 2026-10-22
- **Entscheider:** Herbert Schrotter (Wahl beim Anlegen, BitLocker und VeraCrypt, verstecktes Volume als Standard,
  Installation über winget nach Bestätigung), Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliotheken, Spurenfreiheit,
  `IContainerTools`), [ADR-0042](0042-navigation-und-einstellungen-mit-reitern.md) (Bibliotheks-Umschalter)

## Kontext

Bis 0.4.14 setzte „Versteckte Bibliothek anlegen…“ voraus, dass der verschlüsselte Container schon eingehängt ist. Einen
Container anzulegen (VeraCrypt installieren, Assistent bedienen, bzw. VHDX anlegen, initialisieren, formatieren und mit
BitLocker verschlüsseln) war Handarbeit außerhalb von Lumora und für Herbert die größte Hürde. Lumora soll dabei führen,
ohne selbst zu verschlüsseln und ohne je ein Container-Kennwort zu sehen. Während der Entwicklung darf auf Herberts PC
nichts davon echt laufen (kein winget, keine VHDX, kein BitLocker, keine UAC-Rückfrage) — alles hängt an Schnittstellen
mit Fakes.

## Entscheidung

1. **Erst fragen, dann anlegen.** Umschalter → „Versteckte Bibliothek anlegen…“ öffnet den Container-Assistenten
   (`ContainerAssistantWindow`, `ContainerAssistantViewModel`): **Container ist schon eingehängt** (bisheriger Weg),
   **Neu mit BitLocker** oder **Neu mit VeraCrypt**. Am Ende beider neuen Wege folgt der bisherige Anlegen-Dialog (Name,
   Lumora-Kennwort, Ordner) mit dem neuen Laufwerk vorgewählt (`HiddenLibrariesViewModel.PreferDrive`).
2. **BitLocker (Windows, ohne Zusatzprogramm):** Speicherort (Vorschlag `<NTFS-Laufwerk mit meistem Platz, nicht System>\
   Daten\sicherung.vhdx`; nur NTFS/ReFS, Cloud-Ordner gesperrt), Größe (Hälfte des freien Platzes, ab 20 GB auf 10 GB
   abgerundet, höchstens 1024 GB; dynamisch erweiterbar), freier Laufwerksbuchstabe (der letzte freie), Bezeichnung
   „Daten“. Das **diskpart-Skript** (`VhdxScript.Build`: `create vdisk … type=expandable`, `attach vdisk`, `convert gpt`,
   `create partition primary`, `format fs=ntfs label=… quick`, `assign letter=…`, ASCII) steckt in einem PowerShell-Befehl
   (`VhdxScript.BuildElevatedPowerShell`, übergeben als `-EncodedCommand`), den Lumora **erhöht** startet (`Verb=runas`,
   eine Windows-Rückfrage, `IElevatedRunner`). Erst dieser erhöhte Lauf schreibt das Skript mit Zufallsnamen nach
   `%SystemRoot%\Temp`, führt `diskpart /s` aus, hängt bei einem Fehler eine schon angelegte VHDX ab und löscht sie
   (`VhdxScript.BuildCleanup`) und löscht das Skript in jedem Fall (`finally`). Danach öffnet Lumora den **Windows-eigenen BitLocker-Assistenten**
   `%windir%\System32\BitLockerWizardElev.exe X:\ T` (derselbe Aufruf wie der Explorer, Registry
   `HKCR\Drive\shell\encrypt-bde-elev\command`) mit Hinweisen: Kennwort, Wiederherstellungsschlüssel in Datei/Druck statt
   Microsoft-Konto, „Automatisch entsperren“ nicht einschalten. „Verschlüsselung prüfen“ liest den Zustand **ohne
   Administratorrechte** über die Shell-Eigenschaft `System.Volume.BitLockerProtection` (1 = an, 3 = wird verschlüsselt
   → weiter; 6 = gesperrt → Hinweis; sonst „noch nicht verschlüsselt“).
3. **VeraCrypt:** Fehlt VeraCrypt (`IVeraCryptLocator`), zeigt der Assistent eine Bestätigungsseite (Paket
   `IDRIX.VeraCrypt`, Quelle winget/Hersteller IDRIX, ca. 40 MB, Administratorrechte, Lizenz *VeraCrypt License: Apache 2.0
   und TrueCrypt License 3.0*); erst der Klick **„Zustimmen und installieren“** startet
   `winget install --id IDRIX.VeraCrypt -e --accept-package-agreements --accept-source-agreements` (Ausgabe mitgelesen,
   letzte Zeilen sichtbar, bekannte winget-Rückgabewerte verständlich übersetzt; Erfolg zählt nur, wenn VeraCrypt danach
   gefunden wird). Ohne winget: Hinweis mit Link zur offiziellen Download-Seite und „Erneut prüfen“. Dann startet Lumora
   **`VeraCrypt Format.exe`** (neben `VeraCrypt.exe`) und zeigt daneben eine **Schritt-Anleitung** (`VeraCryptGuide`) —
   Standard **verstecktes Volume** (Containerdatei → Verstecktes Volume → Normaler Modus → Speicherort → äußeres Volume →
   harmlose Fotos → inneres Volume mit anderem Kennwort), wählbar „normaler Container“; Warnungen: Schreiben in den
   äußeren Bereich zerstört ggf. den inneren, äußeren Bereich nur mit „Verstecktes Volume schützen“ einhängen, Bibliothek
   in den INNEREN Bereich. Danach **einhängen** über das vorhandene `IContainerTools.MountVeraCryptAsync` (VeraCrypt fragt
   das Kennwort selbst); eine Containerdatei in einem Cloud-Ordner hängt Lumora nicht ein. Ist VeraCrypt inzwischen von
   Hand installiert, geht „Erneut prüfen“ ohne winget weiter.
4. **Abbrechen** geht auf jeder Seite (nicht, solange ein Schritt läuft) und tut selbst nichts. Wurde die VHDX schon
   angelegt, aber noch nicht verschlüsselt, bzw. ist der neue Container schon eingehängt, sagt Lumora nach dem Abbrechen,
   was zu tun ist. UAC abgelehnt → nichts angelegt (ein von Lumora angelegter leerer Ordner wird wieder entfernt).
   diskpart scheitert halb → der erhöhte Lauf hängt die neue VHDX ab und löscht sie; bleibt sie trotzdem, Meldung mit
   „Auswerfen und Datei löschen“. Zeitüberschreitung → ehrliche Meldung, nichts weggeräumt.
5. **Spuren (ADR-0049):** Der Assistent und `ContainerCreationService` **protokollieren nichts** — schon „Container
   angelegt“ wäre im normalen Protokoll eine Spur. Keine Pfade in Einstellungen; Dateidialoge mit `AddToRecent = false`;
   das Skript existiert nur im erhöhten Lauf unter `%SystemRoot%\Temp` (nicht unter `%LocalAppData%\Lumora`) und wird
   sofort gelöscht; Kennwörter gibt es weder im Skript noch in einer
   Befehlszeile noch in Lumora.
6. **Schnittstellen** (Services, `Lumora.Services.HiddenLibraries.Containers`): `IContainerCreation`
   (`ContainerCreationService`), `IElevatedRunner`, `IProcessOutputRunner`, `IShellLauncher`, `IBitLockerPlatform`,
   `IVeraCryptInstaller`, `IDriveSpaceSource`; Oberfläche: `IContainerAssistant`, `IContainerFilePicker`. Tests ersetzen
   alle Windows-Seiten; im Rauchtest wird der Assistent nicht geöffnet (der Schritt „Versteckte Bibliothek“ legt wie bisher
   direkt an).

## Begründung

- **diskpart statt PowerShell `New-VHD`:** `New-VHD` gehört zum Hyper-V-Modul und fehlt auf vielen PCs; diskpart ist
  überall da und kann in einem Lauf anlegen, anhängen, partitionieren, formatieren und den Buchstaben setzen. Eine
  einzige erhöhte Rückfrage reicht.
- **Skript erst im erhöhten Lauf, unter `%SystemRoot%\Temp`** (Abweichung vom ursprünglichen Plan „Skript im `TempRoot`
  der Sitzung“, Selbst-Review): Eine Datei im Benutzerordner könnte jeder Prozess des Benutzers zwischen Schreiben und
  UAC-Zustimmung austauschen (z. B. `clean` auf Datenträger 0) — der Benutzer würde dann fremde Befehle mit
  Administratorrechten bestätigen. Dateien, die ein Administrator-Prozess in `%SystemRoot%\Temp` anlegt, können normale
  Prozesse weder lesen noch ändern; außerdem bleibt so nicht einmal kurz eine Spur unter `%LocalAppData%\Lumora`.
- **Pfad und Bezeichnung nur ASCII:** diskpart liest Skriptdateien nicht verlässlich mit Umlauten; der neutrale Vorschlag
  ist ASCII, eine klare Meldung erklärt die Grenze.
- **Windows-BitLocker-Assistent statt `manage-bde -on`/`Enable-BitLocker`:** Das Kennwort bliebe sonst nicht in einem
  Windows-Fenster (Befehlszeile bzw. Lumora-Eingabe). Der Assistent führt außerdem sauber durch den Wiederherstellungsschlüssel.
- **Zustand über die Shell-Eigenschaft:** `manage-bde -status` und WMI `Win32_EncryptableVolume` verlangen
  Administratorrechte — das wäre eine zweite Rückfrage nur zum Nachsehen.
- **VeraCrypt nur über den Assistenten:** Die VeraCrypt-Befehlszeile kann unter Windows keine versteckten Volumes anlegen;
  ein Kennwort als Argument (`/password`) stünde in der Prozessliste.
- **winget statt eigenem Download:** offizielles Paket mit Prüfsumme aus dem Manifest, kein eigener Download-/Prüfcode in
  Lumora, Updates über winget; Zustimmung zu Lizenz und Quelle gibt der Benutzer ausdrücklich per Klick.

## Konsequenzen

### Positiv
- Herbert kann eine versteckte Bibliothek ohne Vorwissen vom leeren PC bis zum ersten Scan anlegen.
- Kein Kennwort berührt Lumora; die Windows-Seite ist vollständig hinter Schnittstellen und mit Fakes getestet.

### Negativ
- Die echten Abläufe (diskpart, BitLocker-Assistent, winget-Rückgabewerte, `VeraCrypt Format.exe`, Shell-Eigenschaft)
  sind nur mit Fakes geprüft; der erste echte Durchlauf geschieht gemeinsam mit Herbert (Checkliste im CHANGELOG 0.4.18).
- Die Anleitung beschreibt die VeraCrypt-Oberfläche in Worten; ändert VeraCrypt Bezeichnungen, muss sie nachgezogen werden.
- Kurz liegt das diskpart-Skript (VHDX-Pfad, Bezeichnung, Buchstabe — kein Kennwort) unter `%SystemRoot%\Temp`; ein
  Absturz des erhöhten Laufs genau dazwischen ließe es dort liegen (nur für Administratoren lesbar).
- Läuft diskpart länger als 5 Minuten, meldet Lumora das ehrlich („läuft eventuell noch“) und räumt nichts weg — einen
  erhöhten Prozess kann Lumora nicht beenden.

### Neutral
- `HiddenLibrariesViewModel` nimmt den Assistenten optional entgegen (`IContainerAssistant? assistant = null`); ohne ihn
  (Tests) geht es wie bisher direkt zum Anlegen-Dialog.
- Explorer-Doppelklick auf eine BitLocker-VHDX hängt sie später wieder ein (Windows fragt das BitLocker-Kennwort);
  Aushängen wie bisher über `Dismount-DiskImage` (ADR-0049).

## Alternativen

- **Eigene Verschlüsselung in Lumora** (verschlüsselter Katalog/Ordner) → verworfen (Herberts Vorgabe, ADR-0049): der
  Container schützt alles inklusive Originale; eigene Krypto wäre zusätzlicher, schwer zu prüfender Code.
- **VeraCrypt-Befehlszeile mit Kennwort** (`VeraCrypt Format.exe /create … /password …`) → verworfen: Kennwort in der
  Prozessliste und in Lumora, versteckte Volumes ohnehin nicht möglich.
- **VeraCrypt-Installer selbst von veracrypt.fr laden** (Download, Signatur prüfen, starten) → verworfen zugunsten von
  winget: mehr eigener Code für Download/Prüfung/Updates; bleibt als Weg ohne winget (Link zur offiziellen Seite).
- **`manage-bde`/`Enable-BitLocker` mit Kennwort aus Lumora** → verworfen: Lumora sähe das Kennwort.
- **PowerShell `New-VHD`** → verworfen: braucht das Hyper-V-Modul.

## Referenzen

- Code: `src/Lumora.Services/HiddenLibraries/Containers/*`, `src/Lumora.Wpf/Shell/HiddenLibraries/ContainerAssistant*`,
  `HiddenLibrariesViewModel.CreateAsync`, `HiddenLibrariesServiceCollectionExtensions.AddContainerAssistant`
- Tests: `tests/Lumora.Tests/HiddenLibraries/ContainerCreationTests.cs`,
  `tests/Lumora.Wpf.Tests/HiddenLibraries/ContainerAssistantTests.cs`
- Microsoft-Dokumentation diskpart (`create vdisk`, `attach vdisk`, `convert gpt`, `format`, `assign`); winget
  „install“ und Rückgabewerte; VeraCrypt-Dokumentation „Hidden Volume“ und „Command Line Usage“
