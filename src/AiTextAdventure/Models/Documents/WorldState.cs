namespace AiTextAdventure.Models.Documents;

public class WorldState
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string CurrentBiome { get; set; } = "forest";
    public string CurrentLocation { get; set; } = "clearing";
    public string TimeOfDay { get; set; } = "morning";
    public string RegionDescription { get; set; } = "";

    /// <summary>Player's X coordinate on the world map. East = positive.</summary>
    public int PlayerX { get; set; } = 0;
    /// <summary>Player's Y coordinate on the world map. South = positive.</summary>
    public int PlayerY { get; set; } = 0;

    /// <summary>Entities the player can currently see (synced from MapTile.Features + revealed HiddenItems).</summary>
    public List<string> KnownEntities { get; set; } = [];
    /// <summary>Entities hidden until the player looks around (synced from MapTile.HiddenItems).</summary>
    public List<string> HiddenEntities { get; set; } = [];
    /// <summary>Named exits based on adjacent map tiles.</summary>
    public List<string> AvailableExits { get; set; } = [];
    public List<string> RecentEvents { get; set; } = [];
}
