using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoViewerV3.Lightbox.Controls;

/// <summary>
/// Bildanzeige mit Zoom/Pan: Mausrad-Zoom am Cursor, Drag-Pan, Doppelklick Fit ↔ 100 %.
/// „100 %“ bedeutet ein Bildpixel = ein Gerätepixel (DPI-korrigiert). „Fit“ passt ein, skaliert
/// aber nicht über 100 % hoch. Das Bild wird mit expliziter Pixelgröße gerendert, sodass DPI-Metadaten
/// der Datei (72/300 dpi) keine Rolle spielen. Reiner View-Zustand — kein Fachcode.
/// </summary>
public sealed class ZoomPanControl : Grid, IZoomPanTarget
{
    private const double StepFactor = 1.25;
    private const double MaxZoomOverActual = 16.0;
    private const double MinZoomUnderFit = 0.5;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(BitmapSource),
        typeof(ZoomPanControl),
        new PropertyMetadata(null, static (d, e) => ((ZoomPanControl)d).OnSourceChanged(e.NewValue as BitmapSource)));

    /// <summary>Aktueller Zoom in Prozent (100 = ein Bildpixel pro Gerätepixel). Wird vom Control gesetzt.</summary>
    public static readonly DependencyProperty ZoomPercentProperty = DependencyProperty.Register(
        nameof(ZoomPercent),
        typeof(double),
        typeof(ZoomPanControl),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>True, solange der Fit-Modus aktiv ist (passt sich bei Größenänderung an).</summary>
    public static readonly DependencyProperty IsFitProperty = DependencyProperty.Register(
        nameof(IsFit),
        typeof(bool),
        typeof(ZoomPanControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private readonly Image _image;
    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _translateTransform = new(0, 0);

    private double _scale = 1;          // DIP pro Bildpixel
    private double _translateX;
    private double _translateY;
    private double _dpiScale = 1;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _isFit = true;

    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartTranslateX;
    private double _dragStartTranslateY;

    public ZoomPanControl()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // Hit-Test für Mausrad/Drag auf der ganzen Fläche
        Focusable = false;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0, 0),
            RenderTransform = new TransformGroup
            {
                Children = { _scaleTransform, _translateTransform },
            },
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Children.Add(_image);

        Loaded += (_, _) => { UpdateDpi(); Relayout(); };
        SizeChanged += (_, _) => Relayout();
        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        LostMouseCapture += (_, _) => EndDrag();
    }

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double ZoomPercent
    {
        get => (double)GetValue(ZoomPercentProperty);
        set => SetValue(ZoomPercentProperty, value);
    }

    public bool IsFit
    {
        get => (bool)GetValue(IsFitProperty);
        set => SetValue(IsFitProperty, value);
    }

    private bool HasImage => _pixelWidth > 0 && _pixelHeight > 0;

    private double ActualSizeScale => 1.0 / _dpiScale;

    private double FitScale
    {
        get
        {
            if (!HasImage || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return ActualSizeScale;
            }

            var fit = Math.Min(ActualWidth / _pixelWidth, ActualHeight / _pixelHeight);
            return Math.Min(fit, ActualSizeScale);
        }
    }

    private double MinScale => Math.Min(FitScale, ActualSizeScale) * MinZoomUnderFit;

    private double MaxScale => ActualSizeScale * MaxZoomOverActual;

    // ----- IZoomPanTarget -----

    public void ZoomIn() => ZoomAt(ViewportCenter, _scale * StepFactor);

    public void ZoomOut() => ZoomAt(ViewportCenter, _scale / StepFactor);

    public void ZoomToFit()
    {
        _isFit = true;
        Relayout();
    }

    public void ZoomToActualSize() => ZoomAt(ViewportCenter, ActualSizeScale);

    /// <summary>Doppelklick-Logik: bei 100 % zurück auf Fit, sonst 100 % am angegebenen Punkt.</summary>
    public void ToggleFitAndActual(Point viewportPoint)
    {
        if (Math.Abs(_scale - ActualSizeScale) < 1e-6)
        {
            ZoomToFit();
        }
        else
        {
            ZoomAt(viewportPoint, ActualSizeScale);
        }
    }

    // ----- Eingaben -----

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasImage)
        {
            return;
        }

        var notches = e.Delta / 120.0;
        var factor = Math.Pow(StepFactor, notches);
        ZoomAt(e.GetPosition(this), _scale * factor);
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasImage)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            EndDrag();
            ToggleFitAndActual(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartTranslateX = _translateX;
        _dragStartTranslateY = _translateY;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var current = e.GetPosition(this);
        _translateX = _dragStartTranslateX + (current.X - _dragStartPoint.X);
        _translateY = _dragStartTranslateY + (current.Y - _dragStartPoint.Y);
        ClampAndApply();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        Cursor = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
        Relayout();
    }

    // ----- Zoom-Mathematik -----

    private Point ViewportCenter => new(ActualWidth / 2, ActualHeight / 2);

    private void OnSourceChanged(BitmapSource? source)
    {
        EndDrag();
        _image.Source = source;
        if (source is null)
        {
            _pixelWidth = 0;
            _pixelHeight = 0;
            _image.Visibility = Visibility.Collapsed;
            return;
        }

        _pixelWidth = source.PixelWidth;
        _pixelHeight = source.PixelHeight;
        _image.Width = _pixelWidth;
        _image.Height = _pixelHeight;
        _image.Visibility = Visibility.Visible;
        _isFit = true; // neues Bild startet immer eingepasst
        UpdateDpi();
        Relayout();
    }

    private void UpdateDpi()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0)
            {
                _dpiScale = dpi.DpiScaleX;
            }
        }
        catch
        {
            _dpiScale = 1;
        }
    }

    /// <summary>Zoomt so, dass der Bildpunkt unter <paramref name="viewportPoint"/> an Ort und Stelle bleibt.</summary>
    private void ZoomAt(Point viewportPoint, double newScale)
    {
        if (!HasImage)
        {
            return;
        }

        newScale = Math.Clamp(newScale, MinScale, MaxScale);
        var ratio = newScale / _scale;
        _translateX = viewportPoint.X - (viewportPoint.X - _translateX) * ratio;
        _translateY = viewportPoint.Y - (viewportPoint.Y - _translateY) * ratio;
        _scale = newScale;
        _isFit = false;
        ClampAndApply();
    }

    private void Relayout()
    {
        if (!HasImage)
        {
            return;
        }

        if (_isFit)
        {
            _scale = FitScale;
        }
        else
        {
            _scale = Math.Clamp(_scale, MinScale, MaxScale);
        }

        ClampAndApply();
    }

    private void ClampAndApply()
    {
        if (!HasImage)
        {
            return;
        }

        var viewW = ActualWidth;
        var viewH = ActualHeight;
        var imgW = _pixelWidth * _scale;
        var imgH = _pixelHeight * _scale;

        _translateX = imgW <= viewW ? (viewW - imgW) / 2 : Math.Clamp(_translateX, viewW - imgW, 0);
        _translateY = imgH <= viewH ? (viewH - imgH) / 2 : Math.Clamp(_translateY, viewH - imgH, 0);

        // Auf Gerätepixel runden — vermeidet Unschärfe bei 100 % durch Subpixel-Versatz.
        _translateX = Math.Round(_translateX * _dpiScale) / _dpiScale;
        _translateY = Math.Round(_translateY * _dpiScale) / _dpiScale;

        _scaleTransform.ScaleX = _scale;
        _scaleTransform.ScaleY = _scale;
        _translateTransform.X = _translateX;
        _translateTransform.Y = _translateY;

        var actual = ActualSizeScale;
        RenderOptions.SetBitmapScalingMode(
            _image,
            _scale < actual - 1e-9 ? BitmapScalingMode.HighQuality
            : _scale > actual * 3 ? BitmapScalingMode.NearestNeighbor
            : BitmapScalingMode.Linear);

        var percent = Math.Round(_scale * _dpiScale * 100, _scale * _dpiScale * 100 < 10 ? 1 : 0);
        if (Math.Abs(ZoomPercent - percent) > 1e-9)
        {
            SetCurrentValue(ZoomPercentProperty, percent);
        }

        if (IsFit != _isFit)
        {
            SetCurrentValue(IsFitProperty, _isFit);
        }
    }
}
