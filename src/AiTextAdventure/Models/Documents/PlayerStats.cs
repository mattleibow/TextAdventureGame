namespace AiTextAdventure.Models.Documents;

/// <summary>
/// Tracks the adventurer's vital statistics. Updated each turn and when items are used.
/// </summary>
public class PlayerStats
{
    public Guid Id { get; set; }
    public Guid SaveSlotId { get; set; }
    public int Health { get; set; } = 100;
    public int MaxHealth { get; set; } = 100;
    /// <summary>0 = full, 100 = starving. Increases each turn. High hunger damages health.</summary>
    public int Hunger { get; set; } = 0;
    /// <summary>0 = rested, 100 = exhausted. Increases each turn. High tiredness damages health.</summary>
    public int Tiredness { get; set; } = 0;
    /// <summary>Damage reduction from equipped armor.</summary>
    public int Armor { get; set; } = 0;
    public string? EquippedWeapon { get; set; }
    public string? EquippedArmor { get; set; }
}
