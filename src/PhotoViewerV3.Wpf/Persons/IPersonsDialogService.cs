using System.Collections.Generic;
using System.Threading.Tasks;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Persons;

/// <summary>Antwort auf die Ausreißer-Rückfrage beim Bestätigen.</summary>
public enum OutlierDecision
{
    /// <summary>Nichts bestätigen.</summary>
    Cancel = 0,

    /// <summary>Alle ausgewählten Gesichter bestätigen, auch die zweifelhaften.</summary>
    ConfirmAll = 1,

    /// <summary>Nur die unauffälligen bestätigen, zweifelhafte bleiben offen.</summary>
    SkipOutliers = 2,
}

/// <summary>
/// Dialoge der Personen-Seite (Namen eingeben, Bestätigungen, Ausreißer-Rückfrage, Person auswählen). Fenster nur über
/// <c>AppDialogWindow</c>/<c>AppFluentWindow</c> (CLAUDE.md); im Rauchtest durch einen Fake ersetzt.
/// </summary>
public interface IPersonsDialogService
{
    Task<string?> PromptNameAsync(string title, string message, string initialValue);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);

    /// <summary>„n der m Gesichter passen nicht gut zu …“ mit den Gründen als Details.</summary>
    Task<OutlierDecision> AskOutliersAsync(string personName, IReadOnlyList<FaceOutlierResult> outliers, int total);

    /// <summary>Person auswählen (Suche); <paramref name="suggestedPersonId"/> ist vorgewählt, <paramref name="excludedPersonId"/> fehlt.</summary>
    Task<PersonSummary?> PickPersonAsync(string title, IReadOnlyList<PersonSummary> persons, int? suggestedPersonId, int? excludedPersonId);

    Task ShowErrorAsync(string title, string message);
}
