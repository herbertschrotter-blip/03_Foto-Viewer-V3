using System;

namespace PhotoViewerV3.Shell;

/// <summary>Tags der Navigationsziele (NavigationViewItem.TargetPageTag in MainWindow.xaml).</summary>
public static class NavigationTags
{
    public const string Library = "library";
    public const string Gallery = "gallery";
    public const string Folders = "folders";
    public const string Tags = "tags";
    public const string Settings = "settings";
}

/// <summary>
/// Programmatische Navigation zwischen den Seiten der Shell. Singleton; von jedem Thread aufrufbar
/// (wird auf den UI-Thread gestellt). Aufrufe vor dem Anbinden des Hauptfensters werden gemerkt.
/// </summary>
public interface INavigationService
{
    /// <summary>Tag der aktuell angezeigten Seite oder null, solange noch nicht navigiert wurde.</summary>
    string? CurrentTag { get; }

    /// <summary>Wird nach jeder Navigation (Klick oder NavigateTo) mit dem Ziel-Tag ausgelöst (UI-Thread).</summary>
    event EventHandler<string>? Navigated;

    /// <summary>Navigiert zur Seite mit dem Tag (siehe <see cref="NavigationTags"/>).</summary>
    void NavigateTo(string tag);
}
