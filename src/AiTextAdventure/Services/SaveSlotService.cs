using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using Shiny.SqliteDocumentDb;

namespace AiTextAdventure.Services;

public class SaveSlotService(IDocumentStore store) : ISaveSlotService
{
    public async Task<IReadOnlyList<SaveSlot>> GetSaveSlots(CancellationToken cancellationToken = default) =>
        await store.GetAll<SaveSlot>(GameJsonContext.Default.SaveSlot, cancellationToken);

    public async Task<SaveSlot> CreateSaveSlot(string name, CancellationToken cancellationToken = default)
    {
        var slot = new SaveSlot
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTime.UtcNow,
            LastPlayedAt = DateTime.UtcNow
        };
        await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, cancellationToken);
        return slot;
    }

    public async Task<SaveSlot?> GetSaveSlot(Guid id, CancellationToken cancellationToken = default)
    {
        var results = await store.Query<SaveSlot>(
            s => s.Id == id,
            GameJsonContext.Default.SaveSlot,
            cancellationToken);
        return results.FirstOrDefault();
    }

    public Task DeleteSaveSlot(Guid id, CancellationToken cancellationToken = default) =>
        store.Remove<SaveSlot>(id.ToString(), cancellationToken);

    public async Task UpdateLastPlayed(Guid id, CancellationToken cancellationToken = default)
    {
        var slot = await GetSaveSlot(id, cancellationToken);
        if (slot is null) return;
        slot.LastPlayedAt = DateTime.UtcNow;
        await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, cancellationToken);
    }
}
