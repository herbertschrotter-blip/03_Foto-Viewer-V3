namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Lifecycle einer einzelnen FileOperation innerhalb eines OperationBatch.
/// </summary>
public enum FileOperationStatus
{
    Pending = 0,
    Applying = 1,
    Applied = 2,
    Failed = 3,
    Undone = 4,
}
