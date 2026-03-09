using System.ComponentModel;
using Microsoft.Extensions.Logging;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Tools;

public class MovePlayerTool(ToolContext ctx)
{
    [Description("Move the player in a compass direction. Automatically generates new map tiles when the player enters unexplored territory. Returns a description of the new location.")]
    public async Task<string> MovePlayer(
        [Description("The direction to move. Must be exactly one of: north, south, east, west, northeast, northwest, southeast, southwest")]
        string direction,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return "No direction specified. Use: north, south, east, west, northeast, northwest, southeast, or southwest.";

        var worldState = await ctx.WorldStateService.GetCurrentState(ctx.SaveSlotId, cancellationToken);
        if (worldState is null) return "Cannot move — world state not found.";

        try
        {
            var (newTile, newState) = await ctx.MapService.MovePlayer(ctx.SaveSlotId, worldState, direction, ctx.GameName, cancellationToken);

            newState.RecentEvents ??= [];
            newState.RecentEvents.Add($"moved {direction} to {newTile.LocationName}");
            if (newState.RecentEvents.Count > 3) newState.RecentEvents = newState.RecentEvents[^3..];
            await ctx.WorldStateService.SaveState(newState, cancellationToken);

            ctx.EventStream.Emit(new AgentEvent(
                $"🗺️ Moved {direction} → {newTile.LocationName} ({newTile.Biome})",
                "GameMaster", AgentEventKind.ToolResult));

            var tileContext = ctx.MapService.BuildTileContext(newTile, newTile.IsRevealed);
            return $"You move {direction}.\n{tileContext}";
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "MovePlayer failed: {Direction}", direction);
            return $"You cannot move {direction}: {ex.Message}";
        }
    }
}
