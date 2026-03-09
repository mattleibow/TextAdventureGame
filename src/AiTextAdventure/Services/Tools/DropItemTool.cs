using System.ComponentModel;
using Microsoft.Extensions.Logging;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Tools;

public class DropItemTool(ToolContext ctx)
{
    [Description("Drop an item from the player's inventory at the current location, placing it on the ground. Only use names from the INVENTORY list in get_world_state.")]
    public async Task<string> DropItem(
        [Description("The exact item name from the INVENTORY list in get_world_state. Copy verbatim.")]
        string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item name provided.";

        var allItems = await ctx.Store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == ctx.SaveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";

        await ctx.Store.Remove<InventoryItem>(invItem.Id.ToString(), cancellationToken);

        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is not null)
        {
            worldState.KnownEntities ??= [];
            if (!worldState.KnownEntities.Contains(invItem.ItemName, StringComparer.OrdinalIgnoreCase))
                worldState.KnownEntities.Add(invItem.ItemName);
            await ctx.SaveRecentEvent(worldState, $"dropped {invItem.ItemName}", cancellationToken);
        }

        ctx.EventStream.Emit(new AgentEvent($"🗑 dropped: {invItem.ItemName}", "GameMaster", AgentEventKind.ToolResult));
        return $"You drop the {invItem.ItemName} on the ground.";
    }
}
