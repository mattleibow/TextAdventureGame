using AiTextAdventure.Models;

namespace AiTextAdventure.Services;

public interface IGameOrchestrator
{
    Task<GameTurnResult> ProcessTurnAsync(
        Guid saveSlotId,
        string playerInput,
        CancellationToken cancellationToken = default);
}

public record GameTurnResult(string Narrative, List<SuggestedAction> Suggestions);
