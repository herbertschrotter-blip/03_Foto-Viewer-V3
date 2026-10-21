namespace Lumora.Gallery.Controls;

/// <summary>
/// Command-Parameter von <see cref="RatingStars"/>: der durchgereichte CommandParameter
/// (z. B. das GalleryItem) plus die neue Bewertung (null = Bewertung löschen).
/// </summary>
public sealed record RatingRequest(object? Parameter, byte? Rating);
