namespace AiTextAdventure.Models.Documents;

public class JournalEntry
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string EntryText { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public JournalEntryType Type { get; set; } = JournalEntryType.Narrative;
}

public enum JournalEntryType { Narrative, Quest, Discovery }
