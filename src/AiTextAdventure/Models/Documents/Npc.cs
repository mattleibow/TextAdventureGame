namespace AiTextAdventure.Models.Documents;

public class Npc
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string Name { get; set; } = "";
    public string Personality { get; set; } = "";
    public Guid LocationId { get; set; }
    public List<DialogEntry> DialogHistory { get; set; } = [];
}

public record DialogEntry(string Speaker, string Text, DateTime Timestamp);
