using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Wpf.Tests.Fixtures;

/// <summary>Katalog-Kontext für Tests mit umschaltbarem <see cref="IsHidden"/> (ADR-0048/0049).</summary>
public sealed class TestCatalogContext : ICatalogContext
{
    public bool IsHidden { get; private set; }

    public string CatalogRoot => string.Empty;

    public string DatabasePath => string.Empty;

    public string ThumbnailsRoot => string.Empty;

    public string LogsRoot => string.Empty;

    public string CacheRoot => string.Empty;

    public string TempRoot => string.Empty;

    public void SetHidden(bool hidden) => IsHidden = hidden;
}
