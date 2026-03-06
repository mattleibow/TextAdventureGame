namespace AiTextAdventure.Models.Documents;

public class InventoryItem
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string ItemName { get; set; } = "";
    public string Description { get; set; } = "";
    public int Quantity { get; set; } = 1;
}
