using System.Diagnostics;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Scanning;

/// <summary>Erreichbarkeitsprüfung mit Timeout (PLAN Kap. 12: lokal 1 s, UNC 3 s).</summary>
public sealed class RootAvailabilityProbeTests
{
    [Fact]
    public async Task Vorhandener_lokaler_Ordner_ist_erreichbar()
    {
        using var fx = await TempLibraryFixture.CreateAsync();

        (await RootAvailabilityProbe.IsAvailableAsync(fx.LibraryRoot)).Should().BeTrue();
    }

    [Fact]
    public async Task Fehlender_lokaler_Ordner_und_leerer_Pfad_sind_nicht_erreichbar()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "gibt-es-nicht-" + Guid.NewGuid().ToString("N"));

        (await RootAvailabilityProbe.IsAvailableAsync(missing)).Should().BeFalse();
        (await RootAvailabilityProbe.IsAvailableAsync(string.Empty)).Should().BeFalse();
        (await RootAvailabilityProbe.IsAvailableAsync("   ")).Should().BeFalse();
    }

    [Fact]
    public void Timeout_richtet_sich_nach_Pfadtyp()
    {
        RootAvailabilityProbe.IsUncPath(@"\\nas\fotos").Should().BeTrue();
        RootAvailabilityProbe.IsUncPath(@"D:\Fotos").Should().BeFalse();
        RootAvailabilityProbe.GetTimeout(@"\\nas\fotos").Should().Be(RootAvailabilityProbe.UncTimeout).And.Be(TimeSpan.FromSeconds(3));
        RootAvailabilityProbe.GetTimeout(@"D:\Fotos").Should().Be(RootAvailabilityProbe.LocalTimeout).And.Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Nicht_erreichbarer_UNC_Pfad_gibt_innerhalb_des_Timeouts_false_zurueck()
    {
        var sw = Stopwatch.StartNew();

        var available = await RootAvailabilityProbe.IsAvailableAsync(@"\\nonexistent-lumora-test-host\share");

        sw.Stop();
        available.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(RootAvailabilityProbe.UncTimeout + TimeSpan.FromSeconds(2), "ein SMB-Timeout von ~30 s darf nicht durchschlagen");
    }

    [Fact]
    public async Task Bereits_abgebrochenes_Token_bricht_die_Pruefung_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => RootAvailabilityProbe.IsAvailableAsync(fx.LibraryRoot, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
