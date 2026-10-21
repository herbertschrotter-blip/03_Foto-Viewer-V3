using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Domain.Enums;
using Lumora.Services.Metadata;

namespace Lumora.Gallery.CaptureDates;

/// <summary>Art der Änderung im Dialog „Aufnahmedatum setzen“.</summary>
public enum CaptureDateDialogMode
{
    Exact,
    Approximate,
    Shift,
    Reset,
}

/// <summary>
/// Aufruf des Dialogs: Betreff („3 Dateien“, „Ordner „Urlaub““), Zahl der Dateien, bei einem Ordner zusätzlich die Zahl
/// samt Unterordnern (dann erscheint „Unterordner einschließen“), und das bisherige Datum der ersten Datei als Vorgabe.
/// </summary>
public sealed record CaptureDateDialogRequest(string Subject, int FileCount, int? FileCountWithSubfolders = null, CaptureDateInfo? Current = null);

/// <summary>Ergebnis: die Änderung und (nur bei Ordnern) ob Unterordner mitzählen.</summary>
public sealed record CaptureDateDialogResult(CaptureDateEdit Edit, bool IncludeSubfolders);

/// <summary>Eintrag der Monatsauswahl („— nur Jahr“, Januar … Dezember).</summary>
public sealed record CaptureMonthOption(int Month, string Label);

