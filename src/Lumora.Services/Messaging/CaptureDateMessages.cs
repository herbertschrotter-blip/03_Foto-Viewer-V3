using Lumora.Services.Metadata;

namespace Lumora.Services.Messaging;

// Aufnahmedatum (ADR-0046). Gesendet vom Thread-Pool — UI-Empfänger müssen selbst dispatchen.

/// <summary>
/// Aufnahmedaten von Dateien haben sich geändert (Nachtragen, „Datum setzen…“, Rückgängig). Galerie und Lightbox
/// laden nach; bei Sortierung nach Aufnahmedatum ändert sich die Reihenfolge.
/// </summary>
public sealed record CaptureDatesChangedMessage(int LibraryId, IReadOnlyList<int> FileIds);

/// <summary>Fortschritt des Nachtragens im Hintergrund (gedrosselt).</summary>
public sealed record CaptureDateBackfillStatusMessage(CaptureDateBackfillStatus Status);
