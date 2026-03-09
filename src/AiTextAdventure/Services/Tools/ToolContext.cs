using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Tools;

/// <summary>
/// Shared context and helper methods available to all game tools.
/// Created per-turn with the active save slot bound in.
/// </summary>
public class ToolContext(
    Guid saveSlotId,
    string gameName,
    WorldStateService worldStateService,
    MapService mapService,
    IDocumentStore store,
    EventStream eventStream,
    ILogger logger)
{
    public Guid SaveSlotId => saveSlotId;
    public string GameName => gameName;
    public WorldStateService WorldStateService => worldStateService;
    public MapService MapService => mapService;
    public IDocumentStore Store => store;
    public EventStream EventStream => eventStream;
    public ILogger Logger => logger;

    /// <summary>Applies a game effect string (heal:N, food:N, weapon:N, armor:N, poison:N) to player stats.</summary>
    public static void ApplyEffect(PlayerStats stats, string effect)
    {
        if (string.IsNullOrEmpty(effect)) return;
        var colon = effect.IndexOf(':');
        var kind = colon >= 0 ? effect[..colon] : effect;
        var value = colon >= 0 && int.TryParse(effect[(colon + 1)..], out var v) ? v : 0;
        switch (kind.ToLowerInvariant())
        {
            case "heal":   stats.Health = Math.Min(stats.MaxHealth, stats.Health + value); break;
            case "food":   stats.Hunger = Math.Max(0, stats.Hunger - value); break;
            case "poison":
            case "danger": stats.Health = Math.Max(0, stats.Health - Math.Max(1, value - stats.Armor)); break;
        }
    }

    /// <summary>Removes a named item from the persistent MapTile (features and hidden items).</summary>
    public async Task RemoveFromTile(WorldState worldState, string itemName, CancellationToken ct)
    {
        var tile = await mapService.GetTile(saveSlotId, worldState.PlayerX, worldState.PlayerY, ct);
        if (tile is null) return;
        var changed = false;
        var fi = tile.Features.FindIndex(f => f.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (fi >= 0) { tile.Features.RemoveAt(fi); changed = true; }
        var hi = tile.HiddenItems.FindIndex(h => h.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (hi >= 0) { tile.HiddenItems.RemoveAt(hi); changed = true; }
        if (changed) await store.Set(tile.Id.ToString(), tile, GameJsonContext.Default.MapTile, ct);
    }

    /// <summary>Appends an event to WorldState.RecentEvents (capped at 3) and saves.</summary>
    public async Task SaveRecentEvent(WorldState worldState, string eventText, CancellationToken ct)
    {
        worldState.RecentEvents ??= [];
        worldState.RecentEvents.Add(eventText);
        if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
        await worldStateService.SaveState(worldState, ct);
    }
}
