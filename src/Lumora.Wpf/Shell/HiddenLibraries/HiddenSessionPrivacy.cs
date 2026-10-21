using System;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>
/// Spurenschutz der versteckten Sitzung (ADR-0049) für Stellen, die Windows-Shell-Funktionen aufrufen: „Im Explorer
/// zeigen“, „Ordner öffnen“, „Mit Standard-App öffnen“. Explorer und fremde Programme merken sich Pfade dauerhaft
/// (Schnellzugriff, „Zuletzt verwendet“, Ordneransichten in der Registry) — in einer versteckten Sitzung sperrt Lumora
/// diese Aufrufe deshalb. Gesetzt nur vom <see cref="CatalogSessionManager"/> (UI-Thread).
/// </summary>
public static class HiddenSessionPrivacy
{
    public const string ShellBlockedMessage =
        "In einer versteckten Bibliothek öffnet Lumora keine Explorer-Fenster und keine anderen Programme — Windows würde sich die Pfade merken.";

    public static bool IsHiddenSessionActive { get; internal set; }

    /// <summary>Wirft <see cref="InvalidOperationException"/> mit <see cref="ShellBlockedMessage"/> in einer versteckten Sitzung.</summary>
    public static void ThrowIfShellBlocked()
    {
        if (IsHiddenSessionActive)
        {
            throw new InvalidOperationException(ShellBlockedMessage);
        }
    }
}
