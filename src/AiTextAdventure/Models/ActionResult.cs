using System.ComponentModel;

namespace AiTextAdventure.Models;

/// <summary>
/// Structured output from the ActionResolver agent.
/// The AI determines everything: what was picked up, what was used, whether the player is
/// moving or exploring. All names must be copied verbatim from the provided entity lists.
/// </summary>
[Description("State changes from the player's action. ALL item names must be copied verbatim from the provided lists. Do not paraphrase or invent names.")]
public class ActionResult
{
    [Description("Items the player picked up from the world this turn. Each name must be copied exactly from the 'Available portable items' list. Empty if nothing was picked up.")]
    public List<PickedUpItem> ItemsPickedUp { get; set; } = [];

    [Description("Inventory items the player used or consumed this turn (e.g. ate food, drank potion). Each name must be copied exactly from the 'Player inventory' list. Empty if nothing was used.")]
    public List<string> ItemsUsed { get; set; } = [];

    [Description("Inventory items the player equipped this turn (wielded weapon or worn armor). Each name must be copied exactly from the 'Player inventory' list. Empty if nothing was equipped.")]
    public List<string> ItemsEquipped { get; set; } = [];

    [Description("Items dropped from inventory onto the ground. Each name must be copied exactly from the 'Player inventory' list. Empty if nothing was dropped.")]
    public List<string> ItemsDropped { get; set; } = [];

    [Description("World entity names to remove after being picked up or destroyed. Copy exact names from provided lists. Empty if nothing was removed.")]
    public List<string> EntitiesRemoved { get; set; } = [];

    [Description("If the player is moving, the direction: north, south, east, west, northeast, northwest, southeast, or southwest. Null if the player is NOT moving.")]
    public string? MovementDirection { get; set; }

    [Description("True if the player is searching, exploring, or looking around to discover what is hidden here. False otherwise.")]
    public bool IsExploring { get; set; }
}

/// <summary>An item picked up by the player, including its game effect.</summary>
[Description("An item the player picked up.")]
public class PickedUpItem
{
    [Description("Item name copied EXACTLY from the 'Available portable items' list — no abbreviation, no paraphrasing.")]
    public string ItemName { get; set; } = "";

    [Description("One sentence describing the item.")]
    public string Description { get; set; } = "";

    [Description("The item's game effect when used: 'heal:N' restores N health, 'food:N' reduces hunger by N, 'weapon:N' gives N attack power, 'armor:N' gives N defense, 'poison:N' deals N damage. Use empty string if the item has no consumable/equippable effect.")]
    public string Effect { get; set; } = "";
}

