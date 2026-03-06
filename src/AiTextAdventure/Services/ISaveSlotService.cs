using AiTextAdventure.Models.Documents;

namespace AiTextAdventure.Services;

public interface ISaveSlotService
{
    Task<IReadOnlyList<SaveSlot>> GetSaveSlots(CancellationToken cancellationToken = default);
    Task<SaveSlot> CreateSaveSlot(string name, CancellationToken cancellationToken = default);
    Task<SaveSlot?> GetSaveSlot(Guid id, CancellationToken cancellationToken = default);
    Task DeleteSaveSlot(Guid id, CancellationToken cancellationToken = default);
    Task UpdateLastPlayed(Guid id, CancellationToken cancellationToken = default);
}
