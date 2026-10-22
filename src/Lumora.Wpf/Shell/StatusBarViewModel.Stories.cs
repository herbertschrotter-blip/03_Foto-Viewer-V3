using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;
using Lumora.Services.Stories;

namespace Lumora.Shell;

/// <summary>
/// Story-Erkennung in der Statusleiste (ADR-0060, Mockup 09: „Storys aktuell“): „Storys werden berechnet …“ während eines
/// Durchgangs, danach „Storys aktuell“; „Storys warten auf Aufnahmedaten und Orte“ während des Nachtragens. Erst nach der
/// ersten Meldung sichtbar (vorher ausgeblendet).
/// </summary>
public sealed partial class StatusBarViewModel
{
    /// <summary>Text der Story-Erkennung; null = ausblenden.</summary>
    [ObservableProperty] private string? _storyIndexText;

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>StatusBarViewModel.cs</c>).</summary>
    private void InitStories()
        => _messenger.Register<StatusBarViewModel, StoryIndexStatusMessage>(this, static (r, m) => UiThread.Post(() => r.ApplyStoryStatus(m.Status)));

    private void ApplyStoryStatus(StoryIndexStatus status)
        => StoryIndexText = DescribeStoryIndex(status);

    /// <summary>Text der Statusleiste; leerer Text = ausblenden.</summary>
    internal static string? DescribeStoryIndex(StoryIndexStatus status)
        => string.IsNullOrWhiteSpace(status.Text) ? null : status.Text;
}
