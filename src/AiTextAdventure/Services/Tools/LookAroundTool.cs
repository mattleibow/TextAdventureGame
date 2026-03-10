using System.ComponentModel;

namespace AiTextAdventure.Services.Tools;

public class LookAroundTool(ToolContext ctx)
{
    [Description("Search the current location carefully to reveal hidden items. Call this when the player wants to look around, search, explore, or examine their surroundings. Returns a list of discovered items that can be picked up.")]
    public async Task<string> LookAround(CancellationToken cancellationToken = default)
    {
        ctx.EmitToolCall("look_around");
        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is null) return "Nothing to examine — world not initialized.";

        var hiddenCountBefore = worldState.HiddenEntities?.Count ?? 0;
        await ctx.MapService.RevealTile(ctx.SaveSlotId, worldState.PlayerX, worldState.PlayerY, worldState, cancellationToken);

        await ctx.SaveRecentEvent(worldState, "searched the area", cancellationToken);

        var discovered = worldState.KnownEntities ?? [];
        // ActionLog entry uses a generic description to avoid triggering Apple Intelligence
        // content filter when item names (Znife, etc.) are rephrased by the Narrator.
        var resultMsg = hiddenCountBefore > 0 && discovered.Count > 0
            ? $"Searched area and discovered {discovered.Count} item(s) that can be picked up."
            : discovered.Count > 0
                ? $"Looked around. Found {discovered.Count} portable item(s) nearby."
                : "Searched thoroughly but found nothing new.";

        ctx.EmitToolResult("🔍", $"look_around: {discovered.Count} item(s) visible", resultMsg, isAction: true);
        return resultMsg;
    }
}
