using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AiTextAdventure.Models;

/// <summary>
/// AI-generated structured response for a single map tile.
/// Separate from the MapTile DB document so we can add AI-friendly attributes
/// (Description, enums) without affecting persisted data shapes.
/// </summary>
[Description("A generated map location tile for a text adventure game.")]
public class TileGenResponse
{
    [Description("Terrain biome. Must be one of: forest, plains, hills, mountain, desert, swamp, cave, ocean, ruins, tundra. Must be geographically compatible with neighbouring tiles.")]
    public BiomeKind Biome { get; set; } = BiomeKind.Forest;

    [Description("Short evocative name for this specific location (2-4 words, e.g. 'Mossy Stone Bridge', 'Crumbling Watch Tower').")]
    public string LocationName { get; set; } = "";

    [Description("One vivid sentence describing what the player sees when they arrive here.")]
    public string Description { get; set; } = "";

    [Description("Brief mood phrase (3-5 words) capturing the atmosphere, e.g. 'Eerily quiet and cold'.")]
    public string Atmosphere { get; set; } = "";

    [Description("1-2 large IMMOVABLE landmarks or environmental features (e.g. 'mossy stone altar', 'collapsed tower', 'ancient oak tree', 'bubbling hot spring'). The player can examine these but NOT pick them up.")]
    public List<string> Features { get; set; } = [];

    [Description("Exactly 5 PORTABLE items the player can pick up — one of each: (1) healing item (herb, salve, potion), (2) food (ration, fruit, bread), (3) weapon (knife, axe, sword), (4) armor or protection (cloak, bracers, shield), (5) dangerous thing (venomous snake, bear trap, poisoned dart). Use specific evocative names, never generic placeholders.")]
    public List<string> HiddenItems { get; set; } = [];
}

/// <summary>Terrain biome for a map tile.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BiomeKind>))]
public enum BiomeKind
{
    [JsonStringEnumMemberName("forest")]   Forest,
    [JsonStringEnumMemberName("plains")]   Plains,
    [JsonStringEnumMemberName("hills")]    Hills,
    [JsonStringEnumMemberName("mountain")] Mountain,
    [JsonStringEnumMemberName("desert")]   Desert,
    [JsonStringEnumMemberName("swamp")]    Swamp,
    [JsonStringEnumMemberName("cave")]     Cave,
    [JsonStringEnumMemberName("ocean")]    Ocean,
    [JsonStringEnumMemberName("ruins")]    Ruins,
    [JsonStringEnumMemberName("tundra")]   Tundra,
}
