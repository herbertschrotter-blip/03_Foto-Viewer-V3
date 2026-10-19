namespace PhotoViewerV3.Services.Archive;

/// <summary>Das Archiv (oder ein gewählter Eintrag) ist passwortgeschützt — es gibt keinen Passwort-Dialog, nur eine klare Meldung.</summary>
public sealed class ArchiveEncryptedException : Exception
{
    public ArchiveEncryptedException(string archivePath)
        : base($"Das Archiv „{System.IO.Path.GetFileName(archivePath)}“ ist passwortgeschützt und kann nicht entpackt werden.")
    {
        ArchivePath = archivePath;
    }

    public string ArchivePath { get; }
}

/// <summary>Das Archiv ist beschädigt oder das Format wird nicht unterstützt.</summary>
public sealed class ArchiveFormatException : Exception
{
    public ArchiveFormatException(string archivePath, string reason, Exception? inner = null)
        : base($"„{System.IO.Path.GetFileName(archivePath)}“ konnte nicht gelesen werden: {reason}", inner)
    {
        ArchivePath = archivePath;
    }

    public string ArchivePath { get; }
}
