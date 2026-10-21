using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumora.Slideshow;

/// <summary>
/// Code-Behind des Overlays: nur der Fortschrittsbalken (Animation über die Anzeigedauer, anhalten/fortsetzen) — alles
/// andere läuft über Bindings und Commands des <see cref="SlideshowViewModel"/>.
/// </summary>
public partial class SlideshowOverlay : UserControl
{
    public SlideshowOverlay()
    {
        InitializeComponent();
    }

    /// <summary>True, solange die Maus über der Bedienleiste steht (Ausblenden wartet).</summary>
    public bool IsBarHovered => Bar.IsMouseOver;

    /// <summary>Hintergrundfläche (Mausbewegung im Video-Modus).</summary>
    public UIElement SurfaceElement => Surface;

    /// <summary>Balken von vorn über <paramref name="duration"/> füllen; <paramref name="paused"/> = bei 0 stehen bleiben.</summary>
    public void StartProgress(TimeSpan duration, bool paused)
    {
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = 0;
        if (!paused && duration > TimeSpan.Zero)
        {
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, duration));
        }
    }

    /// <summary>Balken an der aktuellen Stelle anhalten.</summary>
    public void PauseProgress()
    {
        var current = ProgressScale.ScaleX;
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = current;
    }

    /// <summary>Balken von der aktuellen Stelle in <paramref name="remaining"/> zu Ende füllen.</summary>
    public void ResumeProgress(TimeSpan remaining)
    {
        var current = ProgressScale.ScaleX;
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = current;
        if (remaining > TimeSpan.Zero)
        {
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(current, 1, remaining));
        }
    }
}
