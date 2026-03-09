using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AiTextAdventure.Models;

/// <summary>
/// Structured state changes returned by the ActionResolver agent.
/// Applied to the database after each player action.
/// </summary>
[Description("State changes that result from the player's action this turn. Only include changes that clearly happened — use empty lists when nothing changed.")]
public class ActionResult
{
    [Description("Items the player explicitly picked up this turn. Empty if the player did not pick up anything.")]
    public List<PickedUpItem> ItemsPickedUp { get; set; } = [];

    [Description("Names of items the player explicitly dropped this turn. Empty if nothing was dropped.")]
    public List<string> ItemsDropped { get; set; } = [];

    [Description("If the player moved to a new named location, the location name. Null if no movement occurred.")]
    public string? LocationChanged { get; set; }

    [Description("Names of entities or items to remove from the world (e.g. consumed, destroyed). Empty if nothing was removed.")]
    public List<string> EntitiesRemoved { get; set; } = [];

    [Description("New entities or items that appeared in the world this turn (e.g. something discovered). Empty if nothing new appeared.")]
    public List<string> NewEntities { get; set; } = [];

    [Description("The cardinal direction the player moved, if any movement occurred. Null if no movement.")]
    public MovementDirection? MovementDirection { get; set; }
}

[Description("An item the player picked up, with its name and a short description.")]
public class PickedUpItem
{
    [Description("Exact name of the item as it appears in the world item list.")]
    public string ItemName { get; set; } = "";

    [Description("Brief description of the item (1 sentence).")]
    public string Description { get; set; } = "";
}

/// <summary>Cardinal movement directions a player can travel.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MovementDirection>))]
public enum MovementDirection
{
    [JsonStringEnumMemberName("north")]     North,
    [JsonStringEnumMemberName("south")]     South,
    [JsonStringEnumMemberName("east")]      East,
    [JsonStringEnumMemberName("west")]      West,
    [JsonStringEnumMemberName("northeast")] Northeast,
    [JsonStringEnumMemberName("northwest")] Northwest,
    [JsonStringEnumMemberName("southeast")] Southeast,
    [JsonStringEnumMemberName("southwest")] Southwest,
}

