using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Sorter.Services;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>Ergebnis eines Neuladens der Wurzeln: andere Bibliothek bzw. geänderte Wurzeln (Id, Rolle oder Pfad).</summary>
public sealed class SorterRootsReloadedEventArgs : EventArgs
{
    public SorterRootsReloadedEventArgs(bool libraryChanged, bool rootsChanged)
    {
        LibraryChanged = libraryChanged;
        RootsChanged = rootsChanged;
    }

    public bool LibraryChanged { get; }

    public bool RootsChanged { get; }
}

/// <summary>
/// Quelle und Ziel des Sortierers bzw. von Flatten &amp; Move über die Wurzeln der aktiven Bibliothek (ADR-0020,
/// ADR-0024): je eine Wurzel-Auswahl (Rolle „Eingang“/„Archiv“, Pfad, Offline-Wurzeln gesperrt) plus ein frei wählbarer
/// Ordner darunter. Vorbelegung: Quelle = erster erreichbarer Eingang (sonst Archiv), Ziel = Archiv-Wurzel. Lädt neu bei
/// <see cref="ActiveLibraryChangedMessage"/> und <see cref="LibraryRootsChangedMessage"/> (nach dem ersten
/// <see cref="LoadAsync"/>); gültige Auswahlen bleiben dabei erhalten.
/// </summary>
public sealed partial class SorterRootSelection : ObservableObject
{
    private readonly SorterLibraryLocator _libraries;
    private readonly IFolderPickerService _folderPicker;
    private readonly ILogger _logger;
    private readonly string _pickSourceTitle;
    private readonly string _pickTargetTitle;

    private bool _loaded;
    private bool _syncing;
    private string? _rootsSignature;
    private List<SorterRootOption> _roots = new();

    public SorterRootSelection(
        SorterLibraryLocator libraries,
        IFolderPickerService folderPicker,
        IMessenger messenger,
        ILogger logger,
        string pickSourceTitle,
        string pickTargetTitle)
    {
        _libraries = libraries;
        _folderPicker = folderPicker;
        _logger = logger;
        _pickSourceTitle = pickSourceTitle;
        _pickTargetTitle = pickTargetTitle;

        messenger.Register<SorterRootSelection, ActiveLibraryChangedMessage>(this, static (r, _) => r.ReloadFromMessage());
        messenger.Register<SorterRootSelection, LibraryRootsChangedMessage>(this, static (r, m) =>
        {
            if (r.LibraryId is null || r.LibraryId == m.LibraryId)
            {
                r.ReloadFromMessage();
            }
        });
    }

    /// <summary>Nach jedem Neuladen (UI-Thread).</summary>
    public event EventHandler<SorterRootsReloadedEventArgs>? Reloaded;

    /// <summary>Wurzeln der aktiven Bibliothek (Archiv zuerst) für die Quell-Auswahl.</summary>
    public ObservableCollection<SorterRootOption> SourceRoots { get; } = new();

