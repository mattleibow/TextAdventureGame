using AiTextAdventure.Models.Documents;
using AiTextAdventure.Models;
using Shiny.SqliteDocumentDb;
using GameLocation = AiTextAdventure.Models.Documents.Location;

namespace AiTextAdventure.Services;

public class WorldStateService(IDocumentStore store)
{
    public async Task<WorldState?> GetCurrentState(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        var results = await store.Query<WorldState>(
            w => w.SaveSlotId == saveSlotId,
            GameJsonContext.Default.WorldState,
            cancellationToken);
        return results.FirstOrDefault();
    }

    public Task SaveState(WorldState state, CancellationToken cancellationToken = default) =>
        store.Set(state.Id.ToString(), state, GameJsonContext.Default.WorldState, cancellationToken);

    public async Task<IReadOnlyList<GameLocation>> GetDiscoveredLocations(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        var results = await store.Query<GameLocation>(
            l => l.SaveSlotId == saveSlotId,
            GameJsonContext.Default.Location,
            cancellationToken);
        return [.. results.OrderByDescending(l => l.DiscoveredAt)];
    }

    public Task<IReadOnlyList<Npc>> GetNpcsAtLocation(Guid saveSlotId, Guid locationId, CancellationToken cancellationToken = default) =>
        store.Query<Npc>(
            n => n.SaveSlotId == saveSlotId && n.LocationId == locationId,
            GameJsonContext.Default.Npc,
            cancellationToken);

    public Task<IReadOnlyList<InventoryItem>> GetInventory(Guid saveSlotId, CancellationToken cancellationToken = default) =>
        store.Query<InventoryItem>(
            i => i.SaveSlotId == saveSlotId,
            GameJsonContext.Default.InventoryItem,
            cancellationToken);

    public async Task<IReadOnlyList<JournalEntry>> GetJournalEntries(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        var results = await store.Query<JournalEntry>(
            j => j.SaveSlotId == saveSlotId,
            GameJsonContext.Default.JournalEntry,
            cancellationToken);
        return [.. results.OrderByDescending(j => j.Timestamp)];
    }
}
