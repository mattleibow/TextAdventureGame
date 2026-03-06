using System.ComponentModel;
using System.Text.Json;
using AiTextAdventure.Models;
using AiTextAdventure.Services;

namespace AiTextAdventure.Agents.Tools;

public class InventoryTools(IWorldStateService worldStateService, Guid saveSlotId)
{
    [Description("Gets the player's current inventory items")]
    public async Task<string> GetInventory()
    {
        var items = await worldStateService.GetInventory(saveSlotId);
        return JsonSerializer.Serialize(items, GameJsonContext.Default.ListInventoryItem);
    }
}
