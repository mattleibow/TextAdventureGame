using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.SqliteDocumentDb;

namespace AiTextAdventure.Tests;

/// <summary>
/// Verifies that deleting a save slot cascades to remove all related documents:
/// WorldState, PlayerStats, InventoryItems, JournalEntries, and MapTiles.
/// Uses minimal in-process document types mirroring the real app models.
/// </summary>
public class SaveSlotDeletionTests : IDisposable
{
    // ── Minimal document mirrors (the real types live in the MAUI project) ──

    private class SaveSlot    { public Guid Id { get; set; } public string Name { get; set; } = ""; }
    private class WorldState  { public Guid Id { get; set; } public Guid SaveSlotId { get; set; } }
    private class PlayerStats { public Guid Id { get; set; } public Guid SaveSlotId { get; set; } }
    private class InventoryItem { public Guid Id { get; set; } public Guid SaveSlotId { get; set; } public string ItemName { get; set; } = ""; }
    private class JournalEntry  { public Guid Id { get; set; } public Guid SaveSlotId { get; set; } public string EntryText { get; set; } = ""; }
    private class MapTile       { public Guid Id { get; set; } public Guid SaveSlotId { get; set; } public int X { get; set; } public int Y { get; set; } }

    private readonly SqliteDocumentStore _store;
    private readonly string _dbPath;

    public SaveSlotDeletionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"del_test_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSqliteDocumentStore(opts => opts.ConnectionString = $"Data Source={_dbPath}");
        var provider = services.BuildServiceProvider();
        _store = (SqliteDocumentStore)provider.GetRequiredService<IDocumentStore>();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>
    /// Simulates the cascade delete logic from SaveSlotService.DeleteSaveSlot().
    /// In the real app this lives in SaveSlotService; here we replicate the
    /// same GetAll+LINQ+Remove pattern to verify it works correctly.
    /// </summary>
    private async Task DeleteSaveSlotCascade(Guid slotId)
    {
        await _store.Remove<SaveSlot>(slotId.ToString());

        var worldStates = await _store.GetAll<WorldState>();
        foreach (var ws in worldStates.Where(w => w.SaveSlotId == slotId))
            await _store.Remove<WorldState>(ws.Id.ToString());

        var stats = await _store.GetAll<PlayerStats>();
        foreach (var ps in stats.Where(p => p.SaveSlotId == slotId))
            await _store.Remove<PlayerStats>(ps.Id.ToString());

        var items = await _store.GetAll<InventoryItem>();
        foreach (var item in items.Where(i => i.SaveSlotId == slotId))
            await _store.Remove<InventoryItem>(item.Id.ToString());

        var entries = await _store.GetAll<JournalEntry>();
        foreach (var entry in entries.Where(e => e.SaveSlotId == slotId))
            await _store.Remove<JournalEntry>(entry.Id.ToString());

        var tiles = await _store.GetAll<MapTile>();
        foreach (var tile in tiles.Where(t => t.SaveSlotId == slotId))
            await _store.Remove<MapTile>(tile.Id.ToString());
    }

    [Fact]
    public async Task DeleteSaveSlot_RemovesSlotAndAllRelatedDocuments()
    {
        // Arrange – create a target slot and seed data
        var targetId = Guid.NewGuid();
        var keepId   = Guid.NewGuid();

        var targetSlot = new SaveSlot { Id = targetId, Name = "Target Game" };
        var keepSlot   = new SaveSlot { Id = keepId,   Name = "Keep Game" };
        await _store.Set(targetId.ToString(), targetSlot);
        await _store.Set(keepId.ToString(),   keepSlot);

        // Target slot data
        var ws   = new WorldState  { Id = Guid.NewGuid(), SaveSlotId = targetId };
        var ps   = new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = targetId };
        var inv1 = new InventoryItem { Id = Guid.NewGuid(), SaveSlotId = targetId, ItemName = "rusty sword" };
        var inv2 = new InventoryItem { Id = Guid.NewGuid(), SaveSlotId = targetId, ItemName = "healing potion" };
        var je   = new JournalEntry  { Id = Guid.NewGuid(), SaveSlotId = targetId, EntryText = "Arrived at glade" };
        var mt1  = new MapTile { Id = Guid.NewGuid(), SaveSlotId = targetId, X = 0, Y = 0 };
        var mt2  = new MapTile { Id = Guid.NewGuid(), SaveSlotId = targetId, X = 1, Y = 0 };
        await _store.Set(ws.Id.ToString(),   ws);
        await _store.Set(ps.Id.ToString(),   ps);
        await _store.Set(inv1.Id.ToString(), inv1);
        await _store.Set(inv2.Id.ToString(), inv2);
        await _store.Set(je.Id.ToString(),   je);
        await _store.Set(mt1.Id.ToString(),  mt1);
        await _store.Set(mt2.Id.ToString(),  mt2);

