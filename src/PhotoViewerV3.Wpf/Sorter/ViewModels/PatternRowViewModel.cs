using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Services.Sorter;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>Eine Zeile der Pattern-Liste mit Live-Validierung (Regex kompilierbar, Zahlenfelder, Capture-Gruppe vorhanden).</summary>
public sealed partial class PatternRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _regex = string.Empty;

    [ObservableProperty]
    private string _groupCaptureText = "1";

    [ObservableProperty]
    private string _priorityText = "50";

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool BuiltIn { get; init; }

    public bool CanDelete => !BuiltIn;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Wird bei jeder Eingabe ausgelöst — der Besitzer validiert die ganze Liste (Duplikate).</summary>
    public event EventHandler? Changed;

    public static PatternRowViewModel FromModel(SorterPattern p) => new()
    {
        Name = p.Name,
        Regex = p.Regex,
        GroupCaptureText = p.GroupCapture.ToString(CultureInfo.InvariantCulture),
        PriorityText = p.Priority.ToString(CultureInfo.InvariantCulture),
        Description = p.Description,
        Enabled = p.Enabled,
        BuiltIn = p.BuiltIn,
    };

    public SorterPattern ToModel() => new()
    {
        Name = Name.Trim(),
        Regex = Regex,
        GroupCapture = ParseInt(GroupCaptureText, 1),
        Priority = ParseInt(PriorityText, 50),
        Enabled = Enabled,
        BuiltIn = BuiltIn,
        Description = Description.Trim(),
    };

    /// <summary>Setzt <see cref="Error"/>; <paramref name="duplicateName"/> kommt vom Besitzer (Listenwissen).</summary>
    public void Validate(bool duplicateName)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Name fehlt");
        }
        else if (duplicateName)
        {
            errors.Add("Name ist doppelt");
        }

        System.Text.RegularExpressions.Regex? compiled = null;
        if (!SorterPatternEngine.TryCompile(Regex, out compiled, out var regexError))
        {
            errors.Add(string.IsNullOrWhiteSpace(Regex) ? "Regex fehlt" : $"Regex ungültig: {regexError}");
        }

        if (!int.TryParse(GroupCaptureText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capture) || capture < 0)
        {
            errors.Add("Capture-Gruppe muss eine Zahl ≥ 0 sein");
        }
        else if (compiled is not null && capture > SorterPatternEngine.GroupCount(compiled))
        {
            var count = SorterPatternEngine.GroupCount(compiled);
            errors.Add(count == 0 ? "Regex hat keine Capture-Gruppe (0 wählen oder Klammern setzen)" : $"Regex hat nur {count} Capture-Gruppe(n)");
        }

        if (!int.TryParse(PriorityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            errors.Add("Priorität muss eine Zahl sein");
        }

        Error = errors.Count == 0 ? null : string.Join(" · ", errors);
    }

    internal static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    partial void OnNameChanged(string value) => RaiseChanged();

    partial void OnRegexChanged(string value) => RaiseChanged();

    partial void OnGroupCaptureTextChanged(string value) => RaiseChanged();

    partial void OnPriorityTextChanged(string value) => RaiseChanged();

    partial void OnDescriptionChanged(string value) => RaiseChanged();

    partial void OnEnabledChanged(bool value) => RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
