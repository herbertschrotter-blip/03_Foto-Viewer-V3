using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Lumora.Persons.Dialogs;
using Lumora.Services.Faces.Assignment;
using Lumora.Shell;
using Lumora.Shell.Dialogs;

namespace Lumora.Persons;

/// <summary>Dialoge der Personen-Seite über <see cref="IDialogService"/>, <see cref="AppDialogWindow"/> und <see cref="PersonPickerDialog"/> — immer auf dem UI-Thread aufrufen.</summary>
public sealed class PersonsDialogService(IDialogService dialogs) : IPersonsDialogService
{
    public Task<string?> PromptNameAsync(string title, string message, string initialValue)
        => dialogs.PromptTextAsync(title, message, initialValue, "Übernehmen");

    public Task<PersonDetailsInput?> EditPersonAsync(PersonEditRequest request)
        => Task.FromResult(PersonDetailsDialog.Show(Owner, request));

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
        => dialogs.ConfirmAsync(title, message, confirmText, isDestructive);

    public Task<OutlierDecision> AskOutliersAsync(string personName, IReadOnlyList<FaceOutlierResult> outliers, int total)
    {
        var outcome = AppDialogWindow.Show(Owner, new DialogRequest("Gesichter prüfen", DescribeOutliers(personName, outliers.Count, total))
        {
            PrimaryText = outliers.Count < total ? "Ohne diese bestätigen" : null,
            SecondaryText = "Trotzdem alle bestätigen",
            CloseText = "Abbrechen",
            Details = OutlierDetails(outliers),
        });
        return Task.FromResult(outcome.Choice switch
        {
            DialogChoice.Primary => OutlierDecision.SkipOutliers,
            DialogChoice.Secondary => OutlierDecision.ConfirmAll,
            _ => OutlierDecision.Cancel,
        });
    }

    public Task<PersonSummary?> PickPersonAsync(string title, IReadOnlyList<PersonSummary> persons, int? suggestedPersonId, int? excludedPersonId)
        => Task.FromResult(PersonPickerDialog.Show(Owner, title, persons.Where(p => p.Id != excludedPersonId).ToList(), suggestedPersonId));

    public Task ShowErrorAsync(string title, string message) => dialogs.ShowErrorAsync(title, message);

    /// <summary>Text der Ausreißer-Rückfrage (für Tests öffentlich).</summary>
    public static string DescribeOutliers(string personName, int outliers, int total)
        => outliers == 1
            ? $"1 von {total} Gesichtern passt nicht gut zu „{personName}“. Nur Bestätigtes wird gelernt — ein falsches Gesicht verschlechtert die Vorschläge."
            : $"{outliers} von {total} Gesichtern passen nicht gut zu „{personName}“. Nur Bestätigtes wird gelernt — falsche Gesichter verschlechtern die Vorschläge.";

    private static string OutlierDetails(IReadOnlyList<FaceOutlierResult> outliers)
    {
        var sb = new StringBuilder();
        foreach (var group in outliers.GroupBy(o => o.Check.Kind))
        {
            sb.Append("• ").Append(group.Count()).Append(" × ").AppendLine(group.First().Check.Reason);
        }

        return sb.ToString().TrimEnd();
    }

    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
}