        // Keep slot data (should survive)
        var keepWs = new WorldState { Id = Guid.NewGuid(), SaveSlotId = keepId };
        var keepInv = new InventoryItem { Id = Guid.NewGuid(), SaveSlotId = keepId, ItemName = "keep item" };
        await _store.Set(keepWs.Id.ToString(),  keepWs);
        await _store.Set(keepInv.Id.ToString(), keepInv);

        // Act
        await DeleteSaveSlotCascade(targetId);

        // Assert – target slot and all its data are gone
        var slots       = await _store.GetAll<SaveSlot>();
        var worldStates = await _store.GetAll<WorldState>();
        var allStats    = await _store.GetAll<PlayerStats>();
        var allItems    = await _store.GetAll<InventoryItem>();
        var allEntries  = await _store.GetAll<JournalEntry>();
        var allTiles    = await _store.GetAll<MapTile>();

        Assert.DoesNotContain(slots,       s => s.Id == targetId);
        Assert.DoesNotContain(worldStates, w => w.SaveSlotId == targetId);
        Assert.DoesNotContain(allStats,    p => p.SaveSlotId == targetId);
        Assert.DoesNotContain(allItems,    i => i.SaveSlotId == targetId);
        Assert.DoesNotContain(allEntries,  e => e.SaveSlotId == targetId);
        Assert.DoesNotContain(allTiles,    t => t.SaveSlotId == targetId);

        // Assert – keep slot and its data are untouched
        Assert.Contains(slots,       s => s.Id == keepId);
        Assert.Contains(worldStates, w => w.SaveSlotId == keepId);
        Assert.Contains(allItems,    i => i.SaveSlotId == keepId);
    }

    [Fact]
    public async Task DeleteSaveSlot_WhenSlotHasNoRelatedData_Succeeds()
    {
        // Arrange – slot with no game data
        var slotId = Guid.NewGuid();
        var slot = new SaveSlot { Id = slotId, Name = "Empty Game" };
        await _store.Set(slotId.ToString(), slot);

        // Act – should not throw
        await DeleteSaveSlotCascade(slotId);

        // Assert
        var slots = await _store.GetAll<SaveSlot>();
        Assert.DoesNotContain(slots, s => s.Id == slotId);
    }

    [Fact]
    public async Task DeleteSaveSlot_DoesNotAffectOtherSlots_WithSameDocumentTypes()
    {
        // Arrange – two slots each with inventory items
        var slotA = Guid.NewGuid();
        var slotB = Guid.NewGuid();

        await _store.Set(slotA.ToString(), new SaveSlot { Id = slotA, Name = "A" });
        await _store.Set(slotB.ToString(), new SaveSlot { Id = slotB, Name = "B" });

        var itemA = new InventoryItem { Id = Guid.NewGuid(), SaveSlotId = slotA, ItemName = "sword" };
        var itemB = new InventoryItem { Id = Guid.NewGuid(), SaveSlotId = slotB, ItemName = "potion" };
        var tileA = new MapTile { Id = Guid.NewGuid(), SaveSlotId = slotA, X = 0, Y = 0 };
        var tileB = new MapTile { Id = Guid.NewGuid(), SaveSlotId = slotB, X = 0, Y = 0 };

        await _store.Set(itemA.Id.ToString(), itemA);
        await _store.Set(itemB.Id.ToString(), itemB);
        await _store.Set(tileA.Id.ToString(), tileA);
        await _store.Set(tileB.Id.ToString(), tileB);

        // Act – delete only slot A
        await DeleteSaveSlotCascade(slotA);

        // Assert – slot B's data intact
        var items = await _store.GetAll<InventoryItem>();
        var tiles = await _store.GetAll<MapTile>();

        Assert.DoesNotContain(items, i => i.SaveSlotId == slotA);
        Assert.DoesNotContain(tiles, t => t.SaveSlotId == slotA);
        Assert.Contains(items, i => i.SaveSlotId == slotB);
        Assert.Contains(tiles, t => t.SaveSlotId == slotB);
    }
}
