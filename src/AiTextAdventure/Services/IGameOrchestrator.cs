using AiTextAdventure.Models;

namespace AiTextAdventure.Services;

public interface IGameOrchestrator
{
    /// <summary>
    /// Called once when a game session starts. Creates initial WorldState if missing,
    /// returns an opening narrative and suggested actions.
    /// </summary>
    Task<GameTurnResult> InitializeGameAsync(
        Guid saveSlotId,
        CancellationToken cancellationToken = default);

    Task<GameTurnResult> ProcessTurnAsync(
        Guid saveSlotId,
        string playerInput,
        CancellationToken cancellationToken = default);
}

public record GameTurnResult(string Narrative, List<SuggestedAction> Suggestions);
