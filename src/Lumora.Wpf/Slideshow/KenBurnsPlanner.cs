using System;
using System.Collections.Generic;

namespace Lumora.Slideshow;

/// <summary>
/// Sichtbarer Ausschnitt eines Bildes, relativ (0..1) zum eingepassten Bild: links oben (<see cref="X"/>, <see cref="Y"/>)
/// und Seitenlänge <see cref="Size"/> (gleich in beiden Richtungen, das Seitenverhältnis bleibt). (0, 0, 1) = ganzes Bild.
/// </summary>
public readonly record struct SlideshowViewRect(double X, double Y, double Size)
{
    public static readonly SlideshowViewRect Full = new(0, 0, 1);

    /// <summary>Vergrößerung gegenüber dem eingepassten Bild.</summary>
    public double Scale => 1 / Size;

    /// <summary>Liegt ganz im Bild (keine schwarzen Ränder durch den Zoom), mit Rundungstoleranz.</summary>
    public bool IsInsideImage => X >= -1e-9 && Y >= -1e-9 && X + Size <= 1 + 1e-9 && Y + Size <= 1 + 1e-9 && Size is > 0 and <= 1 + 1e-9;

    /// <summary>
    /// Transformation des Bild-Elements (Ursprung links oben, Elementgröße = eingepasste Bildgröße
    /// <paramref name="width"/> × <paramref name="height"/>), die diesen Ausschnitt auf die ganze Bildfläche legt.
    /// </summary>
    public (double Scale, double TranslateX, double TranslateY) ToTransform(double width, double height)
    {
        var scale = Scale;
        return (scale, -X * width * scale, -Y * height * scale);
    }
}

/// <summary>Langsamer Zoom einer Folie: von <see cref="Start"/> nach <see cref="End"/> über die Anzeigedauer.</summary>
public readonly record struct KenBurnsPlan(SlideshowViewRect Start, SlideshowViewRect End)
{
    /// <summary>Kein Zoom (abgeschaltet oder Video).</summary>
    public static readonly KenBurnsPlan None = new(SlideshowViewRect.Full, SlideshowViewRect.Full);

    public bool IsStatic => Start == End;
}

/// <summary>
/// Plant den langsamen Zoom (Ken Burns, ADR-0059): Ziel ist der Mittelpunkt der Gesichter (Rahmen aus <c>Faces</c>,
/// ADR-0035/0038 — Mitte des Rechtecks, das alle Gesichter umschließt), ohne Gesichter die Bildmitte. Der Endausschnitt
/// (1 / Zoom groß) wird um dieses Ziel gelegt und ins Bild geschoben — so entstehen nie schwarze Ränder. Abwechselnd
/// hinein- und herauszoomen wirkt ruhiger als immer dieselbe Richtung.
/// </summary>
public static class KenBurnsPlanner
{
    /// <summary>Standard-Vergrößerung am Ende (12 %): sichtbar, aber ruhig.</summary>
    public const double DefaultZoom = 1.12;

    /// <summary>Zielpunkt (relativ): Mitte aller Gesichter, sonst (0,5 | 0,5).</summary>
    public static (double X, double Y) FocusPoint(IReadOnlyList<SlideshowFaceBox>? faces)
    {
        if (faces is null || faces.Count == 0)
        {
            return (0.5, 0.5);
        }

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        var any = false;
        foreach (var face in faces)
        {
            if (face.Width <= 0 || face.Height <= 0 || double.IsNaN(face.X) || double.IsNaN(face.Y))
            {
                continue;
            }

            any = true;
            left = Math.Min(left, face.X);
            top = Math.Min(top, face.Y);
            right = Math.Max(right, face.X + face.Width);
            bottom = Math.Max(bottom, face.Y + face.Height);
        }

        if (!any)
        {
            return (0.5, 0.5);
        }

        return (Math.Clamp((left + right) / 2, 0, 1), Math.Clamp((top + bottom) / 2, 0, 1));
    }

    /// <summary>Ausschnitt der Größe 1/<paramref name="zoom"/> um <paramref name="focus"/>, ganz im Bild.</summary>
    public static SlideshowViewRect TargetRect((double X, double Y) focus, double zoom)
    {
        var size = 1 / Math.Max(1, zoom);
        var x = Math.Clamp(focus.X - (size / 2), 0, 1 - size);
        var y = Math.Clamp(focus.Y - (size / 2), 0, 1 - size);
        return new SlideshowViewRect(x, y, size);
    }

    /// <summary>Plan einer Folie: hinein (<paramref name="zoomIn"/>) vom ganzen Bild zum Ziel, sonst umgekehrt.</summary>
    public static KenBurnsPlan Plan(IReadOnlyList<SlideshowFaceBox>? faces, bool zoomIn, double zoom = DefaultZoom)
    {
        var target = TargetRect(FocusPoint(faces), zoom);
        return zoomIn ? new KenBurnsPlan(SlideshowViewRect.Full, target) : new KenBurnsPlan(target, SlideshowViewRect.Full);
    }

    /// <summary>
    /// Ausschnitt nach Anteil <paramref name="t"/> (0..1) — so wie die Ansicht es mit gleichlaufenden Skalierungs- und
    /// Verschiebe-Animationen zeigt (beide linear in <paramref name="t"/>). Bleibt für jedes t im Bild, wenn Start und Ende
    /// im Bild liegen.
    /// </summary>
    public static SlideshowViewRect At(KenBurnsPlan plan, double t)
    {
        t = Math.Clamp(t, 0, 1);
        var scale = plan.Start.Scale + ((plan.End.Scale - plan.Start.Scale) * t);
        // Verschiebung in Einheiten der Bildgröße: −X·Scale (linear zwischen Start und Ende)
        var tx = (-plan.Start.X * plan.Start.Scale) + (((-plan.End.X * plan.End.Scale) - (-plan.Start.X * plan.Start.Scale)) * t);
        var ty = (-plan.Start.Y * plan.Start.Scale) + (((-plan.End.Y * plan.End.Scale) - (-plan.Start.Y * plan.Start.Scale)) * t);
        return new SlideshowViewRect(-tx / scale, -ty / scale, 1 / scale);
    }
}
