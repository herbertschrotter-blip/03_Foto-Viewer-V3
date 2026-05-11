namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Art einer Datei-Operation. V0.1 nur Recycle (Delete-to-Recycle-Bin).
/// Move/Rename ab V0.2, MkDir/Copy in V0.3+.
/// </summary>
public enum OperationType
{
    Recycle = 0,
    Move = 1,
    Copy = 2,
    Rename = 3,
    MkDir = 4,
}
