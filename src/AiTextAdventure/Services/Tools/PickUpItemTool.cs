using System.ComponentModel;
using Microsoft.Extensions.Logging;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;

namespace AiTextAdventure.Services.Tools;

public class PickUpItemTool(ToolContext ctx)
{
    [Description("Pick up a portable item from the current location and add it to the player's inventory. Only use names from the PORTABLE ITEMS list returned by get_world_state. Do NOT pick up landmarks.")]
    public async Task<string> PickUpItem(
        [Description("The exact item name from the PORTABLE ITEMS list in get_world_state. Copy verbatim — do not abbreviate or paraphrase.")]
        string itemName,
        [Description("One evocative sentence describing what this item looks like.")]
        string description,
        [Description("REQUIRED. The item effect category. Choose one: 'weapon:15' for Zeapons (Zlades, Zwords, Znives, Zxes, Zpears, Zlubs). 'armor:10' for Zrmor and Zhields (Zacers, Zauldrons, Zucklers, Zests). 'heal:30' for Zealing items (Zalves, Zorbs, Zonics, Zials). 'food:25' for food (rations, jerky, bread, fruit). '' for gems, trinkets, or decorative artifacts.")]
        string effect,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item name provided.";

        ctx.EmitToolCall("pick_up_item", $"item={itemName}, effect={effect}");

        // Acquire the per-turn write lock: parallel tool calls from the AI would otherwise race on
        // the shared WorldState and MapTile, each loading stale state before the other saves.
        await ctx.WriteLock.WaitAsync(cancellationToken);
        try
        {
        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is null) return "Cannot pick up — world not initialized.";

        // Verify item is in the portable list (the AI must have called get_world_state first)
        var match = (worldState.KnownEntities ?? [])
            .FirstOrDefault(e => e.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            ctx.Logger.LogInformation("PickUpItem blocked — '{Item}' not in KnownEntities", itemName);
            return $"'{itemName}' is not in PORTABLE ITEMS for this location. Call get_world_state to see what can be picked up.";
        }

        // Normal pickup: add to inventory
        var normalEffect = effect ?? "";
        var invItem = new InventoryItem
        {
            Id = Guid.NewGuid(), SaveSlotId = ctx.SaveSlotId,
            ItemName = match, Description = description, Quantity = 1,
            Effect = normalEffect,
            IsConsumable = normalEffect.StartsWith("heal", StringComparison.OrdinalIgnoreCase)
                        || normalEffect.StartsWith("food", StringComparison.OrdinalIgnoreCase),
            IsEquippable = normalEffect.StartsWith("weapon", StringComparison.OrdinalIgnoreCase)
                        || normalEffect.StartsWith("armor", StringComparison.OrdinalIgnoreCase),
        };
        await ctx.Store.Set(invItem.Id.ToString(), invItem, GameJsonContext.Default.InventoryItem, cancellationToken);

        worldState.KnownEntities!.RemoveAll(e => e.Equals(match, StringComparison.OrdinalIgnoreCase));
        await ctx.RemoveFromTile(worldState, match, cancellationToken);
        await ctx.SaveRecentEvent(worldState, $"picked up {match}", cancellationToken);

        ctx.EmitToolResult("🎒", $"+{match} [{normalEffect}]", $"Picked up: {match}. Effect={normalEffect}. Inventory updated.");
        return $"You pick up the {match} and add it to your inventory.";
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }
}
