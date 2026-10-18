using System.Runtime.Versioning;
using Microsoft.VisualBasic.FileIO;

namespace PhotoViewerV3.Services.Operations;

/// <summary>
/// Papierkorb über <see cref="FileSystem.DeleteFile(string, UIOption, RecycleOption)"/> aus
/// Microsoft.VisualBasic.Core (Shared Framework; intern SHFileOperation mit FOF_ALLOWUNDO).
/// Keine Rückfrage-Dialoge; nur bei Shell-Fehlern zeigt Windows einen Fehlerdialog
/// (<see cref="UIOption.OnlyErrorDialogs"/>). Kein Hard-Delete (Repo-Regel: Papierkorb statt Löschen).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : IRecycleBin
{
    public void MoveToRecycleBin(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FileSystem.DeleteFile(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }
}
