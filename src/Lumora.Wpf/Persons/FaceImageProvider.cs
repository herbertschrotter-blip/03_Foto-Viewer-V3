using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumora.Services.Faces.Crops;

namespace Lumora.Persons;

/// <summary>
/// Liefert Gesichts-Ausschnitte als eingefrorene <see cref="ImageSource"/> an die Personen-Seite: Anforderung an den
/// Cache (<see cref="IFaceCropService"/>), Laden + Einfrieren auf dem Worker-Thread, dann GEBÜNDELT mit
/// Background-Priorität auf den UI-Thread (CLAUDE.md — sonst lassen hunderte Einzelaufrufe Eingabe und Rendering
/// verhungern). Ohne WPF-Application (Tests) wird sofort gemeldet.
/// </summary>
public sealed class FaceImageProvider : IDisposable
{
    private readonly IFaceCropService _crops;
    private readonly ConcurrentQueue<(long FaceId, ImageSource Image)> _ready = new();
    private int _flushScheduled;

    public FaceImageProvider(IFaceCropService crops)
    {
        _crops = crops;
        _crops.CropReady += OnCropReady;
    }

    /// <summary>Fertige Bilder (UI-Thread, gebündelt).</summary>
    public event Action<IReadOnlyList<(long FaceId, ImageSource Image)>>? ImagesReady;

    public void Request(int libraryId, IReadOnlyCollection<long> faceIds)
    {
        if (faceIds.Count > 0)
        {
            _crops.Request(libraryId, faceIds);
        }
    }

    private void OnCropReady(FaceCropReadyEventArgs args)
    {
        var image = TryLoad(args.Path);
        if (image is null)
        {
            return;
        }

        _ready.Enqueue((args.FaceId, image));
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Flush();
            return;
        }

        _ = dispatcher.InvokeAsync(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        var batch = new List<(long, ImageSource)>();
        while (_ready.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count > 0)
        {
            ImagesReady?.Invoke(batch);
        }
    }

    internal static ImageSource? TryLoad(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bmp.StreamSource = fs;
                bmp.EndInit();
            }

            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose() => _crops.CropReady -= OnCropReady;
}
