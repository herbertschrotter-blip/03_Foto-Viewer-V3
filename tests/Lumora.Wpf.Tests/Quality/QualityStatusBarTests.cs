using Lumora.Services.Quality;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Quality;

/// <summary>Statusleiste der Foto-Güte (ADR-0057): „Foto-Güte: x von y bewertet“.</summary>
public sealed class QualityStatusBarTests
{
    [Fact]
    public void Fortschritt_und_Ruhe()
    {
        StatusBarViewModel.DescribeQualityIndex(new FileQualityIndexStatus(true, FileQualityIndexStatus.PixelPhase, 12, 3400))
            .Should().Be($"Foto-Güte: 12 von {3400:N0} bewertet");
        StatusBarViewModel.DescribeQualityIndex(new FileQualityIndexStatus(true, FileQualityIndexStatus.AestheticPhase, 5, 900))
            .Should().Be("Foto-Güte: Bildwirkung 5 von 900");
        StatusBarViewModel.DescribeQualityIndex(FileQualityIndexStatus.Idle).Should().BeNull();
        StatusBarViewModel.DescribeQualityIndex(new FileQualityIndexStatus(false, string.Empty, 0, 0, "wartet auf den Katalog"))
            .Should().Be("Foto-Güte: wartet auf den Katalog");
    }
}
