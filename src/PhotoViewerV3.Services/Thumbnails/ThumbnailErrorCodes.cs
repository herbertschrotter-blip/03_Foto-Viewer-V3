namespace PhotoViewerV3.Services.Thumbnails;

/// <summary>
/// Stabile ErrorCodes der Renderer (landen in <c>Thumbnails.ErrorCode</c>). Der WIC-Renderer im
/// Wpf-Projekt liefert <see cref="WicDecodeFailed"/>; darauf reagiert der FFmpeg-Fallback.
/// </summary>
public static class ThumbnailErrorCodes
{
    public const string SourceMissing = "source_missing";
    public const string WicDecodeFailed = "wic_decode_failed";
    public const string FfmpegNotFound = "ffmpeg_not_found";
    public const string FfmpegStartFailed = "ffmpeg_start_failed";
    public const string FfmpegException = "ffmpeg_exception";
    public const string FfmpegCancelled = "ffmpeg_cancelled";
    public const string FfmpegFailed = "ffmpeg_failed";
}
