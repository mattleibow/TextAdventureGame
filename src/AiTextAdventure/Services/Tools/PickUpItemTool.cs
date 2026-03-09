using System.ComponentModel;
using Microsoft.Extensions.Logging;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Tools;

public class PickUpItemTool(ToolContext ctx)
{
    [Description("Pick up a portable item from the current location and add it to the player's inventory. Only use names from the PORTABLE ITEMS list returned by get_world_state. Do NOT pick up landmarks.")]
    public async Task<string> PickUpItem(
        [Description("The exact item name from the PORTABLE ITEMS list in get_world_state. Copy verbatim — do not abbreviate or paraphrase.")]
        string itemName,
        [Description("One evocative sentence describing what this item looks like.")]
        string description,
        [Description("REQUIRED. The item's gameplay classification — analyze the item name and set this correctly. Weapons (swords, knives, axes, spears, bows, clubs, scimitars, rapiers) → 'weapon:15'. Armor and protection (shields, bracers, mail, cloaks, helms, pauldrons) → 'armor:10'. Healing items (potions, herbs, tonics, salves, vials) → 'heal:30'. Food (bread, jerky, meat, fruit, berries, rations) → 'food:25'. Venomous creatures or traps → 'poison:25'. Use empty string ONLY for purely decorative items with no combat or survival use.")]
        string effect,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item name provided.";

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

        // Poison/dangerous items: deal damage on contact but don't enter inventory
        if (!string.IsNullOrEmpty(effect) && effect.StartsWith("poison", StringComparison.OrdinalIgnoreCase))
        {
            var dmgStats = await ctx.WorldStateService.GetPlayerStats(ctx.SaveSlotId, cancellationToken)
                          ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = ctx.SaveSlotId };
            ToolContext.ApplyEffect(dmgStats, effect);
            await ctx.WorldStateService.SavePlayerStats(dmgStats, cancellationToken);

            worldState.KnownEntities!.RemoveAll(e => e.Equals(match, StringComparison.OrdinalIgnoreCase));
            await ctx.RemoveFromTile(worldState, match, cancellationToken);
            await ctx.SaveRecentEvent(worldState, $"touched {match} — took damage!", cancellationToken);

            ctx.EventStream.Emit(new AgentEvent($"☠️ poison: {match}", "GameMaster", AgentEventKind.ToolResult,
                $"HP: {dmgStats.Health}/{dmgStats.MaxHealth}"));
            return $"You reach for {match} — it bites! Venomous. You take damage. HP: {dmgStats.Health}/{dmgStats.MaxHealth}. It falls away.";
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

        ctx.EventStream.Emit(new AgentEvent($"🎒 +{match} [{normalEffect}]", "GameMaster", AgentEventKind.ToolResult));
        return $"You pick up the {match} and add it to your inventory.";
    }
}
