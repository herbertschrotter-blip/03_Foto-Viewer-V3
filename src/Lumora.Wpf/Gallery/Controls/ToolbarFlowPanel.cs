using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Lumora.Gallery.Controls;

/// <summary>
/// Kopfzeilen-Panel der Galerie: legt die Elemente nebeneinander und bricht in eine neue Zeile um, wenn der Platz
/// nicht reicht (wie ein WrapPanel). Elemente mit <see cref="FillProperty"/> = true (Suchfeld, Abstandhalter)
/// belegen zum Umbrechen nur ihre <c>MinWidth</c> und bekommen danach den freien Rest ihrer Zeile — höchstens bis zu
/// ihrer <c>MaxWidth</c>. Bleibt dann noch Platz, steht ein Füll-Element am Zeilenende (rechtsbündig), es sei denn, es
/// beginnt die Zeile (dann linksbündig). Alle Elemente werden in ihrer Zeile vertikal zentriert.
/// </summary>
public sealed class ToolbarFlowPanel : Panel
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill",
        typeof(bool),
        typeof(ToolbarFlowPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(ToolbarFlowPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing),
        typeof(double),
        typeof(ToolbarFlowPanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private List<Line> _lines = [];

    /// <summary>Waagrechter Abstand zwischen zwei Elementen einer Zeile.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Senkrechter Abstand zwischen zwei Zeilen.</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>Anzahl der Zeilen im letzten Layout-Durchlauf (für Tests und Diagnose).</summary>
    public int LineCount => _lines.Count;

    public static bool GetFill(DependencyObject element) => (bool)element.GetValue(FillProperty);

    public static void SetFill(DependencyObject element, bool value) => element.SetValue(FillProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        foreach (UIElement child in InternalChildren)
        {
            if (!IsFillChild(child))
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            }
        }

        _lines = BuildLines(width);
        var usedWidth = 0.0;
        var height = 0.0;
        foreach (var line in _lines)
        {
            foreach (var item in line.Items)
            {
                if (item.IsFill)
                {
                    item.Element.Measure(new Size(item.Width, availableSize.Height));
                }
            }

            line.Height = 0;
            foreach (var item in line.Items)
            {
                line.Height = Math.Max(line.Height, item.Element.DesiredSize.Height);
            }

            usedWidth = Math.Max(usedWidth, line.UsedWidth);
            height += line.Height;
        }

        if (_lines.Count > 1)
        {
            height += LineSpacing * (_lines.Count - 1);
        }

        // Mit Füll-Elementen und endlicher Breite beansprucht das Panel die ganze Breite.
        var desiredWidth = double.IsInfinity(width) || !HasFillChild() ? usedWidth : Math.Max(usedWidth, width);
        return new Size(desiredWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _lines = BuildLines(finalSize.Width);
        var top = 0.0;
        foreach (var line in _lines)
        {
            line.Height = 0;
            foreach (var item in line.Items)
            {
                line.Height = Math.Max(line.Height, item.Element.DesiredSize.Height);
            }

            var x = 0.0;
            var first = true;
            foreach (var item in line.Items)
            {
                if (!first)
                {
                    x += Spacing;
                }

                first = false;
                x += item.LeadingGap;
                var childHeight = Math.Min(item.Element.DesiredSize.Height, line.Height);
                var y = top + ((line.Height - childHeight) / 2);
                item.Element.Arrange(new Rect(x, y, item.Width, childHeight));
                x += item.Width;
            }

            top += line.Height + LineSpacing;
        }

        return finalSize;
    }

    private bool HasFillChild()
    {
        foreach (UIElement child in InternalChildren)
        {
            if (IsFillChild(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFillChild(UIElement child) => child.Visibility != Visibility.Collapsed && GetFill(child);

    /// <summary>Zeilen aufbauen (gierig von links nach rechts) und den Restplatz jeder Zeile auf ihre Füll-Elemente verteilen.</summary>
    private List<Line> BuildLines(double width)
    {
        var lines = new List<Line>();
        var current = new Line();
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var isFill = GetFill(child);
            var baseWidth = isFill ? MinWidthOf(child) : child.DesiredSize.Width;
            var needed = current.Items.Count == 0 ? baseWidth : current.UsedWidth + Spacing + baseWidth;
            if (current.Items.Count > 0 && needed > width + 0.5)
            {
                lines.Add(current);
                current = new Line();
                needed = baseWidth;
            }

            current.Items.Add(new LineItem(child, isFill, baseWidth));
            current.UsedWidth = needed;
        }

        if (current.Items.Count > 0)
        {
            lines.Add(current);
        }

        if (!double.IsInfinity(width))
        {
            foreach (var line in lines)
            {
                DistributeRest(line, width);
            }
        }

        return lines;
    }

    private static void DistributeRest(Line line, double width)
    {
        var rest = width - line.UsedWidth;
        if (rest <= 0)
        {
            return;
        }

        // Rest gleichmäßig auf die Füll-Elemente verteilen, MaxWidth beachten; was übrig bleibt, wandert weiter.
        var open = new List<LineItem>();
        foreach (var item in line.Items)
        {
            if (item.IsFill)
            {
                open.Add(item);
            }
        }

        while (rest > 0.5 && open.Count > 0)
        {
            var share = rest / open.Count;
            var given = 0.0;
            for (var i = open.Count - 1; i >= 0; i--)
            {
                var item = open[i];
                var max = item.Element is FrameworkElement fe ? fe.MaxWidth : double.PositiveInfinity;
                var grow = Math.Min(share, Math.Max(0, max - item.Width));
                item.Width += grow;
                given += grow;
                if (item.Width >= max - 0.5)
                {
                    open.RemoveAt(i);
                }
            }

            rest -= given;
            if (given <= 0.5)
            {
                break;
            }
        }

        line.UsedWidth = width - Math.Max(0, rest);
        if (rest <= 0.5)
        {
            return;
        }

        // Füll-Elemente an ihrer MaxWidth: das erste Füll-Element, das nicht am Zeilenanfang steht, wird mit allem
        // dahinter rechtsbündig geschoben; sonst bleibt die Zeile linksbündig.
        for (var i = 1; i < line.Items.Count; i++)
        {
            if (line.Items[i].IsFill)
            {
                line.Items[i].LeadingGap = rest;
                line.UsedWidth = width;
                return;
            }
        }
    }

    private static double MinWidthOf(UIElement child)
        => child is FrameworkElement { MinWidth: > 0 and var min } && !double.IsInfinity(min) ? min : 0;

    private sealed class Line
    {
        public List<LineItem> Items { get; } = [];

        public double UsedWidth { get; set; }

        public double Height { get; set; }
    }

    private sealed class LineItem(UIElement element, bool isFill, double width)
    {
        public UIElement Element { get; } = element;

        public bool IsFill { get; } = isFill;

        public double Width { get; set; } = width;

        public double LeadingGap { get; set; }
    }
}
