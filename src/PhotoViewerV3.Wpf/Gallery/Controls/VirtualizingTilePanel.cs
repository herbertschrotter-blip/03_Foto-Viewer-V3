using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>Sichtbarer Index-Bereich (inklusive, bezogen auf die ItemsSource), gemeldet vom <see cref="VirtualizingTilePanel"/>.</summary>
public sealed class VisibleRangeChangedEventArgs : RoutedEventArgs
{
    public VisibleRangeChangedEventArgs(RoutedEvent routedEvent, object source, int firstIndex, int lastIndex)
        : base(routedEvent, source)
    {
        FirstIndex = firstIndex;
        LastIndex = lastIndex;
    }

    /// <summary>Erster sichtbarer Index; -1, wenn nichts sichtbar ist.</summary>
    public int FirstIndex { get; }

    /// <summary>Letzter sichtbarer Index (inklusive); -1, wenn nichts sichtbar ist.</summary>
    public int LastIndex { get; }
}

public delegate void VisibleRangeChangedEventHandler(object sender, VisibleRangeChangedEventArgs e);

/// <summary>
/// UI-virtualisierende Kachelanordnung für das Galerie-Grid: feste Kachelgröße (<see cref="ItemWidth"/> ×
/// <see cref="ItemHeight"/>), Spaltenzahl aus der Viewport-Breite, pixelbasiertes vertikales Scrollen über
/// <see cref="IScrollInfo"/>. Es existieren nur Container für die sichtbaren Zeilen plus eine Pufferzeile oben und
/// unten (<see cref="BufferRows"/>); alle anderen werden über den <see cref="ItemContainerGenerator"/> entfernt bzw.
/// — bei <c>VirtualizationMode="Recycling"</c> — wiederverwendet. Kein horizontales Scrollen.
/// Hintergrund (CHANGELOG 0.2.1): mit einem nicht virtualisierenden WrapPanel existierten alle 500–1000 Kacheln einer
/// Seite als Steuerelemente; Erstaufbau im Debug-Build mehrere Sekunden, großer UI-Automation-Baum.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    /// <summary>Pufferzeilen ober- und unterhalb des sichtbaren Bereichs, die zusätzlich realisiert bleiben.</summary>
    public const int BufferRows = 1;

    /// <summary>Scrollweite einer „Zeile“ (Scrollbar-Pfeil) in Pixeln — wie der WPF-ScrollViewer bei Pixel-Scrolling.</summary>
    private const double LineDelta = 16.0;

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnItemExtentChanged),
        IsValidItemExtent);

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnItemExtentChanged),
        IsValidItemExtent);

    /// <summary>
    /// Gebubbeltes Ereignis: der sichtbare Index-Bereich hat sich geändert (Scrollen, Größenänderung, neue Items).
    /// Wird synchron aus dem Measure-Durchlauf gemeldet — Handler dürfen das Layout nicht invalidieren.
    /// </summary>
    public static readonly RoutedEvent VisibleRangeChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(VisibleRangeChanged),
        RoutingStrategy.Bubble,
        typeof(VisibleRangeChangedEventHandler),
        typeof(VirtualizingTilePanel));

    private Size _extent;
    private Size _viewport;
    private double _verticalOffset;
    private int _columns = 1;
    private int _firstRealizedIndex = -1;
    private int _lastRealizedIndex = -1;
    private int _reportedFirstVisible = int.MinValue;
    private int _anchorIndex = -1;
    private int _reportedLastVisible = int.MinValue;

    /// <summary>
    /// Kachelgröße geändert (Vorschaugröße, 0.2.19): die erste sichtbare Kachel bleibt oben — sonst spränge der Pixel-Offset
    /// bei kleineren Kacheln weit nach unten bzw. bei größeren nach oben.
    /// </summary>
    private static void OnItemExtentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (VirtualizingTilePanel)d;
        if (panel._reportedFirstVisible > 0)
        {
            panel._anchorIndex = panel._reportedFirstVisible;
        }
    }

    public static void AddVisibleRangeChangedHandler(DependencyObject d, VisibleRangeChangedEventHandler handler)
        => (d as UIElement)?.AddHandler(VisibleRangeChangedEvent, handler);

    public static void RemoveVisibleRangeChangedHandler(DependencyObject d, VisibleRangeChangedEventHandler handler)
        => (d as UIElement)?.RemoveHandler(VisibleRangeChangedEvent, handler);

    public event VisibleRangeChangedEventHandler VisibleRangeChanged
    {
        add => AddHandler(VisibleRangeChangedEvent, value);
        remove => RemoveHandler(VisibleRangeChangedEvent, value);
    }

    /// <summary>Breite einer Kachel in DIPs (inklusive Rand; die Kachelvorlage füllt die Zelle).</summary>
    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>Höhe einer Kachel in DIPs.</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>Aktuelle Spaltenzahl (aus der Viewport-Breite; mindestens 1).</summary>
    public int Columns => _columns;

    /// <summary>Erster realisierter Item-Index (inklusive Puffer); -1 ohne Items.</summary>
    public int FirstRealizedIndex => _firstRealizedIndex;

    /// <summary>Letzter realisierter Item-Index (inklusive Puffer); -1 ohne Items.</summary>
    public int LastRealizedIndex => _lastRealizedIndex;

    /// <summary>Anzahl der aktuell realisierten Container (Kinder des Panels).</summary>
    public int RealizedCount => InternalChildren.Count;

    // ---- IScrollInfo ---------------------------------------------------------------------------

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _verticalOffset;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_verticalOffset - LineDelta);

    public void LineDown() => SetVerticalOffset(_verticalOffset + LineDelta);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    /// <summary>Mausrad: wie der ScrollViewer bei Pixel-Scrolling <c>WheelScrollLines</c> Zeilen à 16 px je Rastung (Standard 3 → 48 px).</summary>
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - WheelDelta);

    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + WheelDelta);

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = ClampOffset(offset);
        if (Math.Abs(clamped - _verticalOffset) < 0.01)
        {
            return;
        }

        _verticalOffset = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    /// <summary>
    /// Bringt die Kachel, die <paramref name="visual"/> enthält, vollständig in den Viewport (Tastaturnavigation,
    /// <c>BringIntoView</c>, <c>ScrollIntoView</c> bei realisiertem Container). Liefert das Kachel-Rechteck in
    /// Viewport-Koordinaten, Rect.Empty, wenn das Visual nicht zu diesem Panel gehört.
    /// </summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = FindDirectChild(visual);
        if (child is null)
        {
            return Rect.Empty;
        }

        var itemIndex = ItemIndexOfChild(child);
        if (itemIndex < 0)
        {
            return Rect.Empty;
        }

        var (row, column) = RowAndColumn(itemIndex);
        var top = row * ItemHeight;
        SetVerticalOffset(OffsetToShowRow(top));
        return new Rect(column * ItemWidth, top - _verticalOffset, ItemWidth, ItemHeight);
    }

    // ---- Layout --------------------------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        // InternalChildren zuerst anfassen: erst dann ist der ItemContainerGenerator des Panels sicher angebunden.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count ?? 0;
        var itemWidth = ItemWidth;
        var itemHeight = ItemHeight;

        var hasFiniteWidth = !double.IsPositiveInfinity(availableSize.Width);
        _columns = hasFiniteWidth
            ? Math.Max(1, (int)Math.Floor(availableSize.Width / itemWidth + 1e-6))
            : Math.Max(1, itemCount);
        var rows = itemCount == 0 ? 0 : (itemCount + _columns - 1) / _columns;
        if (_anchorIndex >= 0)
        {
            _verticalOffset = _anchorIndex < itemCount ? (_anchorIndex / _columns) * itemHeight : _verticalOffset;
            _anchorIndex = -1;
        }

        var extent = new Size(hasFiniteWidth ? availableSize.Width : _columns * itemWidth, rows * itemHeight);
        var viewport = new Size(extent.Width, double.IsPositiveInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
        var scrollInfoChanged = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;

        var clamped = ClampOffset(_verticalOffset);
        if (Math.Abs(clamped - _verticalOffset) > 0.01)
        {
            _verticalOffset = clamped;
            scrollInfoChanged = true;
        }

        if (scrollInfoChanged)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }

        // Sichtbare Zeilen (teilweise sichtbare zählen mit) plus Pufferzeilen.
        int firstVisibleRow = 0, lastVisibleRow = -1;
        if (rows > 0)
        {
            firstVisibleRow = Math.Min(rows - 1, (int)Math.Floor(_verticalOffset / itemHeight));
            lastVisibleRow = Math.Min(rows - 1, (int)Math.Ceiling((_verticalOffset + _viewport.Height) / itemHeight) - 1);
            lastVisibleRow = Math.Max(lastVisibleRow, firstVisibleRow);
        }

        var virtualizing = itemsOwner is null || GetIsVirtualizing(itemsOwner);
        var recycling = itemsOwner is not null && GetVirtualizationMode(itemsOwner) == VirtualizationMode.Recycling;
        var firstRow = virtualizing ? Math.Max(0, firstVisibleRow - BufferRows) : 0;
        var lastRow = virtualizing ? Math.Min(rows - 1, lastVisibleRow + BufferRows) : rows - 1;
        var firstIndex = rows == 0 ? 0 : firstRow * _columns;
        var lastIndex = rows == 0 ? -1 : Math.Min(itemCount - 1, (lastRow + 1) * _columns - 1);

        if (generator is not null)
        {
            RealizeRange(generator, children, firstIndex, lastIndex, new Size(itemWidth, itemHeight));
            CleanUpOutside(generator, children, firstIndex, lastIndex, recycling);
        }

        _firstRealizedIndex = lastIndex < firstIndex ? -1 : firstIndex;
        _lastRealizedIndex = lastIndex < firstIndex ? -1 : lastIndex;

        var firstVisible = rows == 0 ? -1 : firstVisibleRow * _columns;
        var lastVisible = rows == 0 ? -1 : Math.Min(itemCount - 1, (lastVisibleRow + 1) * _columns - 1);
        ReportVisibleRange(firstVisible, lastVisible);

        return new Size(
            Math.Min(extent.Width, availableSize.Width),
            Math.Min(extent.Height, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var itemWidth = ItemWidth;
        var itemHeight = ItemHeight;
        for (var i = 0; i < children.Count; i++)
        {
            var itemIndex = ItemIndexOfChildAt(i);
            if (itemIndex < 0)
            {
                continue;
            }

            var (row, column) = RowAndColumn(itemIndex);
            children[i].Arrange(new Rect(column * itemWidth, row * itemHeight - _verticalOffset, itemWidth, itemHeight));
        }

        return finalSize;
    }

    /// <summary>
    /// <c>ListBox.ScrollIntoView</c> für ein noch nicht realisiertes Item: Offset so setzen, dass die Zeile des Index im
    /// Viewport liegt, und das Layout sofort nachziehen, damit der Aufrufer den Container direkt findet.
    /// </summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }

        var (row, _) = RowAndColumn(index);
        SetVerticalOffset(OffsetToShowRow(row * ItemHeight));
        UpdateLayout();
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
                RemoveChildRange(args.Position, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveChildRange(args.OldPosition, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Replace:
                ReplaceChildRange(args.Position, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                _verticalOffset = ClampOffset(_verticalOffset);
                break;
        }

        // Measure invalidiert das Basis-Panel selbst (ShouldItemsChangeAffectLayout).
    }

    // ---- Realisierung ----------------------------------------------------------------------------

    private void RealizeRange(IItemContainerGenerator generator, UIElementCollection children, int firstIndex, int lastIndex, Size itemSize)
    {
        if (lastIndex < firstIndex)
        {
            return;
        }

        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child)
                {
                    break;
                }

                childIndex = PlaceChild(children, childIndex, child);
                if (newlyRealized)
                {
                    generator.PrepareItemContainer(child);
                }

                child.Measure(itemSize);
            }
        }
    }

    /// <summary>Stellt sicher, dass <paramref name="child"/> an Position <paramref name="childIndex"/> der Kinder liegt; liefert die tatsächliche Position.</summary>
    private int PlaceChild(UIElementCollection children, int childIndex, UIElement child)
    {
        if (childIndex < children.Count && ReferenceEquals(children[childIndex], child))
        {
            return childIndex;
        }

        var existing = children.IndexOf(child);
        if (existing >= 0)
        {
            RemoveInternalChildRange(existing, 1);
            if (existing < childIndex)
            {
                childIndex--;
            }
        }

        if (childIndex >= children.Count)
        {
            AddInternalChild(child);
            return children.Count - 1;
        }

        InsertInternalChild(childIndex, child);
        return childIndex;
    }

    /// <summary>Entfernt (bzw. recycelt) alle Container außerhalb des Bereichs — bis auf den mit Tastaturfokus (sonst verliert die Navigation ihren Ausgangspunkt).</summary>
    private void CleanUpOutside(IItemContainerGenerator generator, UIElementCollection children, int firstIndex, int lastIndex, bool recycling)
    {
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            if (itemIndex >= 0 && children[i].IsKeyboardFocusWithin)
            {
                continue;
            }

            if (itemIndex >= 0)
            {
                if (recycling && generator is IRecyclingItemContainerGenerator recycler)
                {
                    recycler.Recycle(position, 1);
                }
                else
                {
                    generator.Remove(position, 1);
                }
            }

            RemoveInternalChildRange(i, 1);
        }
    }

    private void RemoveChildRange(GeneratorPosition position, int uiCount)
    {
        if (uiCount <= 0)
        {
            return;
        }

        var index = position.Offset > 0 ? position.Index + 1 : position.Index;
        var children = InternalChildren;
        if (index < 0 || index >= children.Count)
        {
            return;
        }

        RemoveInternalChildRange(index, Math.Min(uiCount, children.Count - index));
    }

    /// <summary>Replace eines realisierten Items: der Generator hat den (neuen oder wiederverwendeten) Container bereits an der Position — ins Panel übernehmen und vorbereiten.</summary>
    private void ReplaceChildRange(GeneratorPosition position, int uiCount)
    {
        if (uiCount <= 0)
        {
            return;
        }

        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        var index = position.Offset > 0 ? position.Index + 1 : position.Index;
        if (generator is null || index < 0 || index >= children.Count)
        {
            return;
        }

        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            for (var i = 0; i < uiCount && index + i < children.Count; i++)
            {
                if (generator.GenerateNext(out _) is not UIElement child)
                {
                    break;
                }

                if (!ReferenceEquals(children[index + i], child))
                {
                    RemoveInternalChildRange(index + i, 1);
                    InsertInternalChild(index + i, child);
                }

                generator.PrepareItemContainer(child);
            }
        }
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    private static bool IsValidItemExtent(object value) => value is double d && d > 0 && !double.IsInfinity(d) && !double.IsNaN(d);

    private static double WheelDelta => Math.Max(1, SystemParameters.WheelScrollLines) * LineDelta;

    private double ClampOffset(double offset)
    {
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        if (double.IsNaN(offset))
        {
            return 0;
        }

        return Math.Min(Math.Max(0, offset), max);
    }

    /// <summary>Kleinste Offset-Änderung, mit der die Zeile ab <paramref name="rowTop"/> vollständig im Viewport liegt.</summary>
    private double OffsetToShowRow(double rowTop)
    {
        var rowBottom = rowTop + ItemHeight;
        if (rowTop < _verticalOffset)
        {
            return rowTop;
        }

        if (rowBottom > _verticalOffset + _viewport.Height)
        {
            return Math.Max(rowTop, rowBottom - _viewport.Height);
        }

        return _verticalOffset;
    }

    private (int Row, int Column) RowAndColumn(int itemIndex)
    {
        var columns = Math.Max(1, _columns);
        return (itemIndex / columns, itemIndex % columns);
    }

    private UIElement? FindDirectChild(Visual? visual)
    {
        DependencyObject? current = visual;
        while (current is not null)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (ReferenceEquals(parent, this))
            {
                return current as UIElement;
            }

            current = parent;
        }

        return null;
    }

    private int ItemIndexOfChild(UIElement child)
    {
        var childIndex = InternalChildren.IndexOf(child);
        return childIndex < 0 ? -1 : ItemIndexOfChildAt(childIndex);
    }

    private int ItemIndexOfChildAt(int childIndex)
    {
        var generator = ItemContainerGenerator;
        return generator is null ? -1 : generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
    }

    private void ReportVisibleRange(int firstVisible, int lastVisible)
    {
        if (firstVisible == _reportedFirstVisible && lastVisible == _reportedLastVisible)
        {
            return;
        }

        _reportedFirstVisible = firstVisible;
        _reportedLastVisible = lastVisible;
        RaiseEvent(new VisibleRangeChangedEventArgs(VisibleRangeChangedEvent, this, firstVisible, lastVisible));
    }
}
