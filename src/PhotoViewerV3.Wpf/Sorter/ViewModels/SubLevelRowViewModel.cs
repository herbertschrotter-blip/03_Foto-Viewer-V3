using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Services.Sorter;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>Eine Zeile der Sub-Level-Liste (Multi-Level); Reihenfolge in der Liste = Ordnertiefe.</summary>
public sealed partial class SubLevelRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _regex = string.Empty;

    [ObservableProperty]
    private string _groupCaptureText = "1";

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public event EventHandler? Changed;

    public static SubLevelRowViewModel FromModel(SorterSubLevel l) => new()
    {
        Name = l.Name,
        Regex = l.Regex,
        GroupCaptureText = l.GroupCapture.ToString(CultureInfo.InvariantCulture),
        Enabled = l.Enabled,
    };

    public SorterSubLevel ToModel() => new()
    {
        Name = Name.Trim(),
        Regex = Regex,
        GroupCapture = PatternRowViewModel.ParseInt(GroupCaptureText, 1),
        Enabled = Enabled,
    };

    public void Validate()
    {
        var errors = new List<string>();
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
            errors.Add($"Regex hat nur {SorterPatternEngine.GroupCount(compiled)} Capture-Gruppe(n)");
        }

        Error = errors.Count == 0 ? null : string.Join(" · ", errors);
    }

    partial void OnNameChanged(string value) => RaiseChanged();

    partial void OnRegexChanged(string value) => RaiseChanged();

    partial void OnGroupCaptureTextChanged(string value) => RaiseChanged();

    partial void OnEnabledChanged(bool value) => RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
