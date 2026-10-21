using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumora.Places.Map.Tiles;

namespace Lumora.Places.Map;

/// <summary>Klick/Doppelklick auf einen Marker.</summary>
public sealed class MapMarkerEventArgs(MapMarker marker) : EventArgs
{
    public MapMarker Marker { get; } = marker;
}

/// <summary>Punkt der Karte (Klick ins Leere, Rechtsklick), ggf. mit Marker darunter.</summary>
public sealed class MapPointEventArgs(double latitude, double longitude, MapMarker? marker, Point screenPoint) : EventArgs
{
    public double Latitude { get; } = latitude;

    public double Longitude { get; } = longitude;

    public MapMarker? Marker { get; } = marker;

    public Point ScreenPoint { get; } = screenPoint;
}

/// <summary>Fotos aus der Seitenleiste „Ohne Ort“ auf die Karte gezogen.</summary>
public sealed class MapDropEventArgs(IReadOnlyList<int> fileIds, double latitude, double longitude) : EventArgs
{
    public IReadOnlyList<int> FileIds { get; } = fileIds;

    public double Latitude { get; } = latitude;

    public double Longitude { get; } = longitude;
}

/// <summary>
/// Eigene, schlanke Kachelkarte (ADR-0051, statt Mapsui/WebView2): zeichnet in <see cref="OnRender"/> die
/// OSM-Kacheln des Ausschnitts (fehlende Kacheln aus gröberen Stufen hochskaliert, ohne Kacheln ein graues Raster) und
/// darüber die Marker (Gruppen rund mit Vorschaubild und Anzahl-Plakette, Einzelfotos als kleines Vorschaubild).
/// Ziehen verschiebt, Mausrad/Doppelklick/Tasten zoomen um den Mauspunkt. Marker sind keine eigenen Elemente — die Karte
/// ist im UI-Automation-Baum ein Blatt (CLAUDE.md: keine tausend Peers), Treffer prüft <see cref="MarkerAt"/>.
/// Klick auf einen Marker wird um die Doppelklickzeit verzögert gemeldet, damit ein Doppelklick ihn ersetzt.
/// </summary>
public sealed class MapControl : FrameworkElement
{
    /// <summary>Datenformat beim Ziehen aus „Ohne Ort“: <c>int[]</c> mit FileIds.</summary>
    public const string FileIdsDataFormat = "Lumora.Places.FileIds";

    public const double GroupRadius = 26;
    public const double SingleSize = 38;

