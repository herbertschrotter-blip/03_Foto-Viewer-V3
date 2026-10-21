using System.IO;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Wpf.Tests.Shell;

/// <summary>Bibliotheken im Speicher für die Tests zu Navigation, Umschalter und „Eingang hinzufügen…“ (ADR-0042).</summary>
internal sealed class InMemoryLibraries : ILibraryService
{
    private readonly List<LibraryInfo> _items = [];
    private int _nextRootId = 100;

    public List<int> SetActiveCalls { get; } = [];

    public List<(int LibraryId, string Path, LibraryRootRole Role)> AddedRoots { get; } = [];

    public Exception? AddRootFailure { get; set; }

    public LibraryInfo Add(int id, string name, int fileCount, DateTime lastOpened)
    {
        var info = new LibraryInfo(id, name, $@"C:\{name}", lastOpened, lastOpened, 1, fileCount, true,
            [new LibraryRootInfo(id * 10, id, $@"C:\{name}", LibraryRootRole.Archive, name, 0, true)]);
        _items.Add(info);
        return info;
    }

    public Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryInfo>>(_items.ToList());

    public Task<LibraryInfo?> GetAsync(int libraryId, CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(l => l.Id == libraryId));

    public Task<LibraryInfo?> GetActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.OrderByDescending(l => l.LastOpenedAt).ThenByDescending(l => l.Id).FirstOrDefault());

    public Task SetActiveAsync(int libraryId, CancellationToken ct = default)
    {
        SetActiveCalls.Add(libraryId);
        var index = _items.FindIndex(l => l.Id == libraryId);
        _items[index] = _items[index] with { LastOpenedAt = DateTime.UtcNow.AddDays(1) };
        return Task.CompletedTask;
    }

    public Task<LibraryRootInfo> AddRootAsync(int libraryId, string path, LibraryRootRole role, string? name = null, CancellationToken ct = default)
    {
        if (AddRootFailure is not null)
        {
            return Task.FromException<LibraryRootInfo>(AddRootFailure);
        }

        AddedRoots.Add((libraryId, path, role));
        var root = new LibraryRootInfo(_nextRootId++, libraryId, path, role, name ?? Path.GetFileName(path), 1, true);
        var index = _items.FindIndex(l => l.Id == libraryId);
        _items[index] = _items[index] with { Roots = [.. _items[index].Roots, root] };
        return Task.FromResult(root);
    }

    public Task<LibraryInfo> CreateAsync(string name, string rootPath, CancellationToken ct = default) => throw new NotSupportedException();

    public Task RenameAsync(int libraryId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

    public Task ClearAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task DeleteThumbnailCacheAsync(int libraryId) => throw new NotSupportedException();

    public Task DeleteAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<LibraryRootInfo>> GetRootsAsync(int libraryId, CancellationToken ct = default) =>
        Task.FromResult(_items.First(l => l.Id == libraryId).Roots);

    public Task RenameRootAsync(int rootId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

    public Task SetRootRoleAsync(int rootId, LibraryRootRole role, CancellationToken ct = default) => throw new NotSupportedException();

    public Task RemoveRootAsync(int rootId, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Merkt Navigationsziele; <see cref="CurrentTag"/> frei setzbar.</summary>
internal sealed class RecordingNavigationService : INavigationService
{
    public List<string> Tags { get; } = [];

    public string? CurrentTag { get; set; }

    public event EventHandler<string>? Navigated;

    public void NavigateTo(string tag) => Tags.Add(tag);

    public void RaiseNavigated(string tag)
    {
        CurrentTag = tag;
        Navigated?.Invoke(this, tag);
    }
}

/// <summary>Dialoge mit vorgegebenen Antworten.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public string? FolderToPick { get; set; }

    public bool ConfirmAnswer { get; set; }

    public List<string> Errors { get; } = [];

    public List<string> Confirmations { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
    {
        Confirmations.Add(title);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
        => Task.FromResult(DialogChoice.None);

    public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult<string?>(null);

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    public string? PickFolder(string title) => FolderToPick;
}
