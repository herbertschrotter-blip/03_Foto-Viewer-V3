namespace Lumora.Domain.Enums;

/// <summary>Art eines Albums (ADR-0054).</summary>
public enum AlbumKind
{
    /// <summary>Eigenes Album: Fotos werden einzeln verknüpft (<c>AlbumItems</c>).</summary>
    Manual = 0,

    /// <summary>Intelligentes Album: gespeicherter Galerie-Filter (<c>Albums.FilterJson</c>), füllt sich selbst.</summary>
    Smart = 1,
}
