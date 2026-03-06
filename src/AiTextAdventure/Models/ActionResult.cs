namespace AiTextAdventure.Models;

/// <summary>
/// Structured state changes returned by the ActionResolver agent.
/// Parsed from AI JSON response and applied to the database.
/// </summary>
public class ActionResult
{
    /// <summary>Items the player picked up this turn.</summary>
    public List<PickedUpItem> ItemsPickedUp { get; set; } = [];
    /// <summary>Item names dropped this turn (return to world entities).</summary>
    public List<string> ItemsDropped { get; set; } = [];
    /// <summary>If the player moved, the new location name. Null if no movement.</summary>
    public string? LocationChanged { get; set; }
    /// <summary>Entity names to remove from WorldState.KnownEntities.</summary>
    public List<string> EntitiesRemoved { get; set; } = [];
    /// <summary>New entity names to add to WorldState.KnownEntities (e.g. after discovering something).</summary>
    public List<string> NewEntities { get; set; } = [];
    /// <summary>
    /// Movement direction detected from player input.
    /// Values: "north","south","east","west","northeast","northwest","southeast","southwest" or null.
    /// </summary>
    public string? MovementDirection { get; set; }
}

public class PickedUpItem
{
    public string ItemName { get; set; } = "";
    public string Description { get; set; } = "";
}
