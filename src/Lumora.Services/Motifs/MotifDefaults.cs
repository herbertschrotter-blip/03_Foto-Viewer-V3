namespace Lumora.Services.Motifs;

/// <summary>Ein Standard-Begriff: deutscher Name (Anzeige, Suche) und englische Beschreibung für das Modell.</summary>
public sealed record MotifDefaultTerm(string Name, string Prompt);

/// <summary>
/// Start-Liste der Motiv-Begriffe (ADR-0052) — angelegt beim ersten Start je Bibliothek, danach frei änderbar.
/// <para>
/// Der Text-Vektor eines Begriffs ist der Mittelwert aus dem deutschen Wort und seiner Beschreibung
/// (<see cref="MotifDefaultTerm.Prompt"/>): SigLIP 2 ist mehrsprachig, trennt aber mit einer englischen Foto-Vorlage
/// („a photo of a dog“) am deutlichsten; das deutsche Wort allein trennt besser als die deutsche Vorlage „ein Foto von
/// einem Hund“ (Prüfung in docs/spikes/14-motive.md: mittlerer Abstand richtig − bester falscher Kandidat 0,041 für Wort
/// + englische Vorlage, 0,034 Wort, 0,017 deutsche Vorlage). Eigene Begriffe ohne Beschreibung nutzen nur ihr Wort.
/// </para>
/// </summary>
public static class MotifDefaults
{
    public static IReadOnlyList<MotifDefaultTerm> Terms { get; } =
    [
        // Landschaft, Natur, Wetter
        new("Sonnenuntergang", "a photo of a sunset"),
        new("Sonnenaufgang", "a photo of a sunrise"),
        new("Meer", "a photo of the sea"),
        new("Strand", "a photo of a beach"),
        new("Berge", "a photo of mountains"),
        new("Schnee", "a photo of snow"),
        new("Wald", "a photo of a forest"),
        new("See", "a photo of a lake"),
        new("Fluss", "a photo of a river"),
        new("Wasserfall", "a photo of a waterfall"),
        new("Wolken", "a photo of clouds in the sky"),
        new("Regenbogen", "a photo of a rainbow"),
        new("Wiese", "a photo of a green meadow"),
        new("Herbst", "a photo of autumn leaves"),
        new("Wüste", "a photo of a desert"),
        new("Blumen", "a photo of flowers"),
        new("Baum", "a photo of a tree"),
        new("Garten", "a photo of a garden"),
        new("Nacht", "a photo taken at night"),
        new("Feuerwerk", "a photo of fireworks"),

        // Tiere
        new("Hund", "a photo of a dog"),
        new("Katze", "a photo of a cat"),
        new("Pferd", "a photo of a horse"),
        new("Vogel", "a photo of a bird"),
        new("Kuh", "a photo of a cow"),
        new("Fisch", "a photo of a fish"),
        new("Schmetterling", "a photo of a butterfly"),
        new("Zoo", "a photo of animals at the zoo"),

        // Menschen und Anlässe
        new("Baby", "a photo of a baby"),
        new("Kinder", "a photo of children"),
        new("Gruppe", "a group photo of many people"),
        new("Selfie", "a selfie"),
        new("Porträt", "a portrait photo of a person"),
        new("Party", "a photo of a party"),
        new("Hochzeit", "a photo of a wedding"),
        new("Geburtstag", "a photo of a birthday party"),
        new("Weihnachten", "a photo of christmas with a christmas tree"),
        new("Ostern", "a photo of easter eggs"),
        new("Konzert", "a photo of a concert"),

        // Essen und Trinken
        new("Essen", "a photo of food"),
        new("Torte", "a photo of a cake"),
        new("Pizza", "a photo of a pizza"),
        new("Eiscreme", "a photo of ice cream"),
        new("Kaffee", "a photo of a cup of coffee"),
        new("Getränke", "a photo of drinks"),
        new("Grillen", "a photo of a barbecue grill"),
        new("Restaurant", "a photo of a restaurant"),

        // Verkehr
        new("Auto", "a photo of a car"),
        new("Fahrrad", "a photo of a bicycle"),
        new("Motorrad", "a photo of a motorcycle"),
        new("Flugzeug", "a photo of an airplane"),
        new("Boot", "a photo of a boat"),
        new("Zug", "a photo of a train"),
        new("Bus", "a photo of a bus"),
        new("Straße", "a photo of a street"),
        new("Hafen", "a photo of a harbor"),

        // Orte und Gebäude
        new("Stadt", "a photo of a city"),
        new("Kirche", "a photo of a church"),
        new("Burg", "a photo of a castle"),
        new("Brücke", "a photo of a bridge"),
        new("Haus", "a photo of a house"),
        new("Leuchtturm", "a photo of a lighthouse"),
        new("Wohnzimmer", "a photo of a living room"),
        new("Küche", "a photo of a kitchen"),
        new("Museum", "a photo of a museum"),
        new("Spielplatz", "a photo of a playground"),
        new("Schwimmbad", "a photo of a swimming pool"),
        new("Zelt", "a photo of a tent at a campsite"),

        // Freizeit und Sport
        new("Sport", "a photo of people doing sports"),
        new("Fußball", "a photo of soccer"),
        new("Skifahren", "a photo of people skiing"),
        new("Wandern", "a photo of hiking"),
        new("Spielzeug", "a photo of toys"),
        new("Musikinstrument", "a photo of a musical instrument"),
        new("Gemälde", "a photo of a painting"),

        // Dokumente und Bildschirme
        new("Dokument", "a photo of a document"),
        new("Bildschirmfoto", "a screenshot of a phone or computer screen"),
        new("Quittung", "a photo of a receipt"),
        new("Schild", "a photo of a sign with text"),
        new("Landkarte", "a photo of a map"),
        new("Zeichnung", "a drawing"),
        new("Buch", "a photo of a book"),
    ];
}
