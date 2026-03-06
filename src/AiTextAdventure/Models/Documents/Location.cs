namespace AiTextAdventure.Models.Documents;

public class Location
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string Name { get; set; } = "";
    public string Biome { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Features { get; set; } = [];
    public List<string> Entities { get; set; } = [];
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}
