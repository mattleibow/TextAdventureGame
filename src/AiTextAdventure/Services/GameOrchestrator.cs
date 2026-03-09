using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services;

public record GameTurnResult(string Narrative, List<SuggestedAction> Suggestions);

/// <summary>
/// Orchestrates a single player turn.
/// Turn flow: [Movement? → MapService] → Narrator (tile-grounded) → ActionResolver → stats → Suggestion
/// The world is persistent: tile data is generated once and reused across sessions.
/// </summary>
public class GameOrchestrator(
    IChatClient chatClient,
    WorldStateService worldStateService,
    SaveSlotService saveSlotService,
    MapService mapService,
    IDocumentStore store,
    EventStream eventStream,
    ILogger<GameOrchestrator> logger)
{
    // Narrator: uses pre-generated tile data so it never invents locations or items.
    private const string NarratorSystemPrompt = """
        You are a text adventure narrator. Write vivid second-person present-tense prose.
        2-3 sentences MAXIMUM. Use ONLY the location data and events provided — never invent.
        Describe atmospheric mood (sounds, smells, light) and react to what just happened.
        Pure prose. No lists, headings, or game mechanics language.
        """;

    // ActionResolver: the AI determines everything about the action — movement, items, exploring.
    // All names MUST come verbatim from the provided entity lists.
    private const string ActionResolverSystemPrompt = """
        You are the action resolver for a text adventure game. Determine what the player is doing.
        You are given exact lists of what exists in the world right now.
        Copy ALL names VERBATIM from the provided lists — never paraphrase, abbreviate, or invent.
        If the player says "pick up sword" and the list has "weathered iron sword", use "weathered iron sword".
        Set MovementDirection to the direction string if the player is moving.
        Set IsExploring to true if the player is looking around, searching, or examining the area.
        For ItemsPickedUp, include the item's Effect: heal:N, food:N, weapon:N, armor:N, poison:N.
        If the player picks up something dangerous (poison, venomous creature, trap), put it in both
        ItemsPickedUp and ItemsUsed with a poison:N effect, representing the danger of handling it.
        """;

    private const string SuggestionSystemPrompt = """
        You are a game assistant for a text adventure. Suggest exactly 3 short player actions.
        Include at least one movement direction (go north/south/east/west) and one interaction.
        If the player has not looked around yet, include "look around carefully".
        Reference specific items or features from context when available.
        Labels should be 2-4 words. Action text should be 4-8 words.
        """;

    public async Task<GameTurnResult> InitializeGameAsync(
        Guid saveSlotId,
        CancellationToken cancellationToken = default)
    {
        var slot = await saveSlotService.GetSaveSlot(saveSlotId, cancellationToken);
        var gameName = slot?.Name ?? "Adventure";
        logger.LogInformation("Initializing game for save {SaveSlotId} (name: {GameName})", saveSlotId, gameName);

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is null)
        {
            eventStream.Emit(new AgentEvent($"New game: {gameName}", "GameMaster", AgentEventKind.AgentInvoked));

            // Generate the starting 3×3 tile grid
            await mapService.GenerateSurroundingTiles(saveSlotId, 0, 0, gameName, cancellationToken);

            // Build WorldState from the starting tile
            var startTile = await mapService.GetTile(saveSlotId, 0, 0, cancellationToken);
            startTile ??= new MapTile { Id = Guid.NewGuid(), SaveSlotId = saveSlotId, X = 0, Y = 0, Biome = "forest", LocationName = "Clearing", Description = "A forest clearing." };
            startTile.IsVisited = true;
            startTile.DiscoveredAt = DateTime.UtcNow;
            await store.Set(startTile.Id.ToString(), startTile, GameJsonContext.Default.MapTile, cancellationToken);

            worldState = new WorldState
            {
                Id = Guid.NewGuid(),
                SaveSlotId = saveSlotId,
                TimeOfDay = "dawn",
                PlayerX = 0,
                PlayerY = 0,
                RecentEvents = [$"You have arrived in \"{gameName}\"."]
            };
            mapService.SyncTileToWorldState(worldState, startTile);
            await worldStateService.SaveState(worldState, cancellationToken);
            logger.LogInformation("Generated world: {Biome} / {Location}", worldState.CurrentBiome, worldState.CurrentLocation);

            // Create fresh player stats
            var stats = new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };
            await worldStateService.SavePlayerStats(stats, cancellationToken);
            eventStream.Emit(new AgentEvent("⚔️ Adventurer created HP:100 Hunger:0 Energy:100", "GameMaster", AgentEventKind.AgentOutput));
        }
        else
        {
            eventStream.Emit(new AgentEvent($"Resuming: {worldState.CurrentLocation} ({worldState.PlayerX},{worldState.PlayerY})", "GameMaster", AgentEventKind.AgentInvoked));
            // Ensure surrounding tiles exist for the resumed position
            _ = mapService.GenerateSurroundingTiles(saveSlotId, worldState.PlayerX, worldState.PlayerY, gameName, cancellationToken);
        }

        return await ProcessTurnAsync(saveSlotId, "Describe the opening scene.", cancellationToken);
    }

    public async Task<GameTurnResult> ProcessTurnAsync(
        Guid saveSlotId,
        string playerInput,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Turn: {Input}", playerInput[..Math.Min(60, playerInput.Length)]);
        eventStream.Emit(new AgentEvent($"▶ \"{playerInput[..Math.Min(40, playerInput.Length)]}\"", "GameMaster", AgentEventKind.AgentInvoked));

        var slot = await saveSlotService.GetSaveSlot(saveSlotId, cancellationToken);
        var gameName = slot?.Name ?? "Adventure";
        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        var isOpeningScene = playerInput == "Describe the opening scene.";

        ActionResult? actionResult = null;

        // ── Step 1: ActionResolver — AI decides everything ─────────────────────
        // For all non-opening turns, ask AI what happened before narration so the
        // Narrator can describe the final state (post-movement, post-pickup, etc.).
        if (!isOpeningScene && worldState is not null)
        {
            actionResult = await ResolveActionAsync(worldState, playerInput, gameName, cancellationToken);

            // Apply movement (AI decided direction)
            if (!string.IsNullOrEmpty(actionResult?.MovementDirection))
            {
                var (newTile, updatedState) = await mapService.MovePlayer(
                    saveSlotId, worldState, actionResult.MovementDirection, gameName, cancellationToken);
                worldState = updatedState;
                await worldStateService.SaveState(worldState, cancellationToken);
                eventStream.Emit(new AgentEvent(
                    $"🗺️ Moved {actionResult.MovementDirection} → {newTile.LocationName} ({newTile.Biome})",
                    "WorldGen", AgentEventKind.AgentOutput));
            }

            // Apply exploring (AI decided player is looking around)
            if (actionResult?.IsExploring == true)
            {
                await mapService.RevealTile(saveSlotId, worldState.PlayerX, worldState.PlayerY, worldState, cancellationToken);
                await worldStateService.SaveState(worldState, cancellationToken);
                worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            }

            // Apply state changes from ActionResult
            if (actionResult is not null && worldState is not null)
                await ApplyActionResultAsync(worldState, actionResult, playerInput, cancellationToken);

            worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        }

        // ── Step 2: Narrator — describes the current state ─────────────────────
        var currentTile = worldState is not null
            ? await mapService.GetTile(saveSlotId, worldState.PlayerX, worldState.PlayerY, cancellationToken)
            : null;
        var tileContext = currentTile is not null
            ? mapService.BuildTileContext(currentTile, currentTile.IsRevealed)
            : BuildFallbackContext(worldState);

        string narrativeText;
        try
        {
            eventStream.Emit(new AgentEvent("Narrating...", "Narrator", AgentEventKind.AgentInvoked));
            var actionSummary = BuildActionSummary(playerInput, actionResult);
            var narratorInput = $"{tileContext}\n\n{actionSummary}";
            eventStream.Emit(new AgentEvent("📋 Narrator prompt", "Narrator", AgentEventKind.Prompt,
                narratorInput[..Math.Min(60, narratorInput.Length)],
                $"[System]\n{NarratorSystemPrompt}\n\n[User]\n{narratorInput}"));

            var resp = await chatClient.GetResponseAsync(
                [new(ChatRole.System, NarratorSystemPrompt), new(ChatRole.User, narratorInput)],
                cancellationToken: cancellationToken);
            narrativeText = resp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(narrativeText))
                narrativeText = "The world holds its breath.";

            eventStream.Emit(new AgentEvent("💬 Narrator response", "Narrator", AgentEventKind.Response,
                narrativeText[..Math.Min(60, narrativeText.Length)], narrativeText));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Narrator failed");
            narrativeText = $"⚠️ {ex.Message}\n\nTry a different action.";
            eventStream.Emit(new AgentEvent("Narrator failed", "Narrator", AgentEventKind.Error, ex.Message));
        }

        // ── Step 3: Stat decay ───────────────────────────────────────────────────
        if (!isOpeningScene)
            await ApplyStatDecayAsync(saveSlotId, cancellationToken);

        // ── Step 4: Suggestions ──────────────────────────────────────────────────
        List<SuggestedAction> suggestions;
        try
        {
            eventStream.Emit(new AgentEvent("Suggesting...", "Suggestion", AgentEventKind.AgentInvoked));
            var hasHidden = (worldState?.HiddenEntities?.Count ?? 0) > 0;
            var portableItems = string.Join(", ", (worldState?.KnownEntities ?? []).Take(3));
            var landmarks = string.Join(", ", (worldState?.LandmarkEntities ?? []).Take(2));
            var suggCtx = $"Location: {worldState?.CurrentLocation} ({worldState?.CurrentBiome}). Exits: north, south, east, west.";
            if (!string.IsNullOrEmpty(landmarks)) suggCtx += $" Landmarks: {landmarks}.";
            if (!string.IsNullOrEmpty(portableItems)) suggCtx += $" Portable items: {portableItems}.";
            if (hasHidden) suggCtx += " (things to discover here)";

            eventStream.Emit(new AgentEvent("📋 Suggestion prompt", "Suggestion", AgentEventKind.Prompt,
                suggCtx[..Math.Min(60, suggCtx.Length)],
                $"[System]\n{SuggestionSystemPrompt}\n\n[User]\n{suggCtx}"));

            var suggResp = await chatClient.GetResponseAsync<SuggestedActions>(
                [new(ChatRole.System, SuggestionSystemPrompt), new(ChatRole.User, suggCtx)],
                cancellationToken: cancellationToken);
            var rawSugg = suggResp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
            suggestions = suggResp.Result?.Actions is { Count: > 0 } acts ? acts : DefaultSuggestions(worldState);

            eventStream.Emit(new AgentEvent("💬 Suggestion response", "Suggestion", AgentEventKind.Response,
                $"{suggestions.Count} actions", rawSugg));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Suggestion failed");
            suggestions = DefaultSuggestions(worldState);
        }

        await PersistJournalEntry(saveSlotId, narrativeText, cancellationToken);
        await UpdateLastPlayed(saveSlotId, cancellationToken);
        eventStream.Emit(new AgentEvent("Turn complete", "GameMaster", AgentEventKind.WorkflowComplete));
        return new GameTurnResult(narrativeText, suggestions);
    }

    /// <summary>
    /// Calls the ActionResolver AI to determine what the player is doing.
    /// The AI returns exact entity names, movement direction, and whether exploring.
    /// </summary>
    private async Task<ActionResult?> ResolveActionAsync(
        WorldState worldState,
        string playerInput,
        string gameName,
        CancellationToken cancellationToken)
    {
        eventStream.Emit(new AgentEvent("Resolving action...", "ActionResolver", AgentEventKind.AgentInvoked));
        try
        {
            var allInvItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
            var inventoryNames = allInvItems
                .Where(i => i.SaveSlotId == worldState.SaveSlotId)
                .Select(i => i.ItemName)
                .ToList();

            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"Player said: \"{playerInput}\"");
            lines.AppendLine();
            var portableItems = worldState.KnownEntities ?? [];
            lines.AppendLine($"Available portable items (can be picked up): [{string.Join(", ", portableItems.Select(i => $"\"{i}\""))}]");
            if (worldState.LandmarkEntities?.Count > 0)
                lines.AppendLine($"Landmarks (immovable, examine only): [{string.Join(", ", worldState.LandmarkEntities.Select(l => $"\"{l}\""))}]");
            if (inventoryNames.Count > 0)
                lines.AppendLine($"Player inventory (can be used, equipped, or dropped): [{string.Join(", ", inventoryNames.Select(i => $"\"{i}\""))}]");
            lines.AppendLine();
            lines.AppendLine("Determine what the player is doing. Copy all names verbatim from the lists above.");

            var userCtx = lines.ToString().Trim();
            eventStream.Emit(new AgentEvent("📋 Resolver prompt", "ActionResolver", AgentEventKind.Prompt,
                $"Action: {playerInput[..Math.Min(50, playerInput.Length)]}",
                $"[System]\n{ActionResolverSystemPrompt}\n\n[User]\n{userCtx}"));

            var resolverResp = await chatClient.GetResponseAsync<ActionResult>(
                [new(ChatRole.System, ActionResolverSystemPrompt), new(ChatRole.User, userCtx)],
                cancellationToken: cancellationToken);
            var resolverJson = resolverResp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
            eventStream.Emit(new AgentEvent("💬 Resolver response", "ActionResolver", AgentEventKind.Response,
                resolverJson[..Math.Min(60, resolverJson.Length)], resolverJson));

            return resolverResp.Result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ActionResolver failed");
            eventStream.Emit(new AgentEvent("Resolver failed", "ActionResolver", AgentEventKind.Error, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Applies the AI-determined ActionResult to game state: pickups, drops, use, equip.
    /// No keyword matching — the AI already resolved what happened.
    /// </summary>
    private async Task ApplyActionResultAsync(
        WorldState worldState,
        ActionResult result,
        string playerInput,
        CancellationToken cancellationToken)
    {
        var changes = new List<string>();

        // Items picked up — Guard 1: must be in KnownEntities (AI should have used exact name)
        foreach (var item in result.ItemsPickedUp)
        {
            if (string.IsNullOrWhiteSpace(item.ItemName)) continue;
            var isKnown = (worldState.KnownEntities ?? []).Any(e => e.Equals(item.ItemName, StringComparison.OrdinalIgnoreCase));
            if (!isKnown)
            {
                eventStream.Emit(new AgentEvent($"🚫 Not in portable list: {item.ItemName}", "ActionResolver", AgentEventKind.AgentOutput));
                logger.LogInformation("Blocked pickup — not in KnownEntities: {Item}", item.ItemName);
                continue;
            }

            // Store with AI-provided effect
            var effect = item.Effect ?? "";
            var invItem = new InventoryItem
            {
                Id = Guid.NewGuid(), SaveSlotId = worldState.SaveSlotId,
                ItemName = item.ItemName, Description = item.Description, Quantity = 1,
                Effect = effect,
                IsConsumable = effect.StartsWith("heal") || effect.StartsWith("food"),
                IsEquippable = effect.StartsWith("weapon") || effect.StartsWith("armor"),
            };
            await store.Set(invItem.Id.ToString(), invItem, GameJsonContext.Default.InventoryItem, cancellationToken);
            changes.Add($"picked up {item.ItemName}");
            eventStream.Emit(new AgentEvent($"🎒 +{item.ItemName} [{effect}]", "Database", AgentEventKind.ToolResult));

            if (!result.EntitiesRemoved.Contains(item.ItemName))
                result.EntitiesRemoved.Add(item.ItemName);
        }

        // Items used/consumed from inventory
        foreach (var usedName in result.ItemsUsed)
        {
            if (string.IsNullOrWhiteSpace(usedName)) continue;
            var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
            var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == worldState.SaveSlotId &&
                i.ItemName.Equals(usedName, StringComparison.OrdinalIgnoreCase));
            if (invItem is null) continue;

            var stats = await worldStateService.GetPlayerStats(worldState.SaveSlotId, cancellationToken)
                        ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = worldState.SaveSlotId };
            ApplyEffect(stats, invItem.Effect);
            await worldStateService.SavePlayerStats(stats, cancellationToken);
            await store.Remove<InventoryItem>(invItem.Id.ToString(), cancellationToken);
            changes.Add($"used {usedName}");
            eventStream.Emit(new AgentEvent($"✨ Used: {usedName}", "ActionResolver", AgentEventKind.AgentOutput, $"HP:{stats.Health} Hunger:{stats.Hunger}"));
        }

        // Items equipped
        foreach (var equippedName in result.ItemsEquipped)
        {
            if (string.IsNullOrWhiteSpace(equippedName)) continue;
            var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
            var invItem = allItems.FirstOrDefault(i => i.SaveSlotId == worldState.SaveSlotId &&
                i.ItemName.Equals(equippedName, StringComparison.OrdinalIgnoreCase));
            if (invItem is null) continue;

            var stats = await worldStateService.GetPlayerStats(worldState.SaveSlotId, cancellationToken)
                        ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = worldState.SaveSlotId };
            if (invItem.Effect.StartsWith("weapon"))
            {
                stats.EquippedWeapon = invItem.ItemName;
                eventStream.Emit(new AgentEvent($"⚔️ Equipped: {invItem.ItemName}", "ActionResolver", AgentEventKind.AgentOutput));
            }
            else if (invItem.Effect.StartsWith("armor"))
            {
                stats.EquippedArmor = invItem.ItemName;
                if (int.TryParse(invItem.Effect.AsSpan(6), out var armorVal)) stats.Armor = armorVal;
                eventStream.Emit(new AgentEvent($"🛡️ Equipped: {invItem.ItemName} (+{stats.Armor})", "ActionResolver", AgentEventKind.AgentOutput));
            }
            await worldStateService.SavePlayerStats(stats, cancellationToken);
            changes.Add($"equipped {equippedName}");
        }

        // Items dropped
        foreach (var droppedName in result.ItemsDropped)
        {
            if (string.IsNullOrWhiteSpace(droppedName)) continue;
            var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
            var existing = allItems.FirstOrDefault(i => i.SaveSlotId == worldState.SaveSlotId &&
                i.ItemName.Equals(droppedName, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;
            await store.Remove<InventoryItem>(existing.Id.ToString(), cancellationToken);
            worldState.KnownEntities ??= [];
            if (!worldState.KnownEntities.Contains(droppedName, StringComparer.OrdinalIgnoreCase))
                worldState.KnownEntities.Add(droppedName);
            changes.Add($"dropped {droppedName}");
            eventStream.Emit(new AgentEvent($"🗑 -{droppedName}", "Database", AgentEventKind.ToolResult));
        }

        // Remove entities from world state and tile
        worldState.KnownEntities ??= [];
        foreach (var removed in result.EntitiesRemoved)
        {
            var idx = worldState.KnownEntities.FindIndex(e => e.Equals(removed, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) worldState.KnownEntities.RemoveAt(idx);
        }

        if (result.EntitiesRemoved.Count > 0)
        {
            var tile = await mapService.GetTile(worldState.SaveSlotId, worldState.PlayerX, worldState.PlayerY, cancellationToken);
            if (tile is not null)
            {
                var changed = false;
                foreach (var removed in result.EntitiesRemoved)
                {
                    var fi = tile.Features.FindIndex(f => f.Equals(removed, StringComparison.OrdinalIgnoreCase));
                    if (fi >= 0) { tile.Features.RemoveAt(fi); changed = true; }
                    var hi = tile.HiddenItems.FindIndex(h => h.Equals(removed, StringComparison.OrdinalIgnoreCase));
                    if (hi >= 0) { tile.HiddenItems.RemoveAt(hi); changed = true; }
                }
                if (changed) await store.Set(tile.Id.ToString(), tile, GameJsonContext.Default.MapTile, cancellationToken);
            }
        }

        worldState.RecentEvents ??= [];
        worldState.RecentEvents.Add(changes.Count > 0 ? string.Join(", ", changes) : playerInput[..Math.Min(40, playerInput.Length)]);
        if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
        await worldStateService.SaveState(worldState, cancellationToken);
        eventStream.Emit(new AgentEvent(changes.Count > 0 ? string.Join(", ", changes) : "no changes", "ActionResolver", AgentEventKind.AgentCompleted));
    }

    private static string BuildActionSummary(string playerInput, ActionResult? result)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"Player action: \"{playerInput}\"");
        if (result is null) return lines.ToString().Trim();
        if (!string.IsNullOrEmpty(result.MovementDirection))
            lines.AppendLine($"Player moved: {result.MovementDirection}");
        if (result.IsExploring)
            lines.AppendLine("Player searched the area.");
        foreach (var item in result.ItemsPickedUp)
            lines.AppendLine($"Player picked up: {item.ItemName}");
        foreach (var used in result.ItemsUsed)
            lines.AppendLine($"Player used: {used}");
        foreach (var equipped in result.ItemsEquipped)
            lines.AppendLine($"Player equipped: {equipped}");
        return lines.ToString().Trim();
    }

    private async Task ApplyStatDecayAsync(Guid saveSlotId, CancellationToken ct)
    {
        try
        {
            var stats = await worldStateService.GetPlayerStats(saveSlotId, ct);
            if (stats is null) return;
            stats.Hunger = Math.Min(100, stats.Hunger + Random.Shared.Next(2, 5));
            stats.Tiredness = Math.Min(100, stats.Tiredness + Random.Shared.Next(1, 3));
            if (stats.Hunger >= 80)    stats.Health = Math.Max(0, stats.Health - 2);
            if (stats.Tiredness >= 90) stats.Health = Math.Max(0, stats.Health - 1);
            await worldStateService.SavePlayerStats(stats, ct);
            if (stats.Health <= 10)
                eventStream.Emit(new AgentEvent($"⚠️ Critical! HP:{stats.Health}", "GameMaster", AgentEventKind.Error));
        }
        catch (Exception ex) { logger.LogWarning(ex, "Stat decay failed (non-fatal)"); }
    }

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

    private static string BuildFallbackContext(WorldState? w)
    {
        if (w is null) return "Location: unknown.";
        return $"[{w.CurrentBiome.ToUpperInvariant()} — {w.CurrentLocation}]\nAn area in the {w.CurrentBiome}.";
    }

    private static List<SuggestedAction> DefaultSuggestions(WorldState? worldState = null)
    {
        var suggestions = new List<SuggestedAction> { new("Look around", "look around carefully") };
        if (worldState?.KnownEntities?.Count > 0)
            suggestions.Add(new("Pick up", $"pick up the {worldState.KnownEntities[0]}"));
        else
            suggestions.Add(new("Go north", "go north"));
        suggestions.Add(new("Rest", "rest and recover your strength"));
        return suggestions;
    }

    private async Task PersistJournalEntry(Guid saveSlotId, string text, CancellationToken ct)
    {
        try
        {
            var entry = new JournalEntry { Id = Guid.NewGuid(), SaveSlotId = saveSlotId, EntryText = text, Timestamp = DateTime.UtcNow, Type = JournalEntryType.Narrative };
            await store.Set(entry.Id.ToString(), entry, GameJsonContext.Default.JournalEntry, ct);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to persist journal entry"); }
    }

    private async Task UpdateLastPlayed(Guid saveSlotId, CancellationToken ct)
    {
        try
        {
            var all = await store.GetAll<SaveSlot>(GameJsonContext.Default.SaveSlot, ct);
            var slot = all.FirstOrDefault(s => s.Id == saveSlotId);
            if (slot is not null) { slot.LastPlayedAt = DateTime.UtcNow; await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, ct); }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to update last played"); }
    }
}

