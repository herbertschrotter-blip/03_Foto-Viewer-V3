namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Key-Value-Setting (App-Konfiguration in der DB statt im AppData-File).
/// Beispiele: aktuelle LibraryId, letztes Filter-Preset, FTS-Optionen.
/// </summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
}
