namespace PhotoViewerV3.Spike02.Data;

public enum GalleryFilter
{
    All,
    Images,
    Videos,
    Favorites,
}

public sealed record GalleryFileDto(
    int Id,
    int FolderId,
    string Name,
    string Extension,
    long Size,
    long ModifiedAtUnix,
    int MediaType,
    bool IsFavorite,
    string FolderPath)
{
    public bool IsVideo => MediaType == 1;
    public string SizeDisplay => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{Size / 1024.0 / 1024.0:N1} MB",
        _ => $"{Size / 1024.0 / 1024.0 / 1024.0:N2} GB",
    };
}
