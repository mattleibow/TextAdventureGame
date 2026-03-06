using AiTextAdventure.Models.Documents;
using GameLocation = AiTextAdventure.Models.Documents.Location;

namespace AiTextAdventure.Services;

public interface IWorldStateService
{
    Task<WorldState?> GetCurrentState(Guid saveSlotId, CancellationToken cancellationToken = default);
    Task SaveState(WorldState state, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameLocation>> GetDiscoveredLocations(Guid saveSlotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Npc>> GetNpcsAtLocation(Guid saveSlotId, Guid locationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryItem>> GetInventory(Guid saveSlotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> GetJournalEntries(Guid saveSlotId, CancellationToken cancellationToken = default);
}
