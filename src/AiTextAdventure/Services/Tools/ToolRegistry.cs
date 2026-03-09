using Microsoft.Extensions.AI;

namespace AiTextAdventure.Services.Tools;

/// <summary>
/// Assembles all game tools into a list of <see cref="AITool"/> instances for the Game Master.
/// Created per-turn with the active save slot context bound in via <see cref="ToolContext"/>.
/// </summary>
public class ToolRegistry(ToolContext ctx)
{
    /// <summary>Returns all game tools ready for use in <see cref="ChatOptions.Tools"/>.</summary>
    public IList<AITool> GetAllTools() =>
    [
        AIFunctionFactory.Create(new GetWorldStateTool(ctx).GetWorldState),
        AIFunctionFactory.Create(new GetCurrentTileTool(ctx).GetCurrentTile),
        AIFunctionFactory.Create(new MovePlayerTool(ctx).MovePlayer),
        AIFunctionFactory.Create(new LookAroundTool(ctx).LookAround),
        AIFunctionFactory.Create(new PickUpItemTool(ctx).PickUpItem),
        AIFunctionFactory.Create(new DropItemTool(ctx).DropItem),
        AIFunctionFactory.Create(new UseItemTool(ctx).UseItem),
        AIFunctionFactory.Create(new EquipItemTool(ctx).EquipItem),
    ];
}
