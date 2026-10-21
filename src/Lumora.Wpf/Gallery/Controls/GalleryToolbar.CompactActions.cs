using System.Windows;

namespace Lumora.Gallery.Controls;

/// <summary>
/// Sortier- und Ansichtsleiste bleibt bei 1280 DIP einzeilig (ADR-0041), auch mit dem dritten Ansicht-Eintrag „Collage“
/// (0.2.46, ADR-0044): reicht die Breite nicht, zeigen „Alle auswählen“ und „Rückgängig“ nur ihr Symbol (Tooltip und
/// Automation-Name bleiben). Erst wenn die Leiste mit Beschriftung wieder sicher passt, kommen die Texte zurück
/// (Hysterese, damit das Umschalten keine Layout-Schleife auslöst).
/// </summary>
public partial class GalleryToolbar
{
    private const double LabelHysteresis = 8;
    private double _actionLabelsWidth;

    private void OnViewRowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateActionLabels();

    private void UpdateActionLabels()
    {
        var available = ViewRow.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        var visible = 0;
        var needed = 0.0;
        foreach (UIElement child in ViewRow.Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            visible++;
            if (!ToolbarFlowPanel.GetFill(child))
            {
                needed += child.DesiredSize.Width;
            }
        }

        needed += ViewRow.Spacing * Math.Max(0, visible - 1);
        var labelsShown = SelectAllLabel.Visibility == Visibility.Visible;
        if (labelsShown)
        {
            if (needed > available + 0.5)
            {
                _actionLabelsWidth = LabelWidth(SelectAllLabel) + LabelWidth(UndoLabel) + LabelWidth(SlideshowLabel); // Diashow: ADR-0059
                SetActionLabels(Visibility.Collapsed);
            }
        }
        else if (needed + _actionLabelsWidth + LabelHysteresis <= available)
        {
            SetActionLabels(Visibility.Visible);
        }
    }

    private static double LabelWidth(FrameworkElement label) => label.DesiredSize.Width;

    private void SetActionLabels(Visibility visibility)
    {
        SelectAllLabel.Visibility = visibility;
        UndoLabel.Visibility = visibility;
        SlideshowLabel.Visibility = visibility; // Diashow: ADR-0059
    }
}
