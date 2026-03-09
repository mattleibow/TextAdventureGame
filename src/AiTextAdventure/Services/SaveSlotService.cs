using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;

namespace AiTextAdventure.Services;

public class SaveSlotService(IDocumentStore store, ILogger<SaveSlotService> logger)
{
    public async Task<IReadOnlyList<SaveSlot>> GetSaveSlots(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: GetAll SaveSlots");
        var slots = await store.GetAll<SaveSlot>(GameJsonContext.Default.SaveSlot, cancellationToken);
        logger.LogDebug("DB: Found {Count} save slots", slots.Count);
        return slots;
    }

    public async Task<SaveSlot> CreateSaveSlot(string name, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("DB: Creating save slot '{Name}'", name);
        var slot = new SaveSlot
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTime.UtcNow,
            LastPlayedAt = DateTime.UtcNow
        };
        await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, cancellationToken);
        logger.LogInformation("DB: Created save slot {Id} '{Name}'", slot.Id, slot.Name);
        return slot;
    }

    public async Task<SaveSlot?> GetSaveSlot(Guid id, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("DB: Query SaveSlot {Id}", id);
        var all = await store.GetAll<SaveSlot>(GameJsonContext.Default.SaveSlot, cancellationToken);
        return all.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// Deletes a save slot and ALL associated data: WorldState, PlayerStats,
    /// InventoryItems, JournalEntries, and MapTiles.
    /// </summary>
    public async Task DeleteSaveSlot(Guid id, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("DB: Deleting save slot {Id} with all related data", id);

        // Delete the save slot record itself
        await store.Remove<SaveSlot>(id.ToString(), cancellationToken);

        // Delete WorldState for this slot
        var worldStates = await store.GetAll<WorldState>(GameJsonContext.Default.WorldState, cancellationToken);
        foreach (var ws in worldStates.Where(w => w.SaveSlotId == id))
        {
            await store.Remove<WorldState>(ws.Id.ToString(), cancellationToken);
            logger.LogDebug("DB: Removed WorldState {Id}", ws.Id);
        }

        // Delete PlayerStats for this slot
        var playerStats = await store.GetAll<PlayerStats>(GameJsonContext.Default.PlayerStats, cancellationToken);
        foreach (var ps in playerStats.Where(p => p.SaveSlotId == id))
        {
            await store.Remove<PlayerStats>(ps.Id.ToString(), cancellationToken);
            logger.LogDebug("DB: Removed PlayerStats {Id}", ps.Id);
        }

        // Delete InventoryItems for this slot
        var items = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        foreach (var item in items.Where(i => i.SaveSlotId == id))
        {
            await store.Remove<InventoryItem>(item.Id.ToString(), cancellationToken);
            logger.LogDebug("DB: Removed InventoryItem {Id}", item.Id);
        }

        // Delete JournalEntries for this slot
        var entries = await store.GetAll<JournalEntry>(GameJsonContext.Default.JournalEntry, cancellationToken);
        foreach (var entry in entries.Where(e => e.SaveSlotId == id))
        {
            await store.Remove<JournalEntry>(entry.Id.ToString(), cancellationToken);
            logger.LogDebug("DB: Removed JournalEntry {Id}", entry.Id);
        }

        // Delete MapTiles for this slot
        var tiles = await store.GetAll<MapTile>(GameJsonContext.Default.MapTile, cancellationToken);
        foreach (var tile in tiles.Where(t => t.SaveSlotId == id))
        {
            await store.Remove<MapTile>(tile.Id.ToString(), cancellationToken);
            logger.LogDebug("DB: Removed MapTile ({X},{Y})", tile.X, tile.Y);
        }

        logger.LogInformation("DB: Deleted all data for save slot {Id}", id);
    }

    public async Task UpdateLastPlayed(Guid id, CancellationToken cancellationToken = default)
    {
        var slot = await GetSaveSlot(id, cancellationToken);
        if (slot is null) return;
        slot.LastPlayedAt = DateTime.UtcNow;
        logger.LogDebug("DB: UpdateLastPlayed for slot {Id}", id);
        await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, cancellationToken);
    }
}
