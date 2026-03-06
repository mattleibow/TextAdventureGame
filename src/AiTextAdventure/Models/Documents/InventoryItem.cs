namespace AiTextAdventure.Models.Documents;

public class InventoryItem
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public string ItemName { get; set; } = "";
    public string Description { get; set; } = "";
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Effect code: "heal:30" | "food:25" | "weapon:15" | "armor:10" | "poison:25"
    /// Empty string = no game effect (lore/decorative item).
    /// </summary>
    public string Effect { get; set; } = "";

    /// <summary>True for food and potions — consumed on use.</summary>
    public bool IsConsumable { get; set; }

    /// <summary>True for weapons and armor — occupies an equipment slot.</summary>
    public bool IsEquippable { get; set; }

    /// <summary>Human-readable effect description for the UI.</summary>
    public string EffectDisplay => Effect switch
    {
        var s when s.StartsWith("heal:") => $"✨ Heals {s[5..]} HP",
        var s when s.StartsWith("food:") => $"🍖 Reduces hunger by {s[5..]}",
        var s when s.StartsWith("weapon:") => $"⚔️ Weapon (DMG {s[7..]})",
        var s when s.StartsWith("armor:") => $"🛡️ Armor ({s[6..]} DEF)",
        var s when s.StartsWith("poison:") => $"☠️ Dangerous ({s[7..]} DMG on use)",
        "" => "",
        _ => Effect
    };
}
