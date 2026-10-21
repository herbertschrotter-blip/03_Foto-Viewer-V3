using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lumora.Gallery.Controls;

/// <summary>
/// UI-virtualisierende Collage für die Galerie (ADR-0044): Items im echten Seitenverhältnis
/// (<see cref="IJustifiedLayoutItem"/>), Reihen gleich hoch und randvoll (<see cref="JustifiedRowLayout"/>), letzte Reihe
/// nicht gestreckt. Der Reihen-Umbruch wird für die ganze Seite vorab berechnet (O(n)); realisiert werden nur die
/// sichtbaren Reihen plus <see cref="BufferRows"/> Pufferreihen oben und unten. Pixelbasiertes vertikales Scrollen über
/// <see cref="IScrollInfo"/>, kein horizontales.
/// <para>
/// Realisierung, Recycling und Aufräumen folgen bewusst exakt dem <see cref="VirtualizingTilePanel"/> (0.2.42):
/// recycelte Container werden IMMER mit <c>PrepareItemContainer</c> vorbereitet, angeordnet wird über den echten
/// Item-Index des Containers (<c>IndexFromContainer</c>), und ein Container mit Tastaturfokus bleibt auch außerhalb des
/// Bereichs an SEINEM Platz erhalten. Der sichtbare Bereich wird über dasselbe Routed Event
/// (<see cref="VirtualizingTilePanel.VisibleRangeChangedEvent"/>) gemeldet, damit die Galerie Vorschaubilder für die
/// sichtbaren Items zuerst anfordert.
/// </para>
/// </summary>
public sealed class JustifiedTilePanel : VirtualizingPanel, IScrollInfo
{
    /// <summary>Pufferreihen ober- und unterhalb des sichtbaren Bereichs (die Pfeiltasten finden so die Nachbarreihe).</summary>
    public const int BufferRows = 1;

    /// <summary>Breite, mit der ohne endliche Vorgabe (Messung in einem unbegrenzten Container) gerechnet wird.</summary>
    private const double FallbackWidth = 1200.0;

    private const double LineDelta = 16.0;

