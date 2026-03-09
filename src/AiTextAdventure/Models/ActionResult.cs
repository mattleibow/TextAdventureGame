using System.ComponentModel;

namespace AiTextAdventure.Models;

/// <summary>
/// Structured state changes returned by the ActionResolver agent.
/// Applied to the database after each player action.
/// </summary>
[Description("State changes that result from the player's action. Use empty lists when nothing changed. All names MUST be copied verbatim from the provided lists — do not paraphrase, abbreviate, or invent names.")]
public class ActionResult
{
    [Description("Items the player picked up. Each ItemName MUST be copied exactly from the 'Available portable items' list provided. Empty if the player did not pick anything up.")]
    public List<PickedUpItem> ItemsPickedUp { get; set; } = [];

    [Description("Names of inventory items the player explicitly dropped. Each name MUST be copied exactly from the 'Player inventory' list. Empty if nothing was dropped.")]
    public List<string> ItemsDropped { get; set; } = [];

    [Description("Names of world entities to remove because they were picked up, consumed, or destroyed. Copy exact names from the provided lists. Empty if nothing was removed.")]
    public List<string> EntitiesRemoved { get; set; } = [];

    [Description("New portable entity names that appeared in the world this turn. Empty if nothing new appeared.")]
    public List<string> NewEntities { get; set; } = [];
}

[Description("An item the player picked up.")]
public class PickedUpItem
{
    [Description("The item name copied EXACTLY from the 'Available portable items' list — no abbreviation, no paraphrasing.")]
    public string ItemName { get; set; } = "";

    [Description("One sentence describing the item.")]
    public string Description { get; set; } = "";
}

