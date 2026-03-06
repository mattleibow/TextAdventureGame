using AiTextAdventure.Models.Documents;
using AiTextAdventure.Models;
using AiTextAdventure.Services.Observability;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using GameLocation = AiTextAdventure.Models.Documents.Location;

namespace AiTextAdventure.Services;

public class WorldStateService(
    IDocumentStore store,
    EventStream eventStream,
    ILogger<WorldStateService> logger)
{
    public async Task<WorldState?> GetCurrentState(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: Query WorldState for save {SaveSlotId}", saveSlotId);
        // Use GetAll + in-memory filter: Shiny's LINQ-to-SQL doesn't handle Guid comparisons correctly
        var all = await store.GetAll<WorldState>(GameJsonContext.Default.WorldState, cancellationToken);
        var state = all.FirstOrDefault(w => w.SaveSlotId == saveSlotId);
        if (state is not null)
            logger.LogDebug("DB: Found WorldState {Location}/{Biome}", state.CurrentLocation, state.CurrentBiome);
        else
            logger.LogDebug("DB: No WorldState found for save {SaveSlotId}", saveSlotId);
        return state;
    }

    public async Task SaveState(WorldState state, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: Save WorldState {Location}/{Biome} for save {SaveSlotId}", state.CurrentLocation, state.CurrentBiome, state.SaveSlotId);
        eventStream.Emit(new AgentEvent($"💾 {state.CurrentLocation} ({state.CurrentBiome})", "Database", AgentEventKind.ToolResult,
            $"Saved world state: {state.TimeOfDay}, {state.KnownEntities?.Count ?? 0} entities"));
        await store.Set(state.Id.ToString(), state, GameJsonContext.Default.WorldState, cancellationToken);
    }

    public async Task<PlayerStats?> GetPlayerStats(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: Query PlayerStats for save {SaveSlotId}", saveSlotId);
        var all = await store.GetAll<PlayerStats>(GameJsonContext.Default.PlayerStats, cancellationToken);
        return all.FirstOrDefault(p => p.SaveSlotId == saveSlotId);
    }

    public async Task SavePlayerStats(PlayerStats stats, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: Save PlayerStats HP={Health}/{MaxHealth} Hunger={Hunger} Tired={Tired} for {SaveSlotId}",
            stats.Health, stats.MaxHealth, stats.Hunger, stats.Tiredness, stats.SaveSlotId);
        eventStream.Emit(new AgentEvent(
            $"💾 Stats HP:{stats.Health} Hunger:{stats.Hunger} Energy:{100 - stats.Tiredness}",
            "Database", AgentEventKind.ToolResult));
        await store.Set(stats.Id.ToString(), stats, GameJsonContext.Default.PlayerStats, cancellationToken);
    }

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

    public async Task<IReadOnlyList<InventoryItem>> GetInventory(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: Query Inventory for save {SaveSlotId}", saveSlotId);
        var all = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var items = all.Where(i => i.SaveSlotId == saveSlotId).ToList();
        logger.LogDebug("DB: Found {Count} inventory items", items.Count);
        return items;
    }

    public async Task<IReadOnlyList<JournalEntry>> GetJournalEntries(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: Query JournalEntries for save {SaveSlotId}", saveSlotId);
        var all = await store.GetAll<JournalEntry>(GameJsonContext.Default.JournalEntry, cancellationToken);
        var ordered = all.Where(j => j.SaveSlotId == saveSlotId)
                         .OrderByDescending(j => j.Timestamp).ToList();
        logger.LogDebug("DB: Found {Count} journal entries", ordered.Count);
        return ordered;
    }
}