    public static readonly DependencyProperty TargetRowHeightProperty = DependencyProperty.Register(
        nameof(TargetRowHeight),
        typeof(double),
        typeof(JustifiedTilePanel),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutInputChanged),
        IsValidExtent);

    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing),
        typeof(double),
        typeof(JustifiedTilePanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutInputChanged),
        value => value is double d && d >= 0 && double.IsFinite(d));

    /// <summary>
    /// Zähler aus dem ViewModel: ändert sich, wenn sich Seitenverhältnisse von Items geändert haben (Maße nachgetragen,
    /// erstes Vorschaubild ohne bekannte Maße) — dann wird der Umbruch neu berechnet.
    /// </summary>
    public static readonly DependencyProperty LayoutVersionProperty = DependencyProperty.Register(
        nameof(LayoutVersion),
        typeof(int),
        typeof(JustifiedTilePanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutInputChanged));

    private readonly JustifiedRowLayout _layout = new();
    private double[] _aspects = [];
    private bool _layoutDirty = true;
    private Size _extent;
    private Size _viewport;
    private double _verticalOffset;
    private int _firstRealizedIndex = -1;
    private int _lastRealizedIndex = -1;
    private int _reportedFirstVisible = int.MinValue;
    private int _reportedLastVisible = int.MinValue;
    private int _anchorIndex = -1;
    private double _anchorDelta;

    /// <summary>Ziel-Bildhöhe einer Reihe in DIP (Vorschaugrößen-Regler); gefüllte Reihen sind höchstens so hoch.</summary>
    public double TargetRowHeight
    {
        get => (double)GetValue(TargetRowHeightProperty);
        set => SetValue(TargetRowHeightProperty, value);
    }

    /// <summary>Zusätzliche Breite/Höhe je Zelle in DIP (Container-Rand um das Bild; ergibt die Fugen).</summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <inheritdoc cref="LayoutVersionProperty"/>
    public int LayoutVersion
    {
        get => (int)GetValue(LayoutVersionProperty);
        set => SetValue(LayoutVersionProperty, value);
    }

    /// <summary>Aktueller Reihen-Umbruch (nach dem letzten Measure); für Tests, Rauchtest und Diagnose.</summary>
    public JustifiedRowLayout Layout => _layout;

    /// <summary>Anzahl Reihen der Seite.</summary>
    public int RowCount => _layout.RowCount;

    /// <summary>Dauer der letzten Neuberechnung des Umbruchs in Millisekunden.</summary>
    public double LastLayoutMilliseconds { get; private set; }

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

    /// <summary>Mausrad wie beim Kachel-Raster: <c>WheelScrollLines</c> × 16 px je Rastung.</summary>
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

    /// <summary>Bringt die Zelle, die <paramref name="visual"/> enthält, vollständig in den Viewport (Tastatur, BringIntoView).</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = FindDirectChild(visual);
        var itemIndex = child is null ? -1 : ItemIndexOfChild(child);
        if (itemIndex < 0 || itemIndex >= _layout.ItemCount)
        {
            return Rect.Empty;
        }

        var cell = _layout.ItemRect(itemIndex);
        SetVerticalOffset(OffsetToShow(cell.Top, cell.Height));
        cell.Offset(0, -_verticalOffset);
        return cell;
    }

    // ---- Layout --------------------------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        // InternalChildren zuerst anfassen: erst dann ist der ItemContainerGenerator des Panels sicher angebunden.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count ?? 0;

        var hasFiniteWidth = !double.IsPositiveInfinity(availableSize.Width);
        var width = hasFiniteWidth ? availableSize.Width : FallbackWidth;
        EnsureLayout(itemsOwner, itemCount, width);

        var extent = new Size(width, _layout.ExtentHeight);
        var viewport = new Size(width, double.IsPositiveInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
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

        var rows = _layout.RowCount;
        int firstVisibleRow = 0, lastVisibleRow = -1;
        if (rows > 0)
        {
            firstVisibleRow = _layout.RowAt(_verticalOffset);
            lastVisibleRow = Math.Max(firstVisibleRow, _layout.RowAt(_verticalOffset + Math.Max(0, _viewport.Height - 0.5)));
        }

        var virtualizing = itemsOwner is null || GetIsVirtualizing(itemsOwner);
        var recycling = itemsOwner is not null && GetVirtualizationMode(itemsOwner) == VirtualizationMode.Recycling;
        var firstRow = virtualizing ? Math.Max(0, firstVisibleRow - BufferRows) : 0;
        var lastRow = virtualizing ? Math.Min(rows - 1, lastVisibleRow + BufferRows) : rows - 1;
        var firstIndex = rows == 0 ? 0 : _layout.RowFirst(firstRow);
        var lastIndex = rows == 0 ? -1 : _layout.RowLast(lastRow);

        if (generator is not null)
        {
            RealizeRange(generator, children, firstIndex, lastIndex);
            CleanUpOutside(generator, children, firstIndex, lastIndex, recycling);
        }

        _firstRealizedIndex = lastIndex < firstIndex ? -1 : firstIndex;
        _lastRealizedIndex = lastIndex < firstIndex ? -1 : lastIndex;

        ReportVisibleRange(
            rows == 0 ? -1 : _layout.RowFirst(firstVisibleRow),
            rows == 0 ? -1 : _layout.RowLast(lastVisibleRow));

        return new Size(
            Math.Min(extent.Width, availableSize.Width),
            Math.Min(extent.Height, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            // Platz aus dem echten Item des Containers (wie VirtualizingTilePanel, 0.2.42).
            var itemIndex = ItemIndexOfChild(children[i]);
            if (itemIndex < 0 || itemIndex >= _layout.ItemCount)
            {
                continue;
            }

            var cell = _layout.ItemRect(itemIndex);
            cell.Offset(0, -_verticalOffset);
            children[i].Arrange(cell);
        }

        return finalSize;
    }

    /// <summary><c>ListBox.ScrollIntoView</c> für ein noch nicht realisiertes Item: Reihe in den Viewport, Layout sofort nachziehen.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }

        if (_layoutDirty || index >= _layout.ItemCount)
        {
            var itemsOwner = ItemsControl.GetItemsOwner(this);
            EnsureLayout(itemsOwner, itemsOwner?.Items.Count ?? 0, _viewport.Width > 0 ? _viewport.Width : FallbackWidth);
        }

        if (index >= _layout.ItemCount)
        {
            return;
        }

        var cell = _layout.ItemRect(index);
        SetVerticalOffset(OffsetToShow(cell.Top, cell.Height));
        UpdateLayout();
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        _layoutDirty = true;
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
                break;
        }

        // Measure invalidiert das Basis-Panel selbst (ShouldItemsChangeAffectLayout).
    }

    // ---- Umbruch -------------------------------------------------------------------------------

    /// <summary>
    /// Regler, Abstand oder Seitenverhältnisse geändert: das erste sichtbare Item bleibt oben (mit seinem Versatz innerhalb
    /// der Reihe) — sonst spränge die Ansicht beim Umbrechen weit weg.
    /// </summary>
    private static void OnLayoutInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (JustifiedTilePanel)d;
        panel._layoutDirty = true;
        panel.RememberAnchor();
    }

    private void RememberAnchor()
    {
        if (_anchorIndex >= 0 || _reportedFirstVisible <= 0 || _reportedFirstVisible >= _layout.ItemCount)
        {
            return;
        }

        _anchorIndex = _reportedFirstVisible;
        var row = _layout.RowOf(_anchorIndex);
        _anchorDelta = Math.Max(0, (_verticalOffset - _layout.RowTop(row)) / Math.Max(1, _layout.RowHeight(row)));
    }

    private void EnsureLayout(ItemsControl? itemsOwner, int itemCount, double width)
    {
        if (!_layoutDirty
            && itemCount == _layout.ItemCount
            && Math.Abs(width - _layout.Width) < 0.01
            && Math.Abs(TargetRowHeight - _layout.TargetRowHeight) < 0.01
            && Math.Abs(ItemSpacing - _layout.Spacing) < 0.01)
        {
            return;
        }

        if (!_layoutDirty && itemCount == _layout.ItemCount)
        {
            RememberAnchor(); // nur die Breite hat sich geändert (Fenstergröße)
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_aspects.Length < itemCount)
        {
            _aspects = new double[Math.Max(itemCount, 64)];
        }

        var items = itemsOwner?.Items;
        for (var i = 0; i < itemCount; i++)
        {
            _aspects[i] = items![i] is IJustifiedLayoutItem item ? item.LayoutAspectRatio : JustifiedRowLayout.DefaultAspectRatio;
        }

        _layout.Compute(_aspects.AsSpan(0, itemCount), width, TargetRowHeight, ItemSpacing);
        _layoutDirty = false;
        LastLayoutMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (_anchorIndex >= 0)
        {
            if (_anchorIndex < _layout.ItemCount)
            {
                var row = _layout.RowOf(_anchorIndex);
                _verticalOffset = _layout.RowTop(row) + (_anchorDelta * _layout.RowHeight(row));
            }

            _anchorIndex = -1;
        }
    }

    // ---- Realisierung (wie VirtualizingTilePanel, 0.2.42) ------------------------------------------

    private void RealizeRange(IItemContainerGenerator generator, UIElementCollection children, int firstIndex, int lastIndex)
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

                // Recycelte Container (newlyRealized == false, noch nicht unter unseren Kindern) IMMER vorbereiten —
                // sonst zeigen ihre Bindungen weiter das alte Item (0.2.42).
                var needsPrepare = newlyRealized || !IsChildAt(children, childIndex, child) && children.IndexOf(child) < 0;
                childIndex = PlaceChild(children, childIndex, child);
                if (needsPrepare)
                {
                    generator.PrepareItemContainer(child);
                }

                child.Measure(_layout.ItemRect(itemIndex).Size);
            }
        }
    }

    private int PlaceChild(UIElementCollection children, int childIndex, UIElement child)
    {
        if (IsChildAt(children, childIndex, child))
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

    /// <summary>Entfernt bzw. recycelt alle Container außerhalb des Bereichs — bis auf den mit Tastaturfokus.</summary>
    private void CleanUpOutside(IItemContainerGenerator generator, UIElementCollection children, int firstIndex, int lastIndex, bool recycling)
    {
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var itemIndex = ItemIndexOfChild(children[i]);
            var position = itemIndex >= 0 ? generator.GeneratorPositionFromIndex(itemIndex) : default;
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            if (itemIndex >= 0 && children[i].IsKeyboardFocusWithin)
            {
                // Behaltener Fokus-Container: mit seiner Zellgröße messen, Arrange legt ihn an seinen Platz.
                if (itemIndex < _layout.ItemCount)
                {
                    children[i].Measure(_layout.ItemRect(itemIndex).Size);
                }

                continue;
            }

            if (itemIndex >= 0 && position.Offset == 0)
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

    private static bool IsValidExtent(object value) => value is double d && d > 0 && double.IsFinite(d);

    private static double WheelDelta => Math.Max(1, SystemParameters.WheelScrollLines) * LineDelta;

    private double ClampOffset(double offset)
    {
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        return double.IsNaN(offset) ? 0 : Math.Min(Math.Max(0, offset), max);
    }

    /// <summary>Kleinste Offset-Änderung, mit der die Reihe ab <paramref name="top"/> vollständig im Viewport liegt.</summary>
    private double OffsetToShow(double top, double height)
    {
        if (top < _verticalOffset)
        {
            return top;
        }

        var bottom = top + height;
        if (bottom > _verticalOffset + _viewport.Height)
        {
            return Math.Max(top, bottom - _viewport.Height);
        }

        return _verticalOffset;
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

    private static bool IsChildAt(UIElementCollection children, int childIndex, UIElement child)
        => childIndex >= 0 && childIndex < children.Count && ReferenceEquals(children[childIndex], child);

    /// <summary>Item-Index des Containers laut Generator; -1 für Container ohne Item (Recycling-Warteschlange).</summary>
    private int ItemIndexOfChild(UIElement child)
    {
        var generator = ItemContainerGenerator;
        if (generator is ItemContainerGenerator concrete)
        {
            return concrete.IndexFromContainer(child);
        }

        var childIndex = InternalChildren.IndexOf(child);
        return generator is null || childIndex < 0 ? -1 : generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
    }

    private void ReportVisibleRange(int firstVisible, int lastVisible)
    {
        if (firstVisible == _reportedFirstVisible && lastVisible == _reportedLastVisible)
        {
            return;
        }

        _reportedFirstVisible = firstVisible;
        _reportedLastVisible = lastVisible;
        RaiseEvent(new VisibleRangeChangedEventArgs(VirtualizingTilePanel.VisibleRangeChangedEvent, this, firstVisible, lastVisible));
    }
}
