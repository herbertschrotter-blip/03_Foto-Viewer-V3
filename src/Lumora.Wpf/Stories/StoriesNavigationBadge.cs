using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;
using Lumora.Services.Stories;
using Lumora.Shell;

namespace Lumora.Stories;

/// <summary>
/// Zähler „3 neu“ am Eintrag „Storys“ der Navigation (Mockup 09): ungesehene Storys des Bereichs. Eine gemeinsame Instanz
/// (<see cref="Shared"/>), an die <c>MainWindow.xaml</c> per <c>x:Static</c> bindet; gesetzt von der Übersicht nach jedem
/// Laden und vom <see cref="StoriesNavigationBadgeUpdater"/>, solange die Seite noch nicht offen war.
/// </summary>
public sealed partial class StoriesNavigationBadge : ObservableObject
{
    public static StoriesNavigationBadge Shared { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(HasNew))]
    private int _newCount;

    /// <summary>„3 neu“; leer ohne neue.</summary>
    public string Text => NewCount > 0 ? $"{NewCount:N0} neu" : string.Empty;

    public bool HasNew => NewCount > 0;

    public void Update(int count) => UiThread.Post(() => NewCount = Math.Max(0, count));
}

/// <summary>
/// Hält den Zähler „n neu“ aktuell, auch wenn die Story-Seite nie geöffnet wurde: nach dem Start, nach jeder Änderung an
/// Storys (gebündelt, 1 s) und beim Bibliothekswechsel liest er die Übersicht (≈ 50 ms bei 500 000 Dateien, ADR-0058).
/// </summary>
public sealed class StoriesNavigationBadgeUpdater : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private readonly IStoryService _stories;
    private readonly StoriesNavigationBadge _badge;
    private readonly ILogger<StoriesNavigationBadgeUpdater> _logger;
    private readonly IMessenger _messenger;
    private int _scheduled;

    public StoriesNavigationBadgeUpdater(IStoryService stories, ILogger<StoriesNavigationBadgeUpdater> logger, StoriesNavigationBadge? badge = null, IMessenger? messenger = null)
    {
        _stories = stories;
        _logger = logger;
        _badge = badge ?? StoriesNavigationBadge.Shared;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>Nach dem Anzeigen des Hauptfensters (eine Zeile in <c>App.xaml.cs</c>).</summary>
    public void Start()
    {
        _messenger.Register<StoriesNavigationBadgeUpdater, StoriesChangedMessage>(this, static (r, _) => r.Schedule());
        _messenger.Register<StoriesNavigationBadgeUpdater, ActiveLibraryChangedMessage>(this, static (r, _) => r.Schedule());
        Schedule();
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Delay).ConfigureAwait(false);
            Volatile.Write(ref _scheduled, 0);
            await RefreshAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Zähler jetzt neu lesen (Tests).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var overview = await _stories.GetOverviewAsync().ConfigureAwait(false);
            _badge.Update(overview.NewCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Storys: Zähler „neu“ nicht gelesen");
        }
    }

    public void Dispose() => _messenger.UnregisterAll(this);
}
