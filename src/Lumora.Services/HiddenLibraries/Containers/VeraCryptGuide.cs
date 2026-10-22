namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>Welche Art VeraCrypt-Container der Assistent anleitet (ADR-0061).</summary>
public enum VeraCryptVolumeType
{
    /// <summary>Verstecktes Volume im äußeren Volume (zwei Kennwörter) — Standard (Herbert).</summary>
    Hidden = 0,

    /// <summary>Normaler Container mit einem Kennwort.</summary>
    Normal = 1,
}

/// <summary>Ein Schritt der Anleitung neben dem VeraCrypt-Assistenten.</summary>
public sealed record VeraCryptGuideStep(int Number, string Title, string Detail);

/// <summary>
/// Schritt-Anleitung für den VeraCrypt-Assistenten (<c>VeraCrypt Format.exe</c>). Die VeraCrypt-Befehlszeile kann unter
/// Windows keine versteckten Volumes anlegen und bekäme das Kennwort sichtbar in der Prozessliste — deshalb legt der
/// Benutzer den Container im Assistenten selbst an; Lumora zeigt nur, was er wo wählt. Bezeichnungen der deutschen
/// VeraCrypt-Oberfläche, in Klammern die englischen.
/// </summary>
public static class VeraCryptGuide
{
    public const VeraCryptVolumeType DefaultType = VeraCryptVolumeType.Hidden;

    public const string FormatExecutableName = "VeraCrypt Format.exe";

    public static IReadOnlyList<VeraCryptGuideStep> Steps(VeraCryptVolumeType type) => type == VeraCryptVolumeType.Hidden
        ? Number(
            ("Containerdatei erstellen", "„Eine verschlüsselte Containerdatei erstellen“ (Create an encrypted file container) → Weiter."),
            ("Verstecktes Volume", "„Verstecktes VeraCrypt-Volume“ (Hidden VeraCrypt volume) → Weiter."),
            ("Modus", "„Normaler Modus“ (Normal mode) → Weiter."),
            ("Speicherort", "„Datei…“: neutraler Name, z. B. D:\\Daten\\sicherung.dat — nicht in OneDrive oder einem anderen Cloud-Ordner. „Verlauf nicht speichern“ (Never save history) angehakt lassen."),
            ("Äußeres Volume", "Verschlüsselung und Hash wie vorgeschlagen lassen → Größe des ganzen Containers → Kennwort für den ÄUSSEREN Bereich → Dateisystem exFAT oder NTFS (VeraCrypt sagt, welches mehr Platz für den versteckten Bereich lässt) → Maus bewegen → „Formatieren“."),
            ("Tarnung füllen", "„Äußeres Volume öffnen“ (Open Outer Volume): ein paar harmlose Fotos hineinkopieren, damit der Container glaubwürdig aussieht. Danach Fenster schließen → Weiter."),
            ("Inneres Volume", "Verschlüsselung wie vorgeschlagen → Größe des versteckten Bereichs (VeraCrypt zeigt das Maximum) → ein ANDERES Kennwort → Dateisystem NTFS → „Formatieren“."),
            ("Fertig", "VeraCrypt-Assistent schließen und hier „Weiter: Container einhängen“ wählen. Beim Einhängen das Kennwort des INNEREN Bereichs eingeben."))
        : Number(
            ("Containerdatei erstellen", "„Eine verschlüsselte Containerdatei erstellen“ (Create an encrypted file container) → Weiter."),
            ("Standard-Volume", "„Standard-VeraCrypt-Volume“ (Standard VeraCrypt volume) → Weiter."),
            ("Speicherort", "„Datei…“: neutraler Name, z. B. D:\\Daten\\sicherung.dat — nicht in OneDrive oder einem anderen Cloud-Ordner. „Verlauf nicht speichern“ (Never save history) angehakt lassen."),
            ("Verschlüsselung und Größe", "Verschlüsselung und Hash wie vorgeschlagen lassen → Größe des Containers wählen."),
            ("Kennwort", "Ein gutes Kennwort festlegen (VeraCrypt empfiehlt mindestens 20 Zeichen). Lumora fragt und speichert es nie."),
            ("Formatieren", "Dateisystem NTFS → Maus bewegen → „Formatieren“."),
            ("Fertig", "VeraCrypt-Assistent schließen und hier „Weiter: Container einhängen“ wählen."));

    /// <summary>Warnungen, die neben der Anleitung stehen (bei „versteckt“ zuerst die Gefahr für den inneren Bereich).</summary>
    public static IReadOnlyList<string> Warnings(VeraCryptVolumeType type)
    {
        var common = new List<string>
        {
            "Die Kennwörter gibt es nur in VeraCrypt — Lumora fragt und speichert sie nie. Ein vergessenes Container-Kennwort kann niemand zurückholen.",
            "Den Container nie in OneDrive oder einem anderen Cloud-Ordner ablegen.",
        };
        if (type == VeraCryptVolumeType.Normal)
        {
            return common;
        }

        return
        [
            "Schreiben in den ÄUSSEREN Bereich kann den inneren (versteckten) Bereich zerstören.",
            "Den äußeren Bereich immer mit „Optionen“ → „Verstecktes Volume vor Beschädigung schützen“ (Protect hidden volume against damage) einhängen und dabei beide Kennwörter angeben.",
            "Die Lumora-Bibliothek gehört in den INNEREN Bereich: beim Einhängen das Kennwort des versteckten Volumes eingeben.",
            .. common,
        ];
    }

    /// <summary>Warum der Assistent nötig ist (Hinweis in der Oberfläche).</summary>
    public const string WhyWizard =
        "VeraCrypt kann versteckte Volumes unter Windows nicht über die Befehlszeile anlegen, und ein Kennwort in der "
        + "Befehlszeile wäre in der Prozessliste sichtbar. Deshalb führt der VeraCrypt-Assistent durch die Schritte.";

    private static IReadOnlyList<VeraCryptGuideStep> Number(params (string Title, string Detail)[] steps)
        => steps.Select((s, i) => new VeraCryptGuideStep(i + 1, s.Title, s.Detail)).ToList();
}
