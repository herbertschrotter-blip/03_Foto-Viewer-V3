using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PhotoViewerV3.Lightbox;

/// <summary>
/// Code-Behind des Overlays: nur Slider-Mechanik für das Seeking (Beginn/Ende der Interaktion),
/// alles andere läuft über Bindings und Commands des <see cref="LightboxViewModel"/>.
/// </summary>
public partial class LightboxOverlay : UserControl
{
    /// <summary>True: die Fläche fängt Mausereignisse (Video-Modus); false: durchlässig für das ZoomPanControl.</summary>
    public static readonly DependencyProperty IsSurfaceInteractiveProperty = DependencyProperty.Register(
        nameof(IsSurfaceInteractive),
        typeof(bool),
        typeof(LightboxOverlay),
        new PropertyMetadata(false));

    private bool _seekActive;

    public LightboxOverlay()
    {
        InitializeComponent();

        SeekSlider.PreviewMouseLeftButtonDown += (_, _) => BeginSeek();
        SeekSlider.PreviewMouseLeftButtonUp += (_, _) => EndSeek();
        SeekSlider.LostMouseCapture += (_, _) => EndSeek();
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndSeek()));
    }

    public bool IsSurfaceInteractive
    {
        get => (bool)GetValue(IsSurfaceInteractiveProperty);
        set => SetValue(IsSurfaceInteractiveProperty, value);
    }

    /// <summary>Die Hintergrundfläche (für Doppelklick/Aktivierung im Video-Modus).</summary>
    public UIElement SurfaceElement => Surface;

    /// <summary>True, solange die Maus über Toolbar oder Transportleiste steht (Auto-Hide pausiert).</summary>
    public bool IsChromeHovered => Toolbar.IsMouseOver || Transport.IsMouseOver;

    private LightboxViewModel? ViewModel => DataContext as LightboxViewModel;

    private void BeginSeek()
    {
        if (_seekActive)
        {
            return;
        }

        _seekActive = true;
        ViewModel?.BeginSeek();
    }

    private void EndSeek()
    {
        if (!_seekActive)
        {
            return;
        }

        _seekActive = false;
        ViewModel?.EndSeek(SeekSlider.Value);
    }
}
