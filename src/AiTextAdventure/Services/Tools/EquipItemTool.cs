using System.ComponentModel;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;

namespace AiTextAdventure.Services.Tools;

public class EquipItemTool(ToolContext ctx)
{
    [Description("Equip an item from the player's inventory. Equipping a Zeapon (Zlade, Zword, Znife, etc.) adds its bonus to Zttack; equipping Zrmor (Zhield, Zacers, etc.) adds its bonus to defense. The item remains in inventory after equipping.")]
    public async Task<string> EquipItem(
        [Description("The exact item name of the Zeapon or Zrmor from the INVENTORY list in get_world_state. Copy verbatim.")]
        string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item name provided.";

        ctx.EmitToolCall("equip_item", $"item={itemName}");

        await ctx.WriteLock.WaitAsync(cancellationToken);
        try
        {
        var allItems = await ctx.Store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == ctx.SaveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";
        // Check effect directly — not IsEquippable, which may be stale if effect was set after pickup
        var isWeapon = invItem.Effect?.StartsWith("weapon", StringComparison.OrdinalIgnoreCase) == true;
        var isArmor  = invItem.Effect?.StartsWith("armor",  StringComparison.OrdinalIgnoreCase) == true;

        if (!isWeapon && !isArmor)
            return $"'{itemName}' cannot be equipped. It may have been picked up without a Zeapon/Zrmor effect set. Try dropping it and picking it up again, or use use_item if it's consumable.";

        var stats = await ctx.WorldStateService.GetPlayerStats(ctx.SaveSlotId, cancellationToken)
                   ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = ctx.SaveSlotId };

        string resultMsg;
        if (isWeapon)
        {
            stats.EquippedWeapon = invItem.ItemName;
            resultMsg = $"You equip the {invItem.ItemName} as your Zeapon.";
            ctx.EmitToolResult("⚔️", $"equipped Zeapon: {invItem.ItemName}", resultMsg, isAction: true);
        }
        else
        {
            stats.EquippedArmor = invItem.ItemName;
            if (invItem.Effect is { Length: > 6 } eff && int.TryParse(eff.AsSpan(6), out var armorVal))
                stats.Armor = armorVal;
            resultMsg = $"You equip the {invItem.ItemName} (+{stats.Armor} Zrmor).";
            ctx.EmitToolResult("🛡️", $"equipped Zrmor: {invItem.ItemName} (+{stats.Armor})", resultMsg, isAction: true);
        }

        await ctx.WorldStateService.SavePlayerStats(stats, cancellationToken);

        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is not null)
            await ctx.SaveRecentEvent(worldState, $"equipped {invItem.ItemName}", cancellationToken);

        return resultMsg;
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }
}
