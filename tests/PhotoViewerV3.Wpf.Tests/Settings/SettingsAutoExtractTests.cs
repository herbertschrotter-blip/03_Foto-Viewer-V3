using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Archive;
using PhotoViewerV3.Settings;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Wpf.Tests.Settings;

/// <summary>
/// Einstellungen → „Archive“: Schalter „Neue Archive in Eingangsordnern automatisch entpacken“ und „Papierkorb danach“
/// (ADR-0032) sowie der Statusleisten-Text des automatischen Entpackens.
/// </summary>
public sealed class SettingsAutoExtractTests
{
    private sealed class FakeAutoExtract : IArchiveAutoExtractService
    {
        public bool IsEnabled { get; set; }

        public bool RecycleAfterExtract { get; set; }

        public ArchiveAutoExtractSummary? LastSummary => null;

        public List<bool> EnabledCalls { get; } = [];

        public List<bool> RecycleCalls { get; } = [];

        public Exception? Fail { get; set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            EnabledCalls.Add(enabled);
            if (Fail is not null)
            {
                return Task.FromException(Fail);
            }

            IsEnabled = enabled;
            return Task.CompletedTask;
        }

        public Task SetRecycleAfterExtractAsync(bool recycle, CancellationToken ct = default)
        {
            RecycleCalls.Add(recycle);
            if (Fail is not null)
            {
                return Task.FromException(Fail);
            }

            RecycleAfterExtract = recycle;
            return Task.CompletedTask;
        }

        public Task WhenIdleAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static SettingsViewModel Create(IArchiveAutoExtractService? service)
        // Nur LoadAsync bräuchte die übrigen Dienste — die Archiv-Schalter kommen ohne sie aus.
        => new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<SettingsViewModel>.Instance, service);

    [Fact]
    public void Laden_zeigt_den_gespeicherten_Stand_ohne_ihn_neu_zu_speichern()
    {
        var service = new FakeAutoExtract { IsEnabled = true, RecycleAfterExtract = true };
        var vm = Create(service);
        vm.IsAutoExtractEnabled.Should().BeFalse("vor dem Laden gilt der Standard: aus");

        vm.LoadAutoExtractSettings();

        vm.IsAutoExtractAvailable.Should().BeTrue();
        vm.IsAutoExtractEnabled.Should().BeTrue();
        vm.IsAutoExtractRecycle.Should().BeTrue();
        service.EnabledCalls.Should().BeEmpty();
        service.RecycleCalls.Should().BeEmpty();
    }

    [Fact]
    public void Schalter_speichern_sofort_ueber_den_Dienst()
    {
        var service = new FakeAutoExtract();
        var vm = Create(service);
        vm.LoadAutoExtractSettings();

        vm.IsAutoExtractEnabled = true;
        vm.IsAutoExtractRecycle = true;
        vm.IsAutoExtractEnabled = false;

        service.EnabledCalls.Should().Equal(true, false);
        service.RecycleCalls.Should().Equal(true);
        service.IsEnabled.Should().BeFalse();
        service.RecycleAfterExtract.Should().BeTrue();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Speicherfehler_meldet_und_setzt_den_Schalter_zurueck()
    {
        var service = new FakeAutoExtract { Fail = new System.IO.IOException("Datenbank gesperrt") };
        var vm = Create(service);
        vm.LoadAutoExtractSettings();

        vm.IsAutoExtractEnabled = true;

        vm.ErrorMessage.Should().Contain("Automatisches Entpacken").And.Contain("Datenbank gesperrt");
        vm.IsErrorOpen.Should().BeTrue();
        vm.IsAutoExtractEnabled.Should().BeFalse("der gespeicherte Stand gilt weiter");
        service.EnabledCalls.Should().Equal(true);
    }

    [Fact]
    public void Ohne_Dienst_bleibt_der_Abschnitt_deaktiviert()
    {
        var vm = Create(null);

        vm.LoadAutoExtractSettings();
        vm.IsAutoExtractEnabled = true;

        vm.IsAutoExtractAvailable.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Statusleiste_beschreibt_Fortschritt_Erfolg_und_gemeldete_Archive()
    {
        var running = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Running, 2, 3, "urlaub.zip"));
        running.Text.Should().Be("Archiv 2/3 wird automatisch entpackt: urlaub.zip");
        running.IsWarning.Should().BeFalse();

        var ok = new ArchiveAutoExtractSummary(1,
            [new ArchiveAutoExtractItem(7, @"C:\Eingang\urlaub.zip", ArchiveAutoExtractItemState.Extracted, @"C:\Eingang\urlaub", 12, true, null)],
            null, null, false);
        var done = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Finished, 1, 1, null, ok));
        done.Text.Should().Be("Automatisch entpackt: 1 Archiv · 12 Dateien · 1 im Papierkorb");
        done.IsWarning.Should().BeFalse();
        done.Details.Should().BeNull();

        var mixed = new ArchiveAutoExtractSummary(1,
            [
                new ArchiveAutoExtractItem(7, @"C:\Eingang\urlaub.zip", ArchiveAutoExtractItemState.Extracted, @"C:\Eingang\urlaub", 12, false, null),
                new ArchiveAutoExtractItem(8, @"C:\Eingang\geheim.rar", ArchiveAutoExtractItemState.Failed, null, 0, false, "Das Archiv „geheim.rar“ ist passwortgeschützt und kann nicht entpackt werden."),
            ],
            null, null, false);
        var warn = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Finished, 2, 2, null, mixed));
        warn.Text.Should().Be("Automatisch entpackt: 1 Archiv · 12 Dateien · 1 nicht entpackt");
        warn.IsWarning.Should().BeTrue();
        warn.Details.Should().Contain("geheim.rar").And.Contain("passwortgeschützt");

        var onlyFailed = new ArchiveAutoExtractSummary(1, [mixed.Items[1]], null, null, false);
        var failed = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Finished, 1, 1, null, onlyFailed));
        failed.Text.Should().StartWith("Archiv nicht automatisch entpackt: geheim.rar — ");
        failed.IsWarning.Should().BeTrue();
    }
}
