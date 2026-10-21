using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;
using Lumora.Services.Metadata;

namespace Lumora.Lightbox;

/// <summary>
/// Aufnahmedatum in der Info-Leiste der Lightbox (ADR-0046): Zeile „Aufgenommen“ direkt nach „Geändert“, z. B.
/// „12.07.2024 15:30 (EXIF)“ oder „ca. 1982 (manuell)“. Asynchron aus dem Katalog gelesen; nach „Datum setzen…“,
/// Rückgängig oder dem Nachtragen (<see cref="CaptureDatesChangedMessage"/>) neu.
/// </summary>
public sealed partial class LightboxViewModel
{
    internal const string CaptureDateLabel = "Aufgenommen";

    private int _captureRowVersion;

    private void InitCaptureDate()
        => WeakReferenceMessenger.Default.Register<LightboxViewModel, CaptureDatesChangedMessage>(
            this,
            static (r, m) => r._dispatcher.InvokeAsync(() => r.OnCaptureDatesChanged(m)));

    private void OnCaptureDatesChanged(CaptureDatesChangedMessage message)
    {
        if (!_disposed && CurrentItem is { } item && message.FileIds.Contains(item.FileId))
        {
            QueueCaptureDateRow(item);
        }
    }

    /// <summary>Nach <c>BuildFileInfo</c>: Zeile „Aufgenommen“ nachladen (jüngster Auftrag gewinnt).</summary>
    private void QueueCaptureDateRow(LightboxItem item)
    {
        var version = ++_captureRowVersion;
        _ = LoadCaptureDateRowAsync(item, version);
    }

    private async Task LoadCaptureDateRowAsync(LightboxItem item, int version)
    {
        try
        {
            var fileId = item.FileId;
            var info = await Task.Run(async () =>
            {
                await using var ctx = _contextFactory.Create();
                var row = await ctx.Files.AsNoTracking()
                    .Where(f => f.Id == fileId)
                    .Select(f => new { f.CapturedAt, f.CapturedAtSource, f.CapturedAtPrecision, f.CapturedAtOffset })
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
                if (row is null)
                {
                    return null;
                }

                long? unix = row.CapturedAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeSeconds() : null;
                return CaptureDateInfo.FromColumns(fileId, unix, (int?)row.CapturedAtSource, (int)row.CapturedAtPrecision, row.CapturedAtOffset);
            }).ConfigureAwait(true);

            if (_disposed || version != _captureRowVersion || !ReferenceEquals(item, CurrentItem) || info is null)
            {
                return;
            }

            var newRow = new LightboxInfoRow(CaptureDateLabel, info.Text);
            var existing = FileInfoRows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Label == CaptureDateLabel);
            if (existing.r is not null)
            {
                FileInfoRows[existing.i] = newRow;
                return;
            }

            var modified = FileInfoRows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Label == "Geändert");
            FileInfoRows.Insert(modified.r is not null ? modified.i + 1 : FileInfoRows.Count, newRow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Aufnahmedatum für die Lightbox nicht lesbar (FileId {FileId})", item.FileId);
        }
    }
}
