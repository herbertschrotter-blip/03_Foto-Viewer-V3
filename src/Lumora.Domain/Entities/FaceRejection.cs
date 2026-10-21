namespace Lumora.Domain.Entities;

/// <summary>
/// „Dieses Gesicht ist NICHT diese Person“ (ADR-0035, Tabelle "FaceRejections", Schlüssel FaceId + PersonId) — damit
/// das Lernen (Welle 2) abgelehnte Vorschläge nicht erneut macht. Beide Fremdschlüssel ON DELETE CASCADE.
/// </summary>
public sealed class FaceRejection
{
    public int FaceId { get; set; }
    public int PersonId { get; set; }
}
