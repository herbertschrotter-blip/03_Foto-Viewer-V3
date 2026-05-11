namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Lifecycle eines OperationBatch (mehrere FileOperations, atomar applied/undone).
/// </summary>
public enum BatchStatus
{
    Pending = 0,
    Applying = 1,
    Applied = 2,
    Undoing = 3,
    Undone = 4,
    Failed = 5,
}
