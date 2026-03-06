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

    public async Task DeleteSaveSlot(Guid id, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("DB: Delete SaveSlot {Id}", id);
        await store.Remove<SaveSlot>(id.ToString(), cancellationToken);
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
