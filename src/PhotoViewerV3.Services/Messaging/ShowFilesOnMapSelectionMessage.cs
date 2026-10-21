using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Services.Messaging;

/// <summary>
/// Karte → Galerie (Vertrag „Orte“, ADR-0050): zeigt die Dateien einer Kartengruppe bzw. eines Kartenausschnitts in der
/// Galerie (Filter „Kartenausschnitt“). Gesendet von der Karte (Agent W), empfangen von der Galerie (Agent V).
/// <para>Beim Zusammenbau: diese Datei gibt es in beiden Zweigen mit derselben Signatur — eine davon behalten.</para>
/// </summary>
public sealed record ShowFilesOnMapSelectionMessage(GeoBounds Bounds, IReadOnlyCollection<int> FileIds);
