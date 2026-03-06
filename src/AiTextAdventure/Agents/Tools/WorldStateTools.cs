using System.ComponentModel;
using System.Text.Json;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services;

namespace AiTextAdventure.Agents.Tools;

public class WorldStateTools(WorldStateService worldStateService, Guid saveSlotId)
{
    [Description("Gets the current world state including biome, location, time of day, known entities, and recent events")]
    public async Task<string> GetWorldState()
    {
        var state = await worldStateService.GetCurrentState(saveSlotId);
        return JsonSerializer.Serialize(state, GameJsonContext.Default.WorldState);
    }

    [Description("Gets the list of already discovered locations to avoid duplicates")]
    public async Task<string> GetNearbyLocations()
    {
        var locations = await worldStateService.GetDiscoveredLocations(saveSlotId);
        return JsonSerializer.Serialize(locations, GameJsonContext.Default.ListLocation);
    }

    [Description("Gets an NPC's profile and dialog history by name")]
    public async Task<string> GetNpc(string npcName)
    {
        var state = await worldStateService.GetCurrentState(saveSlotId);
        if (state is null) return "No world state found";

        // Find the current location to get NPCs there
        var locations = await worldStateService.GetDiscoveredLocations(saveSlotId);
        var currentLocation = locations.FirstOrDefault(l => l.Name == state.CurrentLocation);
        if (currentLocation is null) return "No current location found";

        var npcs = await worldStateService.GetNpcsAtLocation(saveSlotId, currentLocation.Id);
        var npc = npcs.FirstOrDefault(n => n.Name.Equals(npcName, StringComparison.OrdinalIgnoreCase));
        return npc is null ? "NPC not found" : JsonSerializer.Serialize(npc, GameJsonContext.Default.Npc);
    }

    [Description("Gets active quest entries from the journal")]
    public async Task<string> GetActiveQuests()
    {
        var entries = await worldStateService.GetJournalEntries(saveSlotId);
        var quests = entries.Where(j => j.Type == JournalEntryType.Quest).ToList();
        return JsonSerializer.Serialize(quests, GameJsonContext.Default.ListJournalEntry);
    }

    [Description("Gets the biome-specific rules for what can and cannot exist in the current biome")]
    public Task<string> GetWorldRules()
    {
        return Task.FromResult("""
        {
            "forest": { "allowed": ["trees", "animals", "streams", "mushrooms", "flowers"], "forbidden": ["sand dunes", "icebergs", "lava", "ocean"] },
            "desert": { "allowed": ["sand", "cacti", "scorpions", "oasis", "ruins"], "forbidden": ["snow", "ice", "lush grass", "rain forest"] },
            "tundra": { "allowed": ["snow", "ice", "wolves", "frozen lakes", "caves"], "forbidden": ["tropical plants", "flowers", "warm weather"] },
            "underground": { "allowed": ["stalactites", "crystals", "mushrooms", "bats", "underground rivers"], "forbidden": ["sunshine", "open sky", "flying birds"] },
            "coastal": { "allowed": ["waves", "sand", "seagulls", "driftwood", "shells"], "forbidden": ["snow", "desert", "deep forest"] }
        }
        """);
    }
}
