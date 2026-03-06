namespace AiTextAdventure.Models.Documents;

public class WorldState
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string CurrentBiome { get; set; } = "forest";
    public string CurrentLocation { get; set; } = "clearing";
    public string TimeOfDay { get; set; } = "morning";
    public string RegionDescription { get; set; } = "";
    public List<string> KnownEntities { get; set; } = [];
    public List<string> RecentEvents { get; set; } = [];
}
