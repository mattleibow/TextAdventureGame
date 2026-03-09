using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services;

/// <summary>
/// AI-callable game tools. Each method is a tool the Game Master AI can invoke to read and
/// write game state. Instances are created per-turn with the current save slot context.
/// </summary>
public class GameTools(
    Guid saveSlotId,
    string gameName,
    WorldStateService worldStateService,
    MapService mapService,
    IDocumentStore store,
    EventStream eventStream,
    ILogger logger)
{
    /// <summary>Returns all game tools as AIFunction instances for the Game Master to call.</summary>
    public IList<AITool> GetAllTools() =>
    [
        AIFunctionFactory.Create(GetWorldState),
        AIFunctionFactory.Create(GetCurrentTile),
        AIFunctionFactory.Create(MovePlayer),
        AIFunctionFactory.Create(LookAround),
        AIFunctionFactory.Create(PickUpItem),
        AIFunctionFactory.Create(DropItem),
        AIFunctionFactory.Create(UseItem),
        AIFunctionFactory.Create(EquipItem),
    ];

    [Description("Get the current world state: player location, visible items, inventory, and stats. Call this first to understand the situation before acting.")]
    public async Task<string> GetWorldState(CancellationToken cancellationToken = default)
    {
        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        var stats = await worldStateService.GetPlayerStats(saveSlotId, cancellationToken);
        var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var inventory = allItems.Where(i => i.SaveSlotId == saveSlotId).ToList();

        var sb = new System.Text.StringBuilder();
        if (worldState is not null)
        {
            sb.AppendLine($"LOCATION: {worldState.CurrentLocation} ({worldState.CurrentBiome}) at ({worldState.PlayerX},{worldState.PlayerY})");
            sb.AppendLine($"TIME: {worldState.TimeOfDay}");
            sb.AppendLine();

            var portable = worldState.KnownEntities ?? [];
            sb.AppendLine(portable.Count > 0
                ? $"PORTABLE ITEMS (can be picked up): {string.Join(", ", portable)}"
                : "PORTABLE ITEMS: none visible yet (use look_around to search)");

            var landmarks = worldState.LandmarkEntities ?? [];
            if (landmarks.Count > 0)
                sb.AppendLine($"LANDMARKS (examine only, cannot be picked up): {string.Join(", ", landmarks)}");

            var hidden = worldState.HiddenEntities ?? [];
            if (hidden.Count > 0)
                sb.AppendLine($"HIDDEN: {hidden.Count} undiscovered item(s) — call look_around to reveal them");
        }
        else
        {
            sb.AppendLine("LOCATION: unknown");
        }

        sb.AppendLine();
        sb.AppendLine(inventory.Count > 0
            ? $"INVENTORY: {string.Join(", ", inventory.Select(i => $"{i.ItemName} (effect: {(string.IsNullOrEmpty(i.Effect) ? "none" : i.Effect)})" ))}"
            : "INVENTORY: empty");

        if (stats is not null)
        {
            sb.AppendLine($"STATS: HP:{stats.Health}/{stats.MaxHealth} | Hunger:{stats.Hunger}/100 | Tiredness:{stats.Tiredness}/100 | Armor:{stats.Armor}");
            if (!string.IsNullOrEmpty(stats.EquippedWeapon)) sb.AppendLine($"WEAPON: {stats.EquippedWeapon}");
            if (!string.IsNullOrEmpty(stats.EquippedArmor)) sb.AppendLine($"ARMOR: {stats.EquippedArmor}");
        }

        if (worldState?.RecentEvents?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"RECENT EVENTS: {string.Join(" → ", worldState.RecentEvents)}");
        }

        var result = sb.ToString().Trim();
        eventStream.Emit(new AgentEvent("🌍 get_world_state", "GameMaster", AgentEventKind.ToolResult,
            result[..Math.Min(100, result.Length)]));
        return result;
    }

    [Description("Get the detailed description of the current map tile: biome, atmosphere, visible features, and exits.")]
    public async Task<string> GetCurrentTile(CancellationToken cancellationToken = default)
    {
        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is null) return "No tile data available.";

        var tile = await mapService.GetTile(saveSlotId, worldState.PlayerX, worldState.PlayerY, cancellationToken);
        if (tile is null) return $"You stand in {worldState.CurrentBiome} terrain.";

        var context = mapService.BuildTileContext(tile, tile.IsRevealed);
        eventStream.Emit(new AgentEvent("🗺️ get_current_tile", "GameMaster", AgentEventKind.ToolResult,
            context[..Math.Min(100, context.Length)]));
        return context;
    }

    [Description("Move the player in a direction. Valid: north, south, east, west, northeast, northwest, southeast, southwest. Generates new tiles as needed. Returns the new location description.")]
    public async Task<string> MovePlayer(
        [Description("Direction to move: north, south, east, west, northeast, northwest, southeast, or southwest.")] string direction,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(direction)) return "No direction specified.";

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is null) return "Cannot move — no world state found.";

        try
        {
            var (newTile, newState) = await mapService.MovePlayer(saveSlotId, worldState, direction, gameName, cancellationToken);

            newState.RecentEvents ??= [];
            newState.RecentEvents.Add($"moved {direction} to {newTile.LocationName}");
            if (newState.RecentEvents.Count > 3) newState.RecentEvents = newState.RecentEvents[^3..];
            await worldStateService.SaveState(newState, cancellationToken);

            var context = mapService.BuildTileContext(newTile, newTile.IsRevealed);
            eventStream.Emit(new AgentEvent($"🗺️ Moved {direction} → {newTile.LocationName} ({newTile.Biome})", "GameMaster", AgentEventKind.ToolResult));
            return $"You move {direction}.\n{context}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MovePlayer tool failed: {Direction}", direction);
            return $"You cannot move {direction}: {ex.Message}";
        }
    }

    [Description("Search the current location carefully to reveal hidden items and discover what is here. Call this when the player wants to look around, search, or explore.")]
    public async Task<string> LookAround(CancellationToken cancellationToken = default)
    {
        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is null) return "Nothing to examine.";

        var hiddenBefore = worldState.HiddenEntities?.Count ?? 0;
        await mapService.RevealTile(saveSlotId, worldState.PlayerX, worldState.PlayerY, worldState, cancellationToken);

        worldState.RecentEvents ??= [];
        worldState.RecentEvents.Add("searched the area");
        if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
        await worldStateService.SaveState(worldState, cancellationToken);

        var discovered = worldState.KnownEntities ?? [];
        eventStream.Emit(new AgentEvent($"🔍 look_around: {discovered.Count} items visible", "GameMaster", AgentEventKind.ToolResult));

        if (hiddenBefore > 0 && discovered.Count > 0)
            return $"You search the area carefully and discover: {string.Join(", ", discovered)}. These items can be picked up.";
        if (discovered.Count > 0)
            return $"You look around. Visible items here: {string.Join(", ", discovered)}.";
        return "You search the area carefully but find nothing new here.";
    }

    [Description("Pick up a portable item from the current location and add it to the player's inventory. Only pick up items listed in PORTABLE ITEMS from get_world_state.")]
    public async Task<string> PickUpItem(
        [Description("The exact item name from the PORTABLE ITEMS list in get_world_state.")] string itemName,
        [Description("One sentence describing the item.")] string description,
        [Description("Game effect: 'heal:N' (restore N health), 'food:N' (reduce hunger by N), 'weapon:N' (N attack power), 'armor:N' (N defense), 'poison:N' (deals N damage on contact), or empty string for misc items.")] string effect,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item specified.";

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is null) return "Cannot pick up — no world state found.";

        // Verify the item is actually in the world
        var match = (worldState.KnownEntities ?? [])
            .FirstOrDefault(e => e.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            logger.LogInformation("PickUpItem blocked — '{Item}' not in KnownEntities", itemName);
            return $"'{itemName}' is not here or cannot be picked up. Check PORTABLE ITEMS in get_world_state.";
        }

        // Poison: apply damage on contact, don't add to inventory
        if (!string.IsNullOrEmpty(effect) && effect.StartsWith("poison", StringComparison.OrdinalIgnoreCase))
        {
            var dmgStats = await worldStateService.GetPlayerStats(saveSlotId, cancellationToken)
                          ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };
            ApplyEffect(dmgStats, effect);
            await worldStateService.SavePlayerStats(dmgStats, cancellationToken);

            worldState.KnownEntities!.RemoveAll(e => e.Equals(match, StringComparison.OrdinalIgnoreCase));
            await RemoveFromTile(worldState, match, cancellationToken);
            await SaveRecentEvent(worldState, $"touched {match} — took damage!", cancellationToken);

            eventStream.Emit(new AgentEvent($"☠️ poison contact: {match}", "GameMaster", AgentEventKind.ToolResult, $"HP: {dmgStats.Health}"));
            return $"You reach for {match} — it's venomous! You take damage (HP: {dmgStats.Health}/{dmgStats.MaxHealth}). It falls away.";
        }

        // Add to inventory
        var invItem = new InventoryItem
        {
            Id = Guid.NewGuid(), SaveSlotId = saveSlotId,
            ItemName = match, Description = description, Quantity = 1,
            Effect = effect ?? "",
            IsConsumable = effect?.StartsWith("heal", StringComparison.OrdinalIgnoreCase) == true
                        || effect?.StartsWith("food", StringComparison.OrdinalIgnoreCase) == true,
            IsEquippable = effect?.StartsWith("weapon", StringComparison.OrdinalIgnoreCase) == true
                        || effect?.StartsWith("armor", StringComparison.OrdinalIgnoreCase) == true,
        };
        await store.Set(invItem.Id.ToString(), invItem, GameJsonContext.Default.InventoryItem, cancellationToken);

        // Remove from world
        worldState.KnownEntities!.RemoveAll(e => e.Equals(match, StringComparison.OrdinalIgnoreCase));
        await RemoveFromTile(worldState, match, cancellationToken);
        await SaveRecentEvent(worldState, $"picked up {match}", cancellationToken);

        eventStream.Emit(new AgentEvent($"🎒 +{match} [{effect}]", "GameMaster", AgentEventKind.ToolResult));
        return $"You pick up the {match} and add it to your inventory.";
    }

    [Description("Drop an item from the player's inventory at the current location.")]
    public async Task<string> DropItem(
        [Description("Exact item name from the INVENTORY list in get_world_state.")] string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item specified.";

        var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == saveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";

        await store.Remove<InventoryItem>(invItem.Id.ToString(), cancellationToken);

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is not null)
        {
            worldState.KnownEntities ??= [];
            if (!worldState.KnownEntities.Contains(invItem.ItemName, StringComparer.OrdinalIgnoreCase))
                worldState.KnownEntities.Add(invItem.ItemName);
            await SaveRecentEvent(worldState, $"dropped {invItem.ItemName}", cancellationToken);
        }

        eventStream.Emit(new AgentEvent($"🗑 -{invItem.ItemName}", "GameMaster", AgentEventKind.ToolResult));
        return $"You drop the {invItem.ItemName}.";
    }

    [Description("Use or consume an item from the player's inventory (eat food, drink potion, apply salve). Removes consumable items after use.")]
    public async Task<string> UseItem(
        [Description("Exact item name from the INVENTORY list in get_world_state.")] string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item specified.";

        var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == saveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";
        if (!invItem.IsConsumable)
            return $"'{itemName}' cannot be consumed. Try equip_item if it's a weapon or armor.";

        var stats = await worldStateService.GetPlayerStats(saveSlotId, cancellationToken)
                   ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };
        var beforeHp = stats.Health;
        var beforeHunger = stats.Hunger;
        ApplyEffect(stats, invItem.Effect);
        await worldStateService.SavePlayerStats(stats, cancellationToken);
        await store.Remove<InventoryItem>(invItem.Id.ToString(), cancellationToken);

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is not null)
            await SaveRecentEvent(worldState, $"used {invItem.ItemName}", cancellationToken);

        eventStream.Emit(new AgentEvent($"✨ used: {invItem.ItemName}", "GameMaster", AgentEventKind.ToolResult,
            $"HP:{beforeHp}→{stats.Health} Hunger:{beforeHunger}→{stats.Hunger}"));
        return $"You use the {invItem.ItemName}. HP: {beforeHp} → {stats.Health}. Hunger: {beforeHunger} → {stats.Hunger}.";
    }

    [Description("Equip a weapon or armor from the player's inventory as active gear.")]
    public async Task<string> EquipItem(
        [Description("Exact item name of the weapon or armor from the INVENTORY list in get_world_state.")] string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return "No item specified.";

        var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
        var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == saveSlotId &&
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (invItem is null) return $"'{itemName}' is not in your inventory.";
        if (!invItem.IsEquippable)
            return $"'{itemName}' cannot be equipped. Try use_item if it's consumable.";

        var stats = await worldStateService.GetPlayerStats(saveSlotId, cancellationToken)
                   ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };

        string resultMsg;
        if (invItem.Effect.StartsWith("weapon", StringComparison.OrdinalIgnoreCase))
        {
            stats.EquippedWeapon = invItem.ItemName;
            resultMsg = $"You equip the {invItem.ItemName} as your weapon.";
            eventStream.Emit(new AgentEvent($"⚔️ equipped: {invItem.ItemName}", "GameMaster", AgentEventKind.ToolResult));
        }
        else
        {
            stats.EquippedArmor = invItem.ItemName;
            if (int.TryParse(invItem.Effect.AsSpan(6), out var armorVal)) stats.Armor = armorVal;
            resultMsg = $"You equip the {invItem.ItemName} (+{stats.Armor} armor).";
            eventStream.Emit(new AgentEvent($"🛡️ equipped: {invItem.ItemName} (+{stats.Armor})", "GameMaster", AgentEventKind.ToolResult));
        }

        await worldStateService.SavePlayerStats(stats, cancellationToken);

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is not null)
            await SaveRecentEvent(worldState, $"equipped {invItem.ItemName}", cancellationToken);

        return resultMsg;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void ApplyEffect(PlayerStats stats, string effect)
    {
        if (string.IsNullOrEmpty(effect)) return;
        var colon = effect.IndexOf(':');
        var kind = colon >= 0 ? effect[..colon] : effect;
        var value = colon >= 0 && int.TryParse(effect[(colon + 1)..], out var v) ? v : 0;
        switch (kind.ToLowerInvariant())
        {
            case "heal":   stats.Health = Math.Min(stats.MaxHealth, stats.Health + value); break;
            case "food":   stats.Hunger = Math.Max(0, stats.Hunger - value); break;
            case "poison": case "danger": stats.Health = Math.Max(0, stats.Health - Math.Max(1, value - stats.Armor)); break;
        }
    }

    private async Task RemoveFromTile(WorldState worldState, string itemName, CancellationToken ct)
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

    private async Task SaveRecentEvent(WorldState worldState, string eventText, CancellationToken ct)
    {
        worldState.RecentEvents ??= [];
        worldState.RecentEvents.Add(eventText);
        if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
        await worldStateService.SaveState(worldState, ct);
    }
}