    /// <summary>„Quellordner“ + Wurzeln für die Ziel-Auswahl.</summary>
    public ObservableCollection<SorterRootOption> TargetRoots { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibrary))]
    private int? _libraryId;

    [ObservableProperty]
    private string? _libraryName;

    public bool HasLibrary => LibraryId is not null;

    [ObservableProperty]
    private SorterRootOption? _selectedSourceRoot;

    [ObservableProperty]
    private SorterRootOption? _selectedTargetRoot;

    /// <summary>Quellordner: eine Wurzel oder ein Ordner darunter.</summary>
    [ObservableProperty]
    private string _sourceFolder = string.Empty;

    /// <summary>Zielwurzel des Plans; null = Quellordner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetRootDisplay), nameof(TargetFolderDisplay), nameof(TargetHint), nameof(IsTargetBlocked))]
    private string? _targetRoot;

    /// <summary>Vom ViewModel gesetzt (false während eines Laufs) — sperrt die Ordnerauswahl.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickSourceFolderCommand), nameof(PickTargetRootCommand))]
    private bool _isEnabled = true;

    public string TargetRootDisplay => TargetRoot ?? "= Quellordner";

    /// <summary>Zielordner kurz: Wurzelname plus relativer Pfad („Fotos6“); außerhalb der Wurzeln der volle Pfad.</summary>
    public string TargetFolderDisplay
    {
        get
        {
            var normalized = SafeNormalize(TargetRoot);
            var root = FindRoot(TargetRoot);
            if (normalized is null || root is null)
            {
                return TargetRootDisplay;
            }

            var relative = SorterPaths.RelativeTo(normalized, root.Path!);
            return relative == "." ? root.Name + "\\" : root.Name + "\\" + relative;
        }
    }

    /// <summary>Wo das Ziel liegt („unter Archiv „…““) bzw. warum es gesperrt ist.</summary>
    public string TargetHint
    {
        get
        {
            if (TargetRoot is null)
            {
                return "Die Ordner entstehen im Quellordner.";
            }

            var root = FindRoot(TargetRoot);
            if (root is null)
            {
                return "Außerhalb der Bibliothek — gesperrt (die Dateien fielen aus dem Katalog).";
            }

            if (!root.IsAvailable)
            {
                return $"{root.RoleText} „{root.Name}“ ist offline — gesperrt.";
            }

            return SorterPaths.PathEquals(root.Path!, SafeNormalize(TargetRoot) ?? string.Empty)
                ? $"{root.RoleText}-Wurzel „{root.Name}“"
                : $"unter {root.RoleText} „{root.Name}“";
        }
    }

    /// <summary>Ziel liegt außerhalb aller Wurzeln oder unter einer Offline-Wurzel.</summary>
    public bool IsTargetBlocked => TargetRoot is not null && FindRoot(TargetRoot) is not { IsAvailable: true };

    /// <summary>Lädt Bibliothek und Wurzeln; behält gültige Auswahlen, sonst Vorbelegung. Liefert die Bibliothek (null = keine).</summary>
    public async Task<SorterLibraryInfo?> LoadAsync()
    {
        var info = await _libraries.GetActiveAsync().ConfigureAwait(true);
        var firstLoad = !_loaded;
        _loaded = true;

        var libraryChanged = info?.Id != LibraryId;
        var signature = info is null
            ? string.Empty
            : string.Join("|", info.Roots.Select(r => $"{r.Id}:{r.Role}:{r.Path}"));
        var rootsChanged = !libraryChanged && !string.Equals(signature, _rootsSignature, StringComparison.OrdinalIgnoreCase);
        _rootsSignature = signature;

        LibraryId = info?.Id;
        LibraryName = info?.Name;
        _roots = info?.Roots.Select(r => new SorterRootOption(r)).ToList() ?? new List<SorterRootOption>();

        _syncing = true;
        try
        {
            SourceRoots.Clear();
            TargetRoots.Clear();
            TargetRoots.Add(SorterRootOption.SourceFolderOption);
            foreach (var option in _roots)
            {
                SourceRoots.Add(option);
                TargetRoots.Add(option);
            }
        }
        finally
        {
            _syncing = false;
        }

        if (libraryChanged || firstLoad || FindRoot(SourceFolder) is null)
        {
            SourceFolder = DefaultSource()?.Path ?? string.Empty;
        }

        if (libraryChanged || firstLoad)
        {
            TargetRoot = DefaultTarget()?.Path;
        }

        SyncSourceSelection();
        SyncTargetSelection();
        OnPropertyChanged(nameof(TargetFolderDisplay));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(IsTargetBlocked));

        _logger.LogDebug(
            "Sortierer-Wurzeln geladen: Bibliothek {Library}, {Count} Wurzel(n), Quelle {Source}, Ziel {Target}",
            info?.Name ?? "-", _roots.Count, SourceFolder, TargetRoot ?? "= Quellordner");
        Reloaded?.Invoke(this, new SorterRootsReloadedEventArgs(libraryChanged && !firstLoad, rootsChanged));
        return info;
    }

    /// <summary>Erster erreichbarer Eingang, sonst die Archiv-Wurzel bzw. die erste erreichbare Wurzel.</summary>
    public SorterRootOption? DefaultSource()
        => _roots.FirstOrDefault(r => r.IsInbox && r.IsAvailable)
           ?? _roots.FirstOrDefault(r => r.IsArchive && r.IsAvailable)
           ?? _roots.FirstOrDefault(r => r.IsAvailable);

    /// <summary>Erreichbare Archiv-Wurzel, sonst null (= Quellordner).</summary>
    public SorterRootOption? DefaultTarget() => _roots.FirstOrDefault(r => r.IsArchive && r.IsAvailable);

    /// <summary>Wurzel, unter der der Pfad liegt (längste gewinnt); null = außerhalb.</summary>
    public SorterRootOption? FindRoot(string? path)
    {
        var normalized = SafeNormalize(path);
        return normalized is null
            ? null
            : _roots
                .Where(r => SorterPaths.IsInsideRoot(normalized, r.Path!))
                .OrderByDescending(r => r.Path!.Length)
                .FirstOrDefault();
    }

    partial void OnSelectedSourceRootChanged(SorterRootOption? value)
    {
        if (!_syncing && value?.Path is { } path)
        {
            SourceFolder = path;
        }
    }

    partial void OnSourceFolderChanged(string value) => SyncSourceSelection();

    partial void OnSelectedTargetRootChanged(SorterRootOption? value)
    {
        if (!_syncing && value is not null)
        {
            TargetRoot = value.Path;
        }
    }

    partial void OnTargetRootChanged(string? value) => SyncTargetSelection();

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private void PickSourceFolder()
    {
        var folder = _folderPicker.PickFolder(_pickSourceTitle, FirstExisting(SourceFolder, SelectedSourceRoot?.Path, DefaultSource()?.Path));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SourceFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private void PickTargetRoot()
    {
        var folder = _folderPicker.PickFolder(_pickTargetTitle, FirstExisting(TargetRoot, SelectedTargetRoot?.Path, DefaultTarget()?.Path, SourceFolder));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            TargetRoot = folder;
        }
    }

    private void SyncSourceSelection()
    {
        _syncing = true;
        try
        {
            SelectedSourceRoot = FindRoot(SourceFolder);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncTargetSelection()
    {
        _syncing = true;
        try
        {
            SelectedTargetRoot = TargetRoot is null ? SorterRootOption.SourceFolderOption : FindRoot(TargetRoot);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadFromMessage()
    {
        if (!_loaded)
        {
            return; // Seite noch nie geöffnet — InitializeAsync lädt beim ersten Anzeigen.
        }

        UiThread.Post(async () =>
        {
            try
            {
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sortierer-Wurzeln konnten nicht neu geladen werden");
            }
        });
    }

    private static string? SafeNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return SorterPaths.NormalizeDirectory(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? FirstExisting(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));
}
