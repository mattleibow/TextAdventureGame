using System.ComponentModel;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;

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

        ctx.EmitToolCall("drop_item", $"item={itemName}");

        await ctx.WriteLock.WaitAsync(cancellationToken);
        try
        {
        var allItems = await ctx.Store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == ctx.SaveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";

        await ctx.Store.Remove<InventoryItem>(invItem.Id.ToString(), cancellationToken);

        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is not null)
        {
            // Update the in-memory world snapshot so the item shows immediately
            worldState.KnownEntities ??= [];
            if (!worldState.KnownEntities.Contains(invItem.ItemName, StringComparer.OrdinalIgnoreCase))
                worldState.KnownEntities.Add(invItem.ItemName);

            // Persist back to MapTile so the item survives when the player leaves and returns
            var tile = await ctx.MapService.GetTile(ctx.SaveSlotId, worldState.PlayerX, worldState.PlayerY, cancellationToken);
            if (tile is not null)
            {
                tile.HiddenItems ??= [];
                if (!tile.HiddenItems.Contains(invItem.ItemName, StringComparer.OrdinalIgnoreCase))
                    tile.HiddenItems.Add(invItem.ItemName);
                await ctx.Store.Set(tile.Id.ToString(), tile, GameJsonContext.Default.MapTile, cancellationToken);
            }

            await ctx.SaveRecentEvent(worldState, $"dropped {invItem.ItemName}", cancellationToken);
        }

        ctx.EmitToolResult("🗑️", $"dropped: {invItem.ItemName}", $"Dropped an item at current location. It can be picked up again.", isAction: true);
        return $"You drop the {invItem.ItemName} on the ground.";
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }
}
