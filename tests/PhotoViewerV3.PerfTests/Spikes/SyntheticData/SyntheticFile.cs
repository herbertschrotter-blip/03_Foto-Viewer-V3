namespace PhotoViewerV3.PerfTests.Spikes.SyntheticData;

internal enum SyntheticMediaType : byte
{
    Image = 0,
    Video = 1,
}

internal sealed record SyntheticFile(
    int Id,
    int FolderId,
    string Name,
    string Extension,
    long Size,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    SyntheticMediaType MediaType,
    int? Width,
    int? Height,
    int? DurationMs,
    string? Codec,
    bool IsFavorite,
    byte? Rating,
    bool Missing);