/// <summary>
/// Logik des Dialogs „Aufnahmedatum setzen“ (ADR-0046): genaues Datum mit Uhrzeit, ungefähr (Jahr bzw. Monat + Jahr),
/// Verschieben um Tage/Stunden/Minuten (± — falsch gestellte Kamera) oder Zurücksetzen (wieder aus der Datei lesen).
/// Prüft die Eingaben laufend (<see cref="ErrorText"/>, <see cref="CanAccept"/>) und baut daraus <see cref="CaptureDateEdit"/>.
/// </summary>
public sealed partial class SetCaptureDateViewModel : ObservableObject
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public SetCaptureDateViewModel(CaptureDateDialogRequest request)
    {
        Request = request;
        MonthOptions = new[] { new CaptureMonthOption(0, "— nur Jahr") }
            .Concat(Enumerable.Range(1, 12).Select(m => new CaptureMonthOption(m, German.DateTimeFormat.GetMonthName(m))))
            .ToList();

        var current = request.Current?.Value;
        var local = current?.Local ?? DateTime.Today.AddHours(12);
        _exactDate = local.Date;
        _exactTime = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        _approximateYear = current is { } c ? c.Local.Year.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _approximateMonth = current is { Precision: CaptureDatePrecision.Month } m
            ? MonthOptions[m.Local.Month]
            : MonthOptions[0];
        _mode = current is { Precision: CaptureDatePrecision.Year or CaptureDatePrecision.Month }
            ? CaptureDateDialogMode.Approximate
            : CaptureDateDialogMode.Exact;
    }

    public CaptureDateDialogRequest Request { get; }

    public IReadOnlyList<CaptureMonthOption> MonthOptions { get; }

    public bool IsFolder => Request.FileCountWithSubfolders is not null;

    /// <summary>„3 Dateien“ bzw. bei Ordnern die Zahl passend zu „Unterordner einschließen“.</summary>
    public string CountText
    {
        get
        {
            var count = AffectedCount;
            return count == 1 ? "1 Datei" : $"{count.ToString("N0", German)} Dateien";
        }
    }

    public int AffectedCount => IncludeSubfolders && Request.FileCountWithSubfolders is { } all ? all : Request.FileCount;

    /// <summary>Bisheriges Datum (erste Datei) als Hinweis, sonst null.</summary>
    public string? CurrentText => Request.Current is { } current ? "Bisher: " + current.Text : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExact), nameof(IsApproximate), nameof(IsShift), nameof(IsReset), nameof(ErrorText), nameof(CanAccept))]
    private CaptureDateDialogMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(AffectedCount), nameof(CanAccept))]
    private bool _includeSubfolders = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(CanAccept))]
    private DateTime? _exactDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(CanAccept))]
    private string _exactTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(CanAccept))]
    private string _approximateYear;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(CanAccept))]
    private CaptureMonthOption _approximateMonth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(CanAccept))]
    private string _shiftDays = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(CanAccept))]
    private string _shiftHours = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(CanAccept))]
    private string _shiftMinutes = "0";

    public bool IsExact
    {
        get => Mode == CaptureDateDialogMode.Exact;
        set => SetMode(value, CaptureDateDialogMode.Exact);
    }

    public bool IsApproximate
    {
        get => Mode == CaptureDateDialogMode.Approximate;
        set => SetMode(value, CaptureDateDialogMode.Approximate);
    }

    public bool IsShift
    {
        get => Mode == CaptureDateDialogMode.Shift;
        set => SetMode(value, CaptureDateDialogMode.Shift);
    }

    public bool IsReset
    {
        get => Mode == CaptureDateDialogMode.Reset;
        set => SetMode(value, CaptureDateDialogMode.Reset);
    }

    /// <summary>Fehler der aktuellen Eingabe (null = gültig).</summary>
    public string? ErrorText => TryBuild(out _, out var error) ? null : error;

    public bool CanAccept => AffectedCount > 0 && TryBuild(out _, out _);

    /// <summary>Ergebnis für „Datum setzen“ oder null bei ungültiger Eingabe.</summary>
    public CaptureDateDialogResult? BuildResult()
        => TryBuild(out var edit, out _) && AffectedCount > 0 ? new CaptureDateDialogResult(edit!, IsFolder && IncludeSubfolders) : null;

    /// <summary>Prüft die Eingaben der gewählten Art und baut die Änderung.</summary>
    public bool TryBuild(out CaptureDateEdit? edit, out string? error)
    {
        edit = null;
        error = null;
        switch (Mode)
        {
            case CaptureDateDialogMode.Exact:
                if (ExactDate is not { } date)
                {
                    error = "Bitte ein Datum wählen.";
                    return false;
                }

                if (!TryParseTime(ExactTime, out var time))
                {
                    error = "Uhrzeit bitte als HH:mm angeben (z. B. 15:30).";
                    return false;
                }

                var wall = date.Date + time;
                if (wall.Year < CaptureDateValue.MinPlausible.Year || wall > DateTime.Now.AddDays(1))
                {
                    error = "Das Datum liegt in der Zukunft oder zu weit zurück.";
                    return false;
                }

                edit = new CaptureDateEdit.Exact(wall);
                return true;

            case CaptureDateDialogMode.Approximate:
                if (!int.TryParse(ApproximateYear?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                    || year < CaptureDateValue.MinPlausible.Year || year > DateTime.Now.Year)
                {
                    error = $"Bitte ein Jahr zwischen {CaptureDateValue.MinPlausible.Year} und {DateTime.Now.Year} angeben.";
                    return false;
                }

                edit = new CaptureDateEdit.Approximate(year, ApproximateMonth is { Month: > 0 } option ? (int?)option.Month : null);
                return true;

            case CaptureDateDialogMode.Shift:
                if (!TryParseSigned(ShiftDays, out var days) || !TryParseSigned(ShiftHours, out var hours) || !TryParseSigned(ShiftMinutes, out var minutes))
                {
                    error = "Tage, Stunden und Minuten bitte als ganze Zahlen angeben (mit „-“ zurück).";
                    return false;
                }

                var delta = TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
                if (delta == TimeSpan.Zero)
                {
                    error = "Bitte eine Verschiebung angeben (z. B. -1 Stunde).";
                    return false;
                }

                if (delta.Duration() > TimeSpan.FromDays(36_500))
                {
                    error = "Höchstens 100 Jahre verschieben.";
                    return false;
                }

                edit = new CaptureDateEdit.Shift(delta);
                return true;

            default:
                edit = new CaptureDateEdit.Reset();
                return true;
        }
    }

    private void SetMode(bool value, CaptureDateDialogMode mode)
    {
        if (value)
        {
            Mode = mode;
        }
    }

    private static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var t = text?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            return true; // ohne Uhrzeit: 00:00
        }

        return TimeSpan.TryParseExact(t, ["h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss", "hh\\.mm", "h\\.mm"], CultureInfo.InvariantCulture, out time)
               && time < TimeSpan.FromDays(1);
    }

    private static bool TryParseSigned(string? text, out int value)
    {
        var t = (text ?? string.Empty).Trim().Replace('−', '-');
        if (t.Length == 0)
        {
            value = 0;
            return true;
        }

        return int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value is >= -100_000 and <= 100_000;
    }
}
