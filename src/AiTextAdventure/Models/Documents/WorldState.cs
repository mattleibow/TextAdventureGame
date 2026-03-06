namespace AiTextAdventure.Models.Documents;

public class WorldState
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string CurrentBiome { get; set; } = "forest";
    public string CurrentLocation { get; set; } = "clearing";
    public string TimeOfDay { get; set; } = "morning";
    public string RegionDescription { get; set; } = "";
    /// <summary>Entities the player has discovered (visible after looking around or immediately obvious).</summary>
    public List<string> KnownEntities { get; set; } = [];
    /// <summary>Entities hidden until the player looks around — moved to KnownEntities on exploration.</summary>
    public List<string> HiddenEntities { get; set; } = [];
    /// <summary>Paths/exits to other locations shown after looking around.</summary>
    public List<string> AvailableExits { get; set; } = [];
    public List<string> RecentEvents { get; set; } = [];
}
