using System;
using System.Windows;
using System.Windows.Media;

namespace Lumora.Stories;

/// <summary>
/// Bild der Story-Seite: füllt die Fläche (wie <c>UniformToFill</c>, mittig beschnitten) mit abgerundeten Ecken. Ein
/// eigenes Element statt <c>ImageBrush</c> in einem <c>Border</c>: gebundene Pinsel in virtualisierten Vorlagen melden beim
/// Abbau der Zeile „Cannot find governing FrameworkElement“ (Binding-Fehler), und ein <c>Image</c> ließe sich nur mit
/// mitwachsendem Clip runden. Zeichnet nur, wenn ein Bild da ist.
/// </summary>
public sealed class StoryImage : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(StoryImage), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(StoryImage), new FrameworkPropertyMetadata(default(CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (Source is not { } source || width <= 0 || height <= 0 || source.Width <= 0 || source.Height <= 0)
        {
            return;
        }

        var target = Fill(source.Width, source.Height, width, height);
        drawingContext.PushClip(Rounded(width, height, CornerRadius));
        drawingContext.DrawImage(source, target);
        drawingContext.Pop();
    }

    /// <summary>Zielrechteck „UniformToFill“: so groß, dass die Fläche bedeckt ist, mittig.</summary>
    internal static Rect Fill(double sourceWidth, double sourceHeight, double width, double height)
    {
        var scale = Math.Max(width / sourceWidth, height / sourceHeight);
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        return new Rect((width - w) / 2, (height - h) / 2, w, h);
    }

    private static Geometry Rounded(double width, double height, CornerRadius r)
    {
        var max = Math.Min(width, height) / 2;
        double tl = Math.Min(r.TopLeft, max), tr = Math.Min(r.TopRight, max), br = Math.Min(r.BottomRight, max), bl = Math.Min(r.BottomLeft, max);
        if (tl <= 0 && tr <= 0 && br <= 0 && bl <= 0)
        {
            return new RectangleGeometry(new Rect(0, 0, width, height));
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(tl, 0), isFilled: true, isClosed: true);
            g.LineTo(new Point(width - tr, 0), true, false);
            g.ArcTo(new Point(width, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(width, height - br), true, false);
            g.ArcTo(new Point(width - br, height), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(bl, height), true, false);
            g.ArcTo(new Point(0, height - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(0, tl), true, false);
            g.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
