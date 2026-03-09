using System.ComponentModel;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Tools;

public class EquipItemTool(ToolContext ctx)
{
    [Description("Equip a weapon or armor from the player's inventory as active gear. Weapons increase combat effectiveness; armor reduces incoming damage. Item remains in inventory after equipping.")]
    public async Task<string> EquipItem(
        [Description("The exact item name of the weapon or armor from the INVENTORY list in get_world_state. Copy verbatim.")]
        string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item name provided.";

        var allItems = await ctx.Store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == ctx.SaveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";
        var isWeapon = invItem.IsEquippable && invItem.Effect.StartsWith("weapon", StringComparison.OrdinalIgnoreCase);
        var isArmor  = invItem.IsEquippable && invItem.Effect.StartsWith("armor", StringComparison.OrdinalIgnoreCase);

        if (!isWeapon && !isArmor)
            return $"'{itemName}' cannot be equipped. It may have been picked up without a weapon/armor effect set. Try dropping it and picking it up again, or use use_item if it's consumable.";

        var stats = await ctx.WorldStateService.GetPlayerStats(ctx.SaveSlotId, cancellationToken)
                   ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = ctx.SaveSlotId };

        string resultMsg;
        if (isWeapon)
        {
            stats.EquippedWeapon = invItem.ItemName;
            resultMsg = $"You equip the {invItem.ItemName} as your weapon.";
            ctx.EventStream.Emit(new AgentEvent($"⚔️ equipped weapon: {invItem.ItemName}", "GameMaster", AgentEventKind.ToolResult));
        }
        else
        {
            stats.EquippedArmor = invItem.ItemName;
            if (invItem.Effect.Length > 6 && int.TryParse(invItem.Effect.AsSpan(6), out var armorVal))
                stats.Armor = armorVal;
            resultMsg = $"You equip the {invItem.ItemName} (+{stats.Armor} armor).";
            ctx.EventStream.Emit(new AgentEvent($"🛡️ equipped armor: {invItem.ItemName} (+{stats.Armor})", "GameMaster", AgentEventKind.ToolResult));
        }

        await ctx.WorldStateService.SavePlayerStats(stats, cancellationToken);

        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is not null)
            await ctx.SaveRecentEvent(worldState, $"equipped {invItem.ItemName}", cancellationToken);

        return resultMsg;
    }
}
