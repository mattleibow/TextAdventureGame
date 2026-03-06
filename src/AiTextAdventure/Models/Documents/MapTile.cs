namespace AiTextAdventure.Models.Documents;

/// <summary>
/// A single tile on the persistent world map. Each tile is uniquely identified by (SaveSlotId, X, Y).
/// Generated once by the WorldGen agent; never randomly re-generated.
/// </summary>
public class MapTile
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }

    /// <summary>X coordinate. East = positive, West = negative.</summary>
    public int X { get; set; }
    /// <summary>Y coordinate. South = positive, North = negative.</summary>
    public int Y { get; set; }

    public string Biome { get; set; } = "forest";
    public string LocationName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Atmosphere { get; set; } = "";

    /// <summary>Immediately visible large features (immovable). Shown from first visit.</summary>
    public List<string> Features { get; set; } = [];

    /// <summary>Items revealed only after "look around". Include healing/food/weapon/armor/danger.</summary>
    public List<string> HiddenItems { get; set; } = [];

    /// <summary>True once the player has stepped on this tile.</summary>
    public bool IsVisited { get; set; }

    /// <summary>True once the player has performed "look around" on this tile.</summary>
    public bool IsRevealed { get; set; }

    public DateTime? DiscoveredAt { get; set; }

    /// <summary>Distance from origin tile (0,0). Used for difficulty scaling.</summary>
    public int DistanceFromOrigin => Math.Abs(X) + Math.Abs(Y);
}
