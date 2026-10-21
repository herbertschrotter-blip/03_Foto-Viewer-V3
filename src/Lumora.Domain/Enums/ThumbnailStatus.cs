namespace Lumora.Domain.Enums;

/// <summary>
/// Lifecycle-Zustand eines Thumbnails gemäß ADR-0007.
/// Beim App-Start: UPDATE WHERE Status=Pending SET Status=Stale (Crash-Recovery).
/// </summary>
public enum ThumbnailStatus
{
    Missing = 0,
    Pending = 1,
    Generated = 2,
    Failed = 3,
    Stale = 4,
}
