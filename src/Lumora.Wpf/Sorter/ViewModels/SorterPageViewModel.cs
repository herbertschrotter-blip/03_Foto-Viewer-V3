using System.Threading.Tasks;

namespace Lumora.Sorter.ViewModels;

/// <summary>Seiten-ViewModel: hält die beiden Bereiche „Sortieren“ und „Flatten &amp; Move“ und initialisiert sie einmalig.</summary>
public sealed class SorterPageViewModel
{
    private Task? _initialization;

    public SorterPageViewModel(FileSorterViewModel sort, FlattenViewModel flatten)
    {
        Sort = sort;
        Flatten = flatten;
    }

    public FileSorterViewModel Sort { get; }

    public FlattenViewModel Flatten { get; }

    public Task EnsureInitializedAsync() => _initialization ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        await Sort.InitializeAsync();
        await Flatten.InitializeAsync();
    }
}
