using System.ComponentModel;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Tools;

public class LookAroundTool(ToolContext ctx)
{
    [Description("Search the current location carefully to reveal hidden items. Call this when the player wants to look around, search, explore, or examine their surroundings. Returns a list of discovered items that can be picked up.")]
    public async Task<string> LookAround(CancellationToken cancellationToken = default)
    {
        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is null) return "Nothing to examine — world not initialized.";

        var hiddenCountBefore = worldState.HiddenEntities?.Count ?? 0;
        await ctx.MapService.RevealTile(ctx.SaveSlotId, worldState.PlayerX, worldState.PlayerY, worldState, cancellationToken);

        await ctx.SaveRecentEvent(worldState, "searched the area", cancellationToken);

        var discovered = worldState.KnownEntities ?? [];
        ctx.EventStream.Emit(new AgentEvent(
            $"🔍 look_around: {discovered.Count} item(s) visible",
            "GameMaster", AgentEventKind.ToolResult));

        if (hiddenCountBefore > 0 && discovered.Count > 0)
            return $"You search carefully and discover: {string.Join(", ", discovered)}. These can be picked up.";
        if (discovered.Count > 0)
            return $"You look around. Visible portable items: {string.Join(", ", discovered)}.";
        return "You search thoroughly but find nothing new here.";
    }
}
