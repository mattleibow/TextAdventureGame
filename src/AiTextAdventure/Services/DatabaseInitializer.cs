using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using Shiny.SqliteDocumentDb;
using GameLocation = AiTextAdventure.Models.Documents.Location;

namespace AiTextAdventure.Services;

/// <summary>
/// Creates SQLite indexes on document store collections at startup.
/// Call once during app initialization before any queries are made.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task CreateIndexes(SqliteDocumentStore store)
    {
        await store.CreateIndexAsync<Npc>(n => n.SaveSlotId, GameJsonContext.Default.Npc);
        await store.CreateIndexAsync<Npc>(n => n.LocationId, GameJsonContext.Default.Npc);
        await store.CreateIndexAsync<GameLocation>(l => l.SaveSlotId, GameJsonContext.Default.Location);
        await store.CreateIndexAsync<WorldState>(w => w.SaveSlotId, GameJsonContext.Default.WorldState);
        await store.CreateIndexAsync<JournalEntry>(j => j.SaveSlotId, GameJsonContext.Default.JournalEntry);
        await store.CreateIndexAsync<InventoryItem>(i => i.SaveSlotId, GameJsonContext.Default.InventoryItem);
    }
}
