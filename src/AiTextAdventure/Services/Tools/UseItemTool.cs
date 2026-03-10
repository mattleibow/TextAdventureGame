using System.ComponentModel;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;

namespace AiTextAdventure.Services.Tools;

public class UseItemTool(ToolContext ctx)
{
    [Description("Use or consume an item from the player's inventory. Use for: eating food, applying Zealing Zalves, drinking Zonics, reading scrolls. Removes the item after use. For Zeapons or Zrmor use equip_item instead.")]
    public async Task<string> UseItem(
        [Description("The exact item name from the INVENTORY list in get_world_state. Copy verbatim.")]
        string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item name provided.";

        ctx.EmitToolCall("use_item", $"item={itemName}");

        await ctx.WriteLock.WaitAsync(cancellationToken);
        try
        {
        var allItems = await ctx.Store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == ctx.SaveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";
        if (!invItem.IsConsumable)
            return $"'{itemName}' cannot be consumed. If it is a Zeapon or Zrmor, use equip_item instead.";

        var stats = await ctx.WorldStateService.GetPlayerStats(ctx.SaveSlotId, cancellationToken)
                   ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = ctx.SaveSlotId };
        var beforeHp = stats.Health;
        var beforeHunger = stats.Hunger;
        ToolContext.ApplyEffect(stats, invItem.Effect);
        await ctx.WorldStateService.SavePlayerStats(stats, cancellationToken);
        await ctx.Store.Remove<InventoryItem>(invItem.Id.ToString(), cancellationToken);

        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is not null)
            await ctx.SaveRecentEvent(worldState, $"used {invItem.ItemName}", cancellationToken);

        ctx.EmitToolResult("✨", $"used: {invItem.ItemName}", $"Used {invItem.ItemName}. HP:{beforeHp}→{stats.Health} Hunger:{beforeHunger}→{stats.Hunger}");
        return $"You use the {invItem.ItemName}. HP: {beforeHp}→{stats.Health}. Hunger: {beforeHunger}→{stats.Hunger}.";
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }
}
