using System.ComponentModel;
using System.Text.Json;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services;

namespace AiTextAdventure.Agents.Tools;

public class LocationTools(WorldStateService worldStateService, Guid saveSlotId)
{
    [Description("Gets interactable entities and features at the current location")]
    public async Task<string> GetLocationEntities()
    {
        var state = await worldStateService.GetCurrentState(saveSlotId);
        var locations = await worldStateService.GetDiscoveredLocations(saveSlotId);
        var current = locations.FirstOrDefault(l => l.Name == state?.CurrentLocation);
        return current is null ? "No location data" : JsonSerializer.Serialize(current, GameJsonContext.Default.Location);
    }

    [Description("Gets contextual information for generating action suggestions: current location features, nearby NPCs, and inventory")]
    public async Task<string> GetAvailableContext()
    {
        var state = await worldStateService.GetCurrentState(saveSlotId);
        var locations = await worldStateService.GetDiscoveredLocations(saveSlotId);
        var currentLocation = locations.FirstOrDefault(l => l.Name == state?.CurrentLocation);

        IReadOnlyList<Npc> npcs = [];
        if (currentLocation != null)
            npcs = await worldStateService.GetNpcsAtLocation(saveSlotId, currentLocation.Id);

        var inventory = await worldStateService.GetInventory(saveSlotId);
        return JsonSerializer.Serialize(new { state, currentLocation, npcs, inventory });
    }
}
