using System.ComponentModel;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Tools;

public class GetCurrentTileTool(ToolContext ctx)
{
    [Description("Get the detailed description of the current map tile: biome, atmosphere, visible landmarks, and available exits. Use this to describe the scene to the player.")]
    public async Task<string> GetCurrentTile(CancellationToken cancellationToken = default)
    {
        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is null) return "No tile data available — world not initialized.";

        var tile = await ctx.MapService.GetTile(ctx.SaveSlotId, worldState.PlayerX, worldState.PlayerY, cancellationToken);
        if (tile is null) return $"You stand in featureless {worldState.CurrentBiome} terrain.";

        var context = ctx.MapService.BuildTileContext(tile, tile.IsRevealed);
        ctx.EventStream.Emit(new AgentEvent("🗺️ get_current_tile", "GameMaster", AgentEventKind.ToolResult,
            context[..Math.Min(120, context.Length)]));
        return context;
    }
}
