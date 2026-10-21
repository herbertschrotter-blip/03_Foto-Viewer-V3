using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PhotoViewerV3.Lightbox.Faces;

namespace PhotoViewerV3.Lightbox.Controls;

/// <summary>
/// Gesichtsrahmen über dem Lightbox-Bild (0.2.38). Liegt als <see cref="ZoomPanControl.Overlay"/> im Zoom-Control und
/// wandert über <see cref="IZoomPanOverlay"/> mit Zoom und Schwenk mit (Box relativ 0..1 × Bildrechteck). Die Ebene
/// selbst hat keinen Hintergrund — Mausrad und Ziehen außerhalb der Rahmen gehen ans Zoom-Control; ein Klick auf einen
/// Rahmen öffnet das Menü aus <see cref="LightboxFacesViewModel.BuildMenuAsync"/>. Reine View-Mechanik.
/// </summary>
public sealed class FaceBoxesLayer : Canvas, IZoomPanOverlay
{
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.Register(
        nameof(Controller),
        typeof(LightboxFacesViewModel),
        typeof(FaceBoxesLayer),
        new PropertyMetadata(null, static (d, e) => ((FaceBoxesLayer)d).OnControllerChanged(e.OldValue as LightboxFacesViewModel, e.NewValue as LightboxFacesViewModel)));

    private static readonly Brush ConfirmedBrush = Frozen(Color.FromRgb(0x4C, 0xC2, 0x6E));
    private static readonly Brush SuggestedBrush = Frozen(Color.FromRgb(0xFF, 0xC8, 0x3D));
    private static readonly Brush UnknownBrush = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush LabelBackground = Frozen(Color.FromArgb(0xCC, 0x10, 0x10, 0x10));

    private readonly List<(LightboxFaceBox Face, Border Frame, Border Label)> _visuals = new();
    private Rect _imageRect = Rect.Empty;
    private bool _rebuildPending;

    public FaceBoxesLayer()
    {
        ClipToBounds = true;
        Background = null; // durchlässig: nur die Rahmen sind treffbar
        Focusable = false;
    }

    public LightboxFacesViewModel? Controller
    {
        get => (LightboxFacesViewModel?)GetValue(ControllerProperty);
        set => SetValue(ControllerProperty, value);
    }

    /// <summary>Anzahl gezeichneter Rahmen (Tests, Rauchtest).</summary>
    public int FrameCount => _visuals.Count;

    /// <summary>Position eines Rahmens in Koordinaten der Ebene (Tests).</summary>
    public Rect FrameBounds(int index)
    {
        var frame = _visuals[index].Frame;
        return new Rect(GetLeft(frame), GetTop(frame), frame.Width, frame.Height);
    }

    public void UpdateImageRect(Rect imageRect)
    {
        _imageRect = imageRect;
        Arrange();
    }

    private void OnControllerChanged(LightboxFacesViewModel? oldValue, LightboxFacesViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.Boxes.CollectionChanged -= OnBoxesChanged;
        }

        if (newValue is not null)
        {
            newValue.Boxes.CollectionChanged += OnBoxesChanged;
        }

        Rebuild();
    }

    /// <summary>
    /// Laden = Clear + n × Add: statt n + 1 Neuaufbauten einer, gebündelt nach dem aktuellen Durchlauf des UI-Threads.
    /// </summary>
    private void OnBoxesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildPending = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        Children.Clear();
        _visuals.Clear();
        if (Controller is not { } controller)
        {
            return;
        }

        foreach (var face in controller.Boxes)
        {
            var brush = face.Kind switch
            {
                LightboxFaceKind.Confirmed => ConfirmedBrush,
                LightboxFaceKind.Suggested => SuggestedBrush,
                _ => UnknownBrush,
            };
            var frame = new Border
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent, // treffbar für den Klick im Rahmen
                Cursor = Cursors.Hand,
                ToolTip = face.AutomationName + " — Klick zum Benennen",
                Tag = face,
            };
            AutomationProperties.SetName(frame, face.AutomationName);
            frame.MouseLeftButtonDown += OnFrameMouseLeftButtonDown;

            var label = new Border
            {
                Background = LabelBackground,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 2),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = face.Label,
                    FontSize = 12,
                    Foreground = brush,
                    FontStyle = face.Kind == LightboxFaceKind.Suggested ? FontStyles.Italic : FontStyles.Normal,
                },
            };

            Children.Add(frame);
            Children.Add(label);
            _visuals.Add((face, frame, label));
        }

        Arrange();
    }

    /// <summary>Rahmen und Beschriftungen an das aktuelle Bildrechteck legen (Beschriftung über dem Rahmen, oben am Rand darin).</summary>
    private void Arrange()
    {
        var hasImage = !_imageRect.IsEmpty && _imageRect.Width > 0 && _imageRect.Height > 0;
        foreach (var (face, frame, label) in _visuals)
        {
            frame.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = frame.Visibility;
            if (!hasImage)
            {
                continue;
            }

            var x = _imageRect.X + face.Box.X * _imageRect.Width;
            var y = _imageRect.Y + face.Box.Y * _imageRect.Height;
            var w = Math.Max(8, face.Box.Width * _imageRect.Width);
            var h = Math.Max(8, face.Box.Height * _imageRect.Height);
            SetLeft(frame, x);
            SetTop(frame, y);
            frame.Width = w;
            frame.Height = h;

            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelHeight = label.DesiredSize.Height;
            SetLeft(label, x);
            SetTop(label, y - labelHeight - 2 >= 0 ? y - labelHeight - 2 : y + 2);
        }
    }

    private async void OnFrameMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: LightboxFaceBox face } frame || Controller is not { } controller)
        {
            return;
        }

        e.Handled = true; // kein Ziehen/Doppelklick-Zoom des Bildes
        try
        {
            var items = await controller.BuildMenuAsync(face).ConfigureAwait(true);
            var menu = new ContextMenu { PlacementTarget = frame, Placement = PlacementMode.Bottom };
            foreach (var item in items)
            {
                menu.Items.Add(CreateMenuItem(item));
            }

            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Gesichter-Menü konnte nicht geöffnet werden");
        }
    }

    private static object CreateMenuItem(FaceMenuItem item)
    {
        if (item.IsSeparator)
        {
            return new Separator();
        }

        var menuItem = new MenuItem { Header = item.Header };
        if (item.Children is { } children)
        {
            foreach (var child in children)
            {
                menuItem.Items.Add(CreateMenuItem(child));
            }
        }
        else if (item.Execute is { } execute)
        {
            menuItem.Click += async (_, _) => await execute().ConfigureAwait(true);
        }

        return menuItem;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
