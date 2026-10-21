using System.Threading.Tasks;
using System.Windows;

namespace Lumora.Places.Dialogs;

/// <summary>Anfrage „Hier verorten?“: wie viele Fotos, wohin, welcher Ort liegt dort.</summary>
/// <param name="PlaceName">Nächster Ort (offline, GeoNames) z. B. „Wien, Wien, Österreich“; null = unbekannt.</param>
public sealed record AssignLocationRequest(int FileCount, double Latitude, double Longitude, string? PlaceName);

/// <summary>Antwort: bestätigt, mit „Änderungsdatum beibehalten“.</summary>
public sealed record AssignLocationAnswer(bool KeepModifiedTime);

/// <summary>Dialoge der Seite „Orte“ (Rauchtest: Fake mit vorgegebener Antwort).</summary>
public interface IPlacesDialogs
{
    /// <summary>Bestätigung vor dem Verorten; null = abgebrochen.</summary>
    Task<AssignLocationAnswer?> ConfirmAssignAsync(AssignLocationRequest request, Window? owner);
}

/// <summary>Echte Dialoge (eigenes <see cref="AssignLocationDialog"/> auf AppFluentWindow).</summary>
public sealed class PlacesDialogs : IPlacesDialogs
{
    public Task<AssignLocationAnswer?> ConfirmAssignAsync(AssignLocationRequest request, Window? owner)
        => Task.FromResult(AssignLocationDialog.Show(owner, request));
}
