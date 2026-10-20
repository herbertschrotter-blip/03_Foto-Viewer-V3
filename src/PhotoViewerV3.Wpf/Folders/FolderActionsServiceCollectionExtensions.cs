using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Folders.Adoption;

namespace PhotoViewerV3.Folders;

/// <summary>DI-Registrierung der Ordner-Aktionen (Wpf/Folders); Picker per TryAdd, damit der Rauchtest seinen Fake vorher setzt.</summary>
public static class FolderActionsServiceCollectionExtensions
{
    public static IServiceCollection AddFolderActions(this IServiceCollection services)
    {
        services.TryAddSingleton<IFolderAdoptionPicker, FolderAdoptionPicker>();
        services.TryAddSingleton<FolderActions>();
        return services;
    }
}
