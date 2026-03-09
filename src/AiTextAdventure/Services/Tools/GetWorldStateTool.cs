using System.ComponentModel;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;

namespace AiTextAdventure.Services.Tools;

public class GetWorldStateTool(ToolContext ctx)
{
    [Description("Get the current world state: player location, visible portable items, landmarks, inventory, and stats. Always call this first before acting so you know what exists.")]
    public async Task<string> GetWorldState(CancellationToken cancellationToken = default)
    {
        ctx.EmitToolCall("get_world_state");
        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        var stats = await ctx.WorldStateService.GetPlayerStats(ctx.SaveSlotId, cancellationToken);
        var allItems = await ctx.Store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var inventory = allItems.Where(i => i.SaveSlotId == ctx.SaveSlotId).ToList();

        var sb = new System.Text.StringBuilder();
        if (worldState is not null)
        {
            sb.AppendLine($"LOCATION: {worldState.CurrentLocation} ({worldState.CurrentBiome}) at ({worldState.PlayerX},{worldState.PlayerY})");
            sb.AppendLine($"TIME: {worldState.TimeOfDay}");
            sb.AppendLine();

            var portable = worldState.KnownEntities ?? [];
            sb.AppendLine(portable.Count > 0
                ? $"PORTABLE ITEMS (can be picked up): {string.Join(", ", portable)}"
                : "PORTABLE ITEMS: none visible yet — call look_around to search");

            var landmarks = worldState.LandmarkEntities ?? [];
            if (landmarks.Count > 0)
                sb.AppendLine($"LANDMARKS (examine only, cannot be picked up): {string.Join(", ", landmarks)}");

            var hidden = worldState.HiddenEntities ?? [];
            if (hidden.Count > 0)
                sb.AppendLine($"HIDDEN: {hidden.Count} undiscovered item(s) — call look_around to reveal them");

            sb.AppendLine();
            sb.AppendLine(inventory.Count > 0
                ? $"INVENTORY: {string.Join(", ", inventory.Select(i => $"{i.ItemName} [{(string.IsNullOrEmpty(i.Effect) ? "misc" : i.Effect)}]"))}"
                : "INVENTORY: empty");

            if (stats is not null)
            {
                sb.AppendLine($"STATS: HP:{stats.Health}/{stats.MaxHealth} | Hunger:{stats.Hunger}/100 | Tiredness:{stats.Tiredness}/100 | Armor:{stats.Armor}");
                if (!string.IsNullOrEmpty(stats.EquippedWeapon)) sb.AppendLine($"WEAPON: {stats.EquippedWeapon}");
                if (!string.IsNullOrEmpty(stats.EquippedArmor)) sb.AppendLine($"ARMOR: {stats.EquippedArmor}");
            }

            if (worldState.RecentEvents?.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"RECENT EVENTS: {string.Join(" → ", worldState.RecentEvents)}");
            }
        }
        else
        {
            sb.AppendLine("LOCATION: unknown — world not yet initialized");
        }

        var result = sb.ToString().Trim();
        ctx.EmitToolResult("🌍", $"get_world_state ({worldState?.CurrentLocation ?? "unknown"})", result);
        return result;
    }
}
