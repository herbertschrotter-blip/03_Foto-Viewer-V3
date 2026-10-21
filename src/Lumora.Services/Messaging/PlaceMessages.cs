using Lumora.Services.Places;

namespace Lumora.Services.Messaging;

// Orte (ADR-0050). Gesendet vom Thread-Pool — UI-Empfänger müssen selbst dispatchen.

/// <summary>
/// Koordinaten bzw. Orte von Dateien haben sich geändert (Nachtragen, „Ort setzen…“, „Ort entfernen“, Rückgängig).
/// Galerie, Lightbox und Karte laden nach.
/// </summary>
public sealed record GeoLocationsChangedMessage(int LibraryId, IReadOnlyList<int> FileIds);

/// <summary>Fortschritt des Nachtragens der Koordinaten (gedrosselt).</summary>
public sealed record GeoBackfillStatusMessage(GeoBackfillStatus Status);