    public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(
        nameof(Viewport), typeof(MapViewport), typeof(MapControl), new PropertyMetadata(null, OnViewportChanged));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<MapMarker>), typeof(MapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(
        nameof(Tiles), typeof(MapTileProvider), typeof(MapControl), new PropertyMetadata(null, OnTilesChanged));

    public static readonly DependencyProperty MapBackgroundProperty = DependencyProperty.Register(
        nameof(MapBackground), typeof(Brush), typeof(MapControl),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(MapControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(MapControl),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaceholderBrushProperty = DependencyProperty.Register(
        nameof(PlaceholderBrush), typeof(Brush), typeof(MapControl),
        new FrameworkPropertyMetadata(Brushes.Silver, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BadgeForegroundProperty = DependencyProperty.Register(
        nameof(BadgeForeground), typeof(Brush), typeof(MapControl),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gewählter Punkt (Kartenfenster „Ort wählen“) als Nadel; null = keine.</summary>
    public static readonly DependencyProperty PinProperty = DependencyProperty.Register(
        nameof(Pin), typeof(Point?), typeof(MapControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush MarkerBorder = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush Shadow = Freeze(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)));
    private static readonly Typeface BadgeFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly DispatcherTimer _clickTimer;
    private readonly DispatcherTimer _tileRetryTimer;
    private int _missingTiles;
    private readonly List<(MapMarker Marker, Point Center)> _drawn = new();
    private Point _down;
    private Point _last;
    private bool _dragging;
    private bool _pressed;
    private MapMarker? _pendingClick;
    private MapMarker? _hover;
    private Point? _dropPoint;
    private (int Zoom, int X0, int X1, int Y0, int Y1)? _wantedKey;

    public MapControl()
    {
        Focusable = true;
        AllowDrop = true;
        ClipToBounds = true;
        FocusVisualStyle = null;
        _clickTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp((int)NativeMethods.GetDoubleClickTime(), 200, 700)),
        };
        _clickTimer.Tick += OnClickTimer;

        // Fehlende Kacheln (offline, Fehler, Warteschlange von einem anderen Kartenfenster ersetzt) regelmäßig erneut anfragen
        _tileRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _tileRetryTimer.Tick += (_, _) =>
        {
            if (_missingTiles > 0 && Tiles is { State: not MapTileSourceState.Disabled })
            {
                _wantedKey = null;
                InvalidateVisual();
            }
        };
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    public MapViewport? Viewport
    {
        get => (MapViewport?)GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    public IReadOnlyList<MapMarker>? Markers
    {
        get => (IReadOnlyList<MapMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public MapTileProvider? Tiles
    {
        get => (MapTileProvider?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    public Brush MapBackground
    {
        get => (Brush)GetValue(MapBackgroundProperty);
        set => SetValue(MapBackgroundProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush PlaceholderBrush
    {
        get => (Brush)GetValue(PlaceholderBrushProperty);
        set => SetValue(PlaceholderBrushProperty, value);
    }

    /// <summary>Schrift der Anzahl-Plakette (auf <see cref="AccentBrush"/>).</summary>
    public Brush BadgeForeground
    {
        get => (Brush)GetValue(BadgeForegroundProperty);
        set => SetValue(BadgeForegroundProperty, value);
    }

    /// <summary>Nadel in Einheitskoordinaten (x, y) oder null.</summary>
    public Point? Pin
    {
        get => (Point?)GetValue(PinProperty);
        set => SetValue(PinProperty, value);
    }

    /// <summary>Anzahl gezeichneter Kacheln mit Bild beim letzten Zeichnen (Tests, Rauchtest).</summary>
    public int LastDrawnTileImages { get; private set; }

    /// <summary>Anzahl gezeichneter Marker beim letzten Zeichnen.</summary>
    public int LastDrawnMarkers => _drawn.Count;

    public event EventHandler<MapMarkerEventArgs>? MarkerClicked;

    public event EventHandler<MapMarkerEventArgs>? MarkerDoubleClicked;

    /// <summary>Klick ins Leere (kein Marker, nicht gezogen).</summary>
    public event EventHandler<MapPointEventArgs>? MapClicked;

    /// <summary>Rechtsklick (Kontextmenü der Seite).</summary>
    public event EventHandler<MapPointEventArgs>? ContextRequested;

    public event EventHandler<MapDropEventArgs>? FilesDropped;

    /// <summary>Oberster Marker unter dem Punkt (zuletzt gezeichnet = oben) oder null.</summary>
    public MapMarker? MarkerAt(Point point)
    {
        for (var i = _drawn.Count - 1; i >= 0; i--)
        {
            var (marker, center) = _drawn[i];
            if (marker.IsGroup)
            {
                var r = RadiusOf(marker) + 2;
                if ((point - center).LengthSquared <= r * r)
                {
                    return marker;
                }
            }
            else if (Math.Abs(point.X - center.X) <= (SingleSize / 2) + 2 && Math.Abs(point.Y - center.Y) <= (SingleSize / 2) + 2)
            {
                return marker;
            }
        }

        return null;
    }

    /// <summary>Bildschirmpunkt eines Markers (für Tests und den Rauchtest).</summary>
    public Point? ScreenPointOf(MapMarker marker)
    {
        if (Viewport is not { } vp)
        {
            return null;
        }

        var (x, y) = vp.UnitToScreen(marker.X, marker.Y);
        return new Point(x, y);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Viewport?.SetSize(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(MapBackground, null, new Rect(size));
        _drawn.Clear();
        if (Viewport is not { } vp || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        DrawTiles(dc, vp, size);
        DrawMarkers(dc, vp);
        if (Pin is { } pin)
        {
            var (px, py) = vp.UnitToScreen(pin.X, pin.Y);
            DrawPin(dc, new Point(px, py));
        }

        if (_dropPoint is { } drop)
        {
            DrawPin(dc, drop);
        }
    }

    // ---- Kacheln -----------------------------------------------------------------------------------------------------

    private void DrawTiles(DrawingContext dc, MapViewport vp, Size size)
    {
        var tiles = Tiles;
        var maxTileZoom = tiles?.MaxZoom ?? 19;
        var tz = (int)Math.Clamp(Math.Round(vp.Zoom), 0, maxTileZoom);
        var n = 1 << tz;
        var tilePx = vp.WorldSize / n;
        var (ox, oy) = vp.UnitToScreen(0, 0);
        var x0 = Math.Max(0, (int)Math.Floor(-ox / tilePx));
        var x1 = Math.Min(n - 1, (int)Math.Floor((size.Width - ox) / tilePx));
        var y0 = Math.Max(0, (int)Math.Floor(-oy / tilePx));
        var y1 = Math.Min(n - 1, (int)Math.Floor((size.Height - oy) / tilePx));
        var gridPen = new Pen(GridBrush, 0.5);
        gridPen.Freeze();
        var drawnImages = 0;
        var wanted = new List<MapTileId>();
        for (var ty = y0; ty <= y1; ty++)
        {
            for (var tx = x0; tx <= x1; tx++)
            {
                var id = new MapTileId(tz, tx, ty);
                var rect = new Rect(ox + (tx * tilePx), oy + (ty * tilePx), tilePx + 0.5, tilePx + 0.5);
                if (tiles?.TryGet(id) is { } image)
                {
                    dc.DrawImage(image, rect);
                    drawnImages++;
                    continue;
                }

                wanted.Add(id);
                if (tiles is not null && DrawFromParent(dc, tiles, id, rect, ox, oy, tilePx))
                {
                    drawnImages++;
                    continue;
                }

                dc.DrawRectangle(null, gridPen, rect);
            }
        }

        LastDrawnTileImages = drawnImages;
        _missingTiles = wanted.Count;
        var key = (tz, x0, x1, y0, y1);
        if (tiles is not null && _wantedKey != key)
        {
            _wantedKey = key;
            var cx = size.Width / 2;
            var cy = size.Height / 2;
            tiles.SetWanted(wanted
                .OrderBy(t => Math.Pow(ox + ((t.X + 0.5) * tilePx) - cx, 2) + Math.Pow(oy + ((t.Y + 0.5) * tilePx) - cy, 2))
                .ToList());
        }
    }

    /// <summary>Fehlende Kachel aus einer gröberen Stufe (bis 4 höher) im Speicher hochskaliert zeichnen.</summary>
    private static bool DrawFromParent(DrawingContext dc, MapTileProvider tiles, MapTileId id, Rect rect, double ox, double oy, double tilePx)
    {
        for (var k = 1; k <= 4 && id.Zoom - k >= 0; k++)
        {
            var parent = new MapTileId(id.Zoom - k, id.X >> k, id.Y >> k);
            if (tiles.TryGet(parent) is not { } image)
            {
                continue;
            }

            var span = tilePx * (1 << k);
            var parentRect = new Rect(ox + (parent.X * span), oy + (parent.Y * span), span + 0.5, span + 0.5);
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawImage(image, parentRect);
            dc.Pop();
            return true;
        }

        return false;
    }

    // ---- Marker ------------------------------------------------------------------------------------------------------

    private void DrawMarkers(DrawingContext dc, MapViewport vp)
    {
        if (Markers is not { Count: > 0 } markers)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var accentPen = new Pen(AccentBrush, 3);
        accentPen.Freeze();
        var whitePen = new Pen(MarkerBorder, 3);
        whitePen.Freeze();
        var badgeBorder = new Pen(MarkerBorder, 1.5);
        badgeBorder.Freeze();

        // Einzelfotos zuerst, dann Gruppen nach Größe — große Gruppen liegen oben
        foreach (var marker in markers.OrderBy(m => m.IsGroup ? 1 : 0).ThenBy(m => m.Count))
        {
            var (sx, sy) = vp.UnitToScreen(marker.X, marker.Y);
            if (sx < -60 || sy < -60 || sx > RenderSize.Width + 60 || sy > RenderSize.Height + 60)
            {
                continue;
            }

            var center = new Point(sx, sy);
            var hover = ReferenceEquals(marker, _hover);
            if (marker.IsGroup)
            {
                var r = RadiusOf(marker);
                dc.DrawEllipse(Shadow, null, new Point(sx + 1, sy + 2), r + 1, r + 1);
                dc.DrawEllipse(PlaceholderBrush, hover ? accentPen : whitePen, center, r, r);
                if (marker.Image is { } image)
                {
                    dc.PushClip(new EllipseGeometry(center, r - 1.5, r - 1.5));
                    dc.DrawImage(image, CoverRect(image, center, (r - 1.5) * 2, (r - 1.5) * 2));
                    dc.Pop();
                    dc.DrawEllipse(null, hover ? accentPen : whitePen, center, r, r);
                }

                var text = new FormattedText(marker.CountText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BadgeFace, 11.5, BadgeForeground, dpi);
                var w = Math.Max(20, text.Width + 10);
                var h = 18.0;
                var badge = new Rect(sx + (r * 0.45), sy - r - 2, w, h);
                dc.DrawRoundedRectangle(AccentBrush, badgeBorder, badge, h / 2, h / 2);
                dc.DrawText(text, new Point(badge.X + ((w - text.Width) / 2), badge.Y + ((h - text.Height) / 2)));
            }
            else
            {
                var half = SingleSize / 2;
                var rect = new Rect(sx - half, sy - half, SingleSize, SingleSize);
                dc.DrawRoundedRectangle(Shadow, null, new Rect(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), 6, 6);
                dc.DrawRoundedRectangle(PlaceholderBrush, null, rect, 6, 6);
                if (marker.Image is { } image)
                {
                    dc.PushClip(new RectangleGeometry(rect, 6, 6));
                    dc.DrawImage(image, CoverRect(image, center, SingleSize, SingleSize));
                    dc.Pop();
                }

                var pen = new Pen(hover ? AccentBrush : MarkerBorder, hover ? 3 : 2);
                pen.Freeze();
                dc.DrawRoundedRectangle(null, pen, rect, 6, 6);
            }

            _drawn.Add((marker, center));
        }
    }

    private static double RadiusOf(MapMarker marker) => marker.Count >= 1000 ? GroupRadius + 6 : marker.Count >= 100 ? GroupRadius + 3 : GroupRadius;

    /// <summary>Bild so groß, dass es die Fläche bedeckt (wie UniformToFill), um den Mittelpunkt.</summary>
    private static Rect CoverRect(ImageSource image, Point center, double width, double height)
    {
        var iw = Math.Max(1, image.Width);
        var ih = Math.Max(1, image.Height);
        var scale = Math.Max(width / iw, height / ih);
        var w = iw * scale;
        var h = ih * scale;
        return new Rect(center.X - (w / 2), center.Y - (h / 2), w, h);
    }

    private void DrawPin(DrawingContext dc, Point at)
    {
        var pen = new Pen(MarkerBorder, 2);
        pen.Freeze();
        dc.DrawEllipse(Shadow, null, new Point(at.X + 1, at.Y + 2), 10, 10);
        dc.DrawEllipse(AccentBrush, pen, at, 9, 9);
        dc.DrawEllipse(MarkerBorder, null, at, 3, 3);
    }

    // ---- Eingabe -----------------------------------------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var pos = e.GetPosition(this);
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            _pendingClick = null;
            var marker = MarkerAt(pos);
            if (marker is not null)
            {
                MarkerDoubleClicked?.Invoke(this, new MapMarkerEventArgs(marker));
            }
            else
            {
                Viewport?.ZoomAt(Math.Floor(Viewport.Zoom + 1e-9) + 1, pos.X, pos.Y);
            }

            e.Handled = true;
            return;
        }

        _down = pos;
        _last = pos;
        _dragging = false;
        _pressed = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        if (_pressed && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && (pos - _down).Length > 4)
            {
                _dragging = true;
                Cursor = Cursors.SizeAll;
            }

            if (_dragging)
            {
                Viewport?.PanBy(_last.X - pos.X, _last.Y - pos.Y);
            }

            _last = pos;
            return;
        }

        var hover = MarkerAt(pos);
        if (!ReferenceEquals(hover, _hover))
        {
            _hover = hover;
            Cursor = hover is null ? null : Cursors.Hand;
            ToolTip = hover is null ? null : hover.IsGroup ? $"{hover.AutomationName} — Klick: hineinzoomen · Doppelklick: in der Galerie zeigen" : "Foto — Klick: öffnen";
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        ReleaseMouseCapture();
        Cursor = _hover is null ? null : Cursors.Hand;
        if (_dragging)
        {
            _dragging = false;
            return;
        }

        var pos = e.GetPosition(this);
        var marker = MarkerAt(pos);
        if (marker is not null)
        {
            _pendingClick = marker;
            _clickTimer.Stop();
            _clickTimer.Start();
        }
        else if (Viewport is { } vp)
        {
            var (lat, lon) = vp.ScreenToGeo(pos.X, pos.Y);
            MapClicked?.Invoke(this, new MapPointEventArgs(lat, lon, null, pos));
        }

        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (Viewport is not { } vp)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var (lat, lon) = vp.ScreenToGeo(pos.X, pos.Y);
        ContextRequested?.Invoke(this, new MapPointEventArgs(lat, lon, MarkerAt(pos), pos));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Viewport is not { } vp)
        {
            return;
        }

        var pos = e.GetPosition(this);
        vp.ZoomAt(vp.Zoom + (e.Delta / 120.0), pos.X, pos.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Viewport is not { } vp)
        {
            return;
        }

        var cx = RenderSize.Width / 2;
        var cy = RenderSize.Height / 2;
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                vp.ZoomAt(Math.Floor(vp.Zoom + 1e-9) + 1, cx, cy);
                break;
            case Key.Subtract or Key.OemMinus:
                vp.ZoomAt(Math.Ceiling(vp.Zoom - 1e-9) - 1, cx, cy);
                break;
            case Key.Left:
                vp.PanBy(-120, 0);
                break;
            case Key.Right:
                vp.PanBy(120, 0);
                break;
            case Key.Up:
                vp.PanBy(0, -120);
                break;
            case Key.Down:
                vp.PanBy(0, 120);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data.GetDataPresent(FileIdsDataFormat))
        {
            e.Effects = DragDropEffects.Copy;
            _dropPoint = e.GetPosition(this);
            InvalidateVisual();
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        _dropPoint = null;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        _dropPoint = null;
        InvalidateVisual();
        if (Viewport is not { } vp || e.Data.GetData(FileIdsDataFormat) is not int[] { Length: > 0 } ids)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var (lat, lon) = vp.ScreenToGeo(pos.X, pos.Y);
        FilesDropped?.Invoke(this, new MapDropEventArgs(ids, lat, lon));
        e.Handled = true;
    }

    private void OnClickTimer(object? sender, EventArgs e)
    {
        _clickTimer.Stop();
        if (_pendingClick is { } marker)
        {
            _pendingClick = null;
            MarkerClicked?.Invoke(this, new MapMarkerEventArgs(marker));
        }
    }

    // ---- Verdrahtung -------------------------------------------------------------------------------------------------

    private bool _attached;

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _wantedKey = null; // nach dem Wiederanzeigen die sichtbaren Kacheln erneut anmelden
        _tileRetryTimer.Start();
        if (Viewport is { } vp)
        {
            vp.Changed += OnViewportModelChanged;
            vp.SetSize(ActualWidth, ActualHeight);
        }

        if (Tiles is { } tiles)
        {
            tiles.TilesChanged += OnTilesArrived;
        }

        InvalidateVisual();
    }

    private void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _clickTimer.Stop();
        _tileRetryTimer.Stop();
        if (Viewport is { } vp)
        {
            vp.Changed -= OnViewportModelChanged;
        }

        if (Tiles is { } tiles)
        {
            tiles.TilesChanged -= OnTilesArrived;
        }
    }

    private void OnViewportModelChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnTilesArrived(object? sender, EventArgs e) => InvalidateVisual();

    private static void OnViewportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (MapControl)d;
        if (!map._attached)
        {
            return;
        }

        if (e.OldValue is MapViewport old)
        {
            old.Changed -= map.OnViewportModelChanged;
        }

        if (e.NewValue is MapViewport vp)
        {
            vp.Changed += map.OnViewportModelChanged;
            vp.SetSize(map.ActualWidth, map.ActualHeight);
        }

        map.InvalidateVisual();
    }

    private static void OnTilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (MapControl)d;
        map._wantedKey = null;
        if (!map._attached)
        {
            return;
        }

        if (e.OldValue is MapTileProvider old)
        {
            old.TilesChanged -= map.OnTilesArrived;
        }

        if (e.NewValue is MapTileProvider tiles)
        {
            tiles.TilesChanged += map.OnTilesArrived;
        }

        map.InvalidateVisual();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetDoubleClickTime();
    }
}
