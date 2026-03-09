using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services;

/// <summary>
/// Manages the persistent coordinate-based world map.
/// Each tile is generated once and stored permanently — the world is never randomly re-generated.
/// Tiles are generated in 3x3 batches as the player explores new areas.
/// </summary>
public class MapService(
    IDocumentStore store,
    IChatClient chatClient,
    EventStream eventStream,
    ILogger<MapService> logger)
{
    private const string TileGenSystemPrompt = """
        You are a world-builder for a text adventure game. Generate a single map location.
        Biome must be geographically compatible with any neighbouring tiles listed.
        Valid biomes: forest, plains, hills, mountain, desert, swamp, cave, ocean, ruins, tundra.
        Biome transitions: forest↔plains↔hills, hills↔mountain, plains↔desert, forest↔swamp, any↔cave, any↔ruins.
        Features are 1-2 large IMMOVABLE landmarks (altar, ruins, statue, cave entrance, well, tower, campfire, bridge, ancient tree, boulder, pool). Players can examine but not pick them up.
        HiddenItems are exactly 5 PORTABLE items a player can pick up — one each of: healing herb/potion/salve, food ration/fruit/bread, knife/sword/axe, cloak/bracers/shield, venomous creature/trap.
        Use specific evocative names. Farther from origin (Dist) = more dangerous/exotic.
        """;

    // Direction → (dx, dy) mapping
    private static readonly Dictionary<string, (int dx, int dy)> DirectionVectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = (0, -1),
        ["south"] = (0, 1),
        ["east"] = (1, 0),
        ["west"] = (-1, 0),
        ["northeast"] = (1, -1),
        ["northwest"] = (-1, -1),
        ["southeast"] = (1, 1),
        ["southwest"] = (-1, 1),
    };

    private static readonly string[] BiomeKeywords =
    [
        "forest", "plains", "hills", "mountain", "desert", "swamp", "cave", "ocean", "ruins", "tundra"
    ];

    public async Task<MapTile?> GetTile(Guid saveSlotId, int x, int y, CancellationToken ct = default)
    {
        var all = await store.GetAll<MapTile>(GameJsonContext.Default.MapTile, ct);
        return all.FirstOrDefault(t => t.SaveSlotId == saveSlotId && t.X == x && t.Y == y);
    }

    public async Task<List<MapTile>> GetAllTiles(Guid saveSlotId, CancellationToken ct = default)
    {
        var all = await store.GetAll<MapTile>(GameJsonContext.Default.MapTile, ct);
        return all.Where(t => t.SaveSlotId == saveSlotId).ToList();
    }

    /// <summary>
    /// Generates all missing tiles in a 3×3 grid centred on (centerX, centerY).
    /// Already-existing tiles are skipped. Called on game init and on movement.
    /// </summary>
    public async Task GenerateSurroundingTiles(Guid saveSlotId, int centerX, int centerY, string gameName, CancellationToken ct = default)
    {
        var allTiles = await GetAllTiles(saveSlotId, ct);
        var existing = new HashSet<(int, int)>(allTiles.Select(t => (t.X, t.Y)));

        var toGenerate = new List<(int x, int y)>();
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var tx = centerX + dx;
                var ty = centerY + dy;
                if (!existing.Contains((tx, ty)))
                    toGenerate.Add((tx, ty));
            }

        if (toGenerate.Count == 0)
        {
            logger.LogDebug("Map: All surrounding tiles already exist around ({X},{Y})", centerX, centerY);
            return;
        }

        logger.LogInformation("Map: Generating {Count} new tiles around ({X},{Y})", toGenerate.Count, centerX, centerY);
        eventStream.Emit(new AgentEvent($"🗺️ Generating {toGenerate.Count} map tiles...", "WorldGen", AgentEventKind.AgentInvoked));

        // Generate tiles one at a time (Apple Intelligence can't handle batch)
        // Build the existing tile set as we add new ones for neighbour-awareness
        var tileDict = allTiles.ToDictionary(t => (t.X, t.Y));

        foreach (var (tx, ty) in toGenerate)
        {
            var tile = await GenerateSingleTile(saveSlotId, tx, ty, tileDict, gameName, ct);
            tileDict[(tx, ty)] = tile;
            await store.Set(tile.Id.ToString(), tile, GameJsonContext.Default.MapTile, ct);
            logger.LogDebug("Map: Generated tile ({X},{Y}) = {Biome} / {Name}", tx, ty, tile.Biome, tile.LocationName);
        }

        eventStream.Emit(new AgentEvent($"🗺️ {toGenerate.Count} tiles ready", "WorldGen", AgentEventKind.AgentOutput));
    }

    /// <summary>
    /// Moves the player in the given direction. Returns the new tile (which is guaranteed to exist).
    /// Updates WorldState with new coordinates and syncs tile data to WorldState.
    /// </summary>
    public async Task<(MapTile tile, WorldState worldState)> MovePlayer(
        Guid saveSlotId,
        WorldState worldState,
        string direction,
        string gameName,
        CancellationToken ct = default)
    {
        if (!DirectionVectors.TryGetValue(direction.ToLowerInvariant(), out var vec))
        {
            logger.LogWarning("Map: Unknown direction {Dir}, defaulting to (0,0)", direction);
            vec = (0, 0);
        }

        var newX = worldState.PlayerX + vec.dx;
        var newY = worldState.PlayerY + vec.dy;

        logger.LogInformation("Map: Player moves {Dir} from ({OldX},{OldY}) to ({NewX},{NewY})", direction, worldState.PlayerX, worldState.PlayerY, newX, newY);
        eventStream.Emit(new AgentEvent($"📍 Moving {direction} → ({newX},{newY})", "ActionResolver", AgentEventKind.AgentOutput));

        // Generate surrounding tiles if needed (async — happens before the turn response)
        await GenerateSurroundingTiles(saveSlotId, newX, newY, gameName, ct);

        var tile = await GetTile(saveSlotId, newX, newY, ct) ?? FallbackTile(saveSlotId, newX, newY);
        tile.IsVisited = true;
        tile.DiscoveredAt ??= DateTime.UtcNow;
        await store.Set(tile.Id.ToString(), tile, GameJsonContext.Default.MapTile, ct);

        // Sync tile data to WorldState
        worldState.PlayerX = newX;
        worldState.PlayerY = newY;
        SyncTileToWorldState(worldState, tile);

        return (tile, worldState);
    }

    /// <summary>
    /// Marks a tile as revealed (look-around performed). Moves HiddenItems to KnownEntities (pickup list) in WorldState.
    /// Landmarks remain in LandmarkEntities — they are never added to the pickup list.
    /// </summary>
    public async Task RevealTile(Guid saveSlotId, int x, int y, WorldState worldState, CancellationToken ct = default)
    {
        var tile = await GetTile(saveSlotId, x, y, ct);
        if (tile is null) return;

        if (!tile.IsRevealed)
        {
            tile.IsRevealed = true;
            await store.Set(tile.Id.ToString(), tile, GameJsonContext.Default.MapTile, ct);
        }

        // Move hidden PORTABLE items to KnownEntities (pickup list)
        worldState.KnownEntities ??= [];
        worldState.HiddenEntities ??= [];
        foreach (var h in tile.HiddenItems)
        {
            if (!worldState.KnownEntities.Contains(h))
                worldState.KnownEntities.Add(h);
        }
        worldState.HiddenEntities = [];

        // Ensure landmarks are tracked separately (inspect-only, never pickup)
        worldState.LandmarkEntities = [.. tile.Features];

        eventStream.Emit(new AgentEvent($"🔍 Revealed {tile.HiddenItems.Count} items at ({x},{y})", "WorldGen", AgentEventKind.AgentOutput));
    }

    /// <summary>Syncs tile data into the WorldState snapshot fields.</summary>
    public void SyncTileToWorldState(WorldState worldState, MapTile tile)
    {
        worldState.CurrentBiome = tile.Biome;
        worldState.CurrentLocation = tile.LocationName;
        worldState.RegionDescription = tile.Description;

        // Landmarks are immovable features — never in KnownEntities (pickup list)
        worldState.LandmarkEntities = [.. tile.Features];

        // KnownEntities = only portable items (revealed hidden items)
        if (tile.IsRevealed)
        {
            worldState.KnownEntities = [.. tile.HiddenItems];
            worldState.HiddenEntities = [];
        }
        else
        {
            worldState.KnownEntities = [];
            worldState.HiddenEntities = [.. tile.HiddenItems];
        }

        // Build exits based on compass directions (human-readable)
        worldState.AvailableExits = CompassExits();
    }

    /// <summary>
    /// Builds a compact context string for the Narrator from a MapTile.
    /// Includes tile data so the AI doesn't invent things that don't exist.
    /// </summary>
    public string BuildTileContext(MapTile tile, bool isRevealed, List<string>? inventory = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[{tile.Biome.ToUpperInvariant()} — {tile.LocationName}] ({tile.X},{tile.Y})");
        sb.Append($"\n{tile.Description}");
        if (!string.IsNullOrEmpty(tile.Atmosphere)) sb.Append($" {tile.Atmosphere}");
        if (tile.Features.Count > 0) sb.Append($"\nLandmarks (inspect only, cannot pick up): {string.Join(", ", tile.Features)}.");
        if (isRevealed && tile.HiddenItems.Count > 0)
            sb.Append($"\nPortable items (can be picked up): {string.Join(", ", tile.HiddenItems)}.");
        if (inventory is { Count: > 0 })
            sb.Append($"\nInventory: {string.Join(", ", inventory.Take(4))}.");
        return sb.ToString();
    }

    public static (int dx, int dy) ParseDirection(string input)
    {
        var lower = input.ToLowerInvariant();
        foreach (var (dir, vec) in DirectionVectors)
            if (lower.Contains(dir)) return vec;
        return (0, 0);
    }

    public static string? DetectMovementDirection(string input)
    {
        var lower = input.ToLowerInvariant();
        var moveKeywords = new[] { "go ", "walk ", "head ", "travel ", "move ", "follow ", "take " };
        var hasMove = moveKeywords.Any(k => lower.Contains(k)) ||
                      lower.StartsWith("north") || lower.StartsWith("south") ||
                      lower.StartsWith("east") || lower.StartsWith("west");
        if (!hasMove) return null;

        // Order matters: check diagonals first
        foreach (var dir in new[] { "northeast", "northwest", "southeast", "southwest", "north", "south", "east", "west" })
            if (lower.Contains(dir)) return dir;
        return null;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<MapTile> GenerateSingleTile(
        Guid saveSlotId,
        int x, int y,
        Dictionary<(int, int), MapTile> existing,
        string gameName,
        CancellationToken ct)
    {
        // Build a compact neighbour context
        var neighbours = new System.Text.StringBuilder();
        foreach (var (dir, (dx, dy)) in DirectionVectors.Where(d => Math.Abs(d.Value.dx) + Math.Abs(d.Value.dy) == 1))
        {
            if (existing.TryGetValue((x + dx, y + dy), out var n))
                neighbours.Append($"{dir}={n.Biome} ");
        }

        var dist = Math.Abs(x) + Math.Abs(y);
        var userPrompt = $"Theme:\"{gameName}\" Pos:({x},{y}) Dist:{dist} Neighbours:{neighbours.ToString().Trim()}";

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, TileGenSystemPrompt),
                new(ChatRole.User, userPrompt)
            };
            eventStream.Emit(new AgentEvent($"📋 TileGen prompt ({x},{y})", "WorldGen", AgentEventKind.Prompt,
                userPrompt,
                $"[System]\n{TileGenSystemPrompt}\n\n[User]\n{userPrompt}"));

            var response = await chatClient.GetResponseAsync<TileGenResponse>(messages, cancellationToken: ct);
            var rawJson = response.Messages.LastOrDefault()?.Text?.Trim() ?? "";
            eventStream.Emit(new AgentEvent($"💬 TileGen response ({x},{y})", "WorldGen", AgentEventKind.Response,
                rawJson[..Math.Min(60, rawJson.Length)], rawJson));

            var tileData = response.Result;

            if (tileData is not null && !string.IsNullOrEmpty(tileData.LocationName))
            {
                var biomeStr = tileData.Biome.ToString().ToLowerInvariant();
                var tile = new MapTile
                {
                    Id = Guid.NewGuid(),
                    SaveSlotId = saveSlotId,
                    X = x,
                    Y = y,
                    DiscoveredAt = DateTime.UtcNow,
                    Biome = ValidateBiome(biomeStr, existing, x, y),
                    LocationName = tileData.LocationName,
                    Description = tileData.Description,
                    Atmosphere = tileData.Atmosphere,
                    Features = SanitizeList(tileData.Features, ["ancient stone", "weathered tree"]),
                    HiddenItems = SanitizeList(tileData.HiddenItems, DefaultHiddenItems(biomeStr)),
                };
                return tile;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Map: Tile generation failed for ({X},{Y}), using fallback", x, y);
            eventStream.Emit(new AgentEvent($"⚠️ Tile ({x},{y}) fallback", "WorldGen", AgentEventKind.Error));
        }

        return FallbackTile(saveSlotId, x, y);
    }

    private static string ValidateBiome(string biome, Dictionary<(int, int), MapTile> existing, int x, int y)
    {
        if (BiomeKeywords.Contains(biome?.ToLowerInvariant() ?? ""))
            return biome!.ToLowerInvariant();

        // Fallback: inherit from a neighbour
        foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (1, 0), (-1, 0) })
            if (existing.TryGetValue((x + dx, y + dy), out var n))
                return n.Biome;
        return "forest";
    }

    private static List<string> SanitizeList(List<string>? list, List<string> fallback)
    {
        var clean = (list ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s) &&
                        !s.StartsWith("item", StringComparison.OrdinalIgnoreCase) &&
                        !s.StartsWith("entity", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return clean.Count >= 1 ? clean : fallback;
    }

    private MapTile FallbackTile(Guid saveSlotId, int x, int y)
    {
        var dist = Math.Abs(x) + Math.Abs(y);
        var biome = dist switch { <= 2 => "forest", <= 5 => "plains", <= 8 => "hills", _ => "mountain" };
        return new MapTile
        {
            Id = Guid.NewGuid(),
            SaveSlotId = saveSlotId,
            X = x, Y = y,
            Biome = biome,
            LocationName = $"{char.ToUpper(biome[0])}{biome[1..]} ({x},{y})",
            Description = $"A {biome} stretching to the horizon.",
            Atmosphere = "The wind carries distant sounds.",
            Features = ["rocky outcrop", "twisted tree"],
            HiddenItems = DefaultHiddenItems(biome),
            DiscoveredAt = DateTime.UtcNow
        };
    }

    private static List<string> DefaultHiddenItems(string biome) => biome.ToLowerInvariant() switch
    {
        "desert" or "badlands" =>
            ["shimmering healing tonic", "salted camel jerky", "rusted iron scimitar", "sun-bleached bone shield", "desert horned viper"],
        "cave" or "dungeon" =>
            ["glowing healing mushroom", "dried cave moss cake", "iron-spiked war club", "iron-banded buckler", "venomous cave spider"],
        "ocean" or "coast" =>
            ["seaweed healing salve", "dried salted fish", "barnacle-crusted cutlass", "crab-shell pauldron", "stonefish trap"],
        "mountain" or "alpine" or "tundra" =>
            ["alpine healing herb", "frozen strip of venison", "stone-tipped climbing axe", "wolf-pelt cloak", "mountain adder"],
        "swamp" =>
            ["bog healing root", "smoked swamp eel", "sharpened bone spear", "toad-leather vest", "swamp moccasin snake"],
        "ruins" =>
            ["cracked healing vial", "hardtack ration biscuit", "corroded iron longsword", "dented iron shield", "venomous ruins adder"],
        "plains" =>
            ["prairie healing flower", "dried prairie rabbit", "flint-tipped hunting spear", "hide-stitched bracers", "prairie rattlesnake"],
        "hills" =>
            ["hill-sage healing poultice", "smoked hill-goat strips", "iron shortsword", "reinforced leather armor", "hill viper"],
        _ => // forest default
            ["luminescent healing berry", "dried mushroom rations", "carved bone hunting knife", "bark-woven leather bracers", "venomous forest asp"]
    };

    private static List<string> CompassExits() =>
    [
        "north", "south", "east", "west"
    ];
}
