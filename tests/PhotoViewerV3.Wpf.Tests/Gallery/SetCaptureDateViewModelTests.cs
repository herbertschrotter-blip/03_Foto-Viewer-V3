using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.CaptureDates;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>Dialog „Aufnahmedatum setzen“ (ADR-0046): Vorgaben, Prüfung der Eingaben, gebaute Änderung, Ordner-Zähler.</summary>
public sealed class SetCaptureDateViewModelTests
{
    private static SetCaptureDateViewModel Create(CaptureDateInfo? current = null, int? withSubfolders = null)
        => new(new CaptureDateDialogRequest("Auswahl", 3, withSubfolders, current));

    [Fact]
    public void Genau_mit_Uhrzeit()
    {
        var vm = Create();
        vm.IsExact.Should().BeTrue();
        vm.ExactDate = new DateTime(2001, 2, 3);
        vm.ExactTime = "04:05";

        vm.CanAccept.Should().BeTrue();
        vm.BuildResult()!.Edit.Should().Be(new CaptureDateEdit.Exact(new DateTime(2001, 2, 3, 4, 5, 0)));

        vm.ExactTime = "25:00";
        vm.CanAccept.Should().BeFalse();
        vm.ErrorText.Should().Contain("HH:mm");
        vm.ExactDate = DateTime.Today.AddDays(10);
        vm.ExactTime = "12:00";
        vm.ErrorText.Should().Contain("Zukunft");
    }

    [Fact]
    public void Ungefaehr_Jahr_oder_Monat()
    {
        var vm = Create();
        vm.IsApproximate = true;
        vm.ApproximateYear = "1982";
        vm.BuildResult()!.Edit.Should().Be(new CaptureDateEdit.Approximate(1982, null));

        vm.ApproximateMonth = vm.MonthOptions[7];
        vm.ApproximateMonth.Label.Should().Be("Juli");
        vm.BuildResult()!.Edit.Should().Be(new CaptureDateEdit.Approximate(1982, 7));

        vm.ApproximateYear = "19x2";
        vm.CanAccept.Should().BeFalse();
        vm.ApproximateYear = (DateTime.Now.Year + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.CanAccept.Should().BeFalse();
    }

    [Fact]
    public void Verschieben_mit_Vorzeichen()
    {
        var vm = Create();
        vm.IsShift = true;
        vm.CanAccept.Should().BeFalse("0 verschiebt nichts");
        vm.ShiftHours = "-1";
        vm.ShiftDays = "2";
        vm.ShiftMinutes = "−30"; // typografisches Minus
        vm.BuildResult()!.Edit.Should().Be(new CaptureDateEdit.Shift(TimeSpan.FromDays(2) - TimeSpan.FromHours(1) - TimeSpan.FromMinutes(30)));
        vm.ShiftDays = "abc";
        vm.CanAccept.Should().BeFalse();
        vm.ShiftDays = int.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.CanAccept.Should().BeFalse("kein Überlauf, sondern ein Hinweis");
        vm.ErrorText.Should().NotBeNull();
    }

    [Fact]
    public void Zuruecksetzen()
    {
        var vm = Create();
        vm.IsReset = true;
        vm.BuildResult()!.Edit.Should().BeOfType<CaptureDateEdit.Reset>();
    }

    [Fact]
    public void Vorgaben_aus_dem_bisherigen_Datum()
    {
        var current = new CaptureDateInfo(1, CaptureDateValue.FromWallClock(new DateTime(1982, 3, 15, 12, 0, 0), 60, CaptureDateSource.Manual, CaptureDatePrecision.Month), CaptureDateSource.Manual);
        var vm = Create(current);

        vm.IsApproximate.Should().BeTrue();
        vm.ApproximateYear.Should().Be("1982");
        vm.ApproximateMonth.Month.Should().Be(3);
        vm.CurrentText.Should().Be("Bisher: ca. März 1982 (manuell)");
    }

    [Fact]
    public void Ordner_mit_und_ohne_Unterordner()
    {
        var vm = Create(withSubfolders: 10);
        vm.IsFolder.Should().BeTrue();
        vm.CountText.Should().Be("10 Dateien");
        vm.IsApproximate = true;
        vm.ApproximateYear = "1982";
        vm.BuildResult()!.IncludeSubfolders.Should().BeTrue();

        vm.IncludeSubfolders = false;
        vm.CountText.Should().Be("3 Dateien");
        vm.BuildResult()!.IncludeSubfolders.Should().BeFalse();

        Create().BuildResult()!.IncludeSubfolders.Should().BeFalse("Auswahl, kein Ordner");
    }

    [Fact]
    public void Statusleiste_zeigt_den_Fortschritt()
    {
        StatusBarViewModel.DescribeCaptureDate(new CaptureDateBackfillStatus(true, 1234, 19556)).Should().Be($"Aufnahmedatum: {1234:N0} / {19556:N0}");
        StatusBarViewModel.DescribeCaptureDate(CaptureDateBackfillStatus.Idle).Should().BeNull();
        StatusBarViewModel.DescribeCaptureDate(new CaptureDateBackfillStatus(false, 0, 0, Message: "wartet auf den Katalog")).Should().Be("Aufnahmedatum: wartet auf den Katalog");
    }
}
