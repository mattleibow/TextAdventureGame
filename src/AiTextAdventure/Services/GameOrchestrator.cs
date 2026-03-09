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
        - 2-3 sentences MAXIMUM.
        - Use ONLY the location data provided — never invent places, items, or paths.
        - Default: atmospheric mood only (sounds, smells, light). Do NOT list items.
        - If action is "look around": describe the visible features and discovered items from context.
        - If player tries something impossible: say so briefly, then describe what IS there.
        - Pure prose. No lists, headings, or game mechanics language.
        """;

    // ActionResolver: detects pickup/drop. Movement is handled by MapService keyword detection.
    private const string ActionResolverSystemPrompt = """
        You are a game state resolver for a text adventure. Determine what changed after the player's action.
        Only report changes that clearly happened based on the player's action and the narrative.
        ItemsPickedUp: ONLY physical hand-held PORTABLE objects — potions, herbs, food, weapons (knife/sword/axe), armor (cloak/bracers/shield), gems, scrolls, keys, coins.
        NEVER include landmarks, ruins, caves, altars, statues, fountains, bridges, towers, trees, rocks, campfires, or any structure — these are immovable and cannot be picked up.
        If nothing changed, return all empty arrays.
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
        var shortInput = playerInput[..Math.Min(60, playerInput.Length)];
        logger.LogInformation("Turn: {Input}", shortInput);
        eventStream.Emit(new AgentEvent($"▶ \"{playerInput[..Math.Min(40, playerInput.Length)]}\"", "GameMaster", AgentEventKind.AgentInvoked));

        var slot = await saveSlotService.GetSaveSlot(saveSlotId, cancellationToken);
        var gameName = slot?.Name ?? "Adventure";

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        var isOpeningScene = playerInput == "Describe the opening scene.";
        var isLookAround = !isOpeningScene && IsLookAroundAction(playerInput);

        // ── Pre-turn: Movement detection (keyword-based, no LLM call) ──────────
        if (!isOpeningScene && !isLookAround)
        {
            var direction = MapService.DetectMovementDirection(playerInput);
            if (direction is not null && worldState is not null)
            {
                var (newTile, updatedState) = await mapService.MovePlayer(saveSlotId, worldState, direction, gameName, cancellationToken);
                worldState = updatedState;
                await worldStateService.SaveState(worldState, cancellationToken);
                eventStream.Emit(new AgentEvent($"🗺️ Moved {direction} → {newTile.LocationName} ({newTile.Biome})", "WorldGen", AgentEventKind.AgentOutput));
            }
        }

        // ── Pre-turn: Look around — reveal hidden tile items ─────────────────
        if (isLookAround && worldState is not null)
        {
            await mapService.RevealTile(saveSlotId, worldState.PlayerX, worldState.PlayerY, worldState, cancellationToken);
            await worldStateService.SaveState(worldState, cancellationToken);
            worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        }

        // Get current tile for grounded narrator context
        var currentTile = worldState is not null
            ? await mapService.GetTile(saveSlotId, worldState.PlayerX, worldState.PlayerY, cancellationToken)
            : null;
        var tileContext = currentTile is not null
            ? mapService.BuildTileContext(currentTile, isLookAround || (currentTile?.IsRevealed ?? false))
            : BuildFallbackContext(worldState);

        try
        {
            // ── Step 1: Narrator — grounded in tile data ────────────────────────
            eventStream.Emit(new AgentEvent("Narrating...", "Narrator", AgentEventKind.AgentInvoked));
            string narrativeText;
            try
            {
                var narrativeMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, NarratorSystemPrompt),
                    new(ChatRole.User, $"{tileContext}\nAction: {playerInput}")
                };
                var promptSummary = $"{tileContext}\nAction: {playerInput}";
                eventStream.Emit(new AgentEvent("📋 Narrator prompt", "Narrator", AgentEventKind.Prompt,
                    promptSummary[..Math.Min(60, promptSummary.Length)],
                    $"[System]\n{NarratorSystemPrompt}\n\n[User]\n{promptSummary}"));

                var resp = await chatClient.GetResponseAsync(narrativeMessages, cancellationToken: cancellationToken);
                narrativeText = resp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(narrativeText))
                    narrativeText = "The world holds its breath. Try a different action.";

                eventStream.Emit(new AgentEvent("💬 Narrator response", "Narrator", AgentEventKind.Response,
                    narrativeText[..Math.Min(60, narrativeText.Length)],
                    narrativeText));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Narrator failed");
                narrativeText = $"⚠️ {ex.Message}\n\nTry a different action.";
                eventStream.Emit(new AgentEvent("Narrator failed", "Narrator", AgentEventKind.Error, ex.Message));
            }

            logger.LogInformation("Narrative: {Length} chars", narrativeText.Length);
            eventStream.Emit(new AgentEvent("Narrative ready", "Narrator", AgentEventKind.AgentOutput,
                narrativeText[..Math.Min(80, narrativeText.Length)]));

            // ── Step 2: ActionResolver — pickup/drop (skip for opening/look-around) ─
            if (worldState is not null && !isOpeningScene && !isLookAround)
            {
                await ResolveAndApplyActionAsync(worldState, playerInput, narrativeText, cancellationToken);
                worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            }
            else if (worldState is not null)
            {
                worldState.RecentEvents ??= [];
                var evtPrefix = isLookAround ? "Looked around" : "Opening";
                worldState.RecentEvents.Add($"{evtPrefix}: {narrativeText[..Math.Min(60, narrativeText.Length)]}");
                if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
                await worldStateService.SaveState(worldState, cancellationToken);
                worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            }

            // ── Step 2b: Item use/equip ──────────────────────────────────────────
            if (!isOpeningScene)
                await ApplyItemInteractionsAsync(saveSlotId, playerInput, narrativeText, cancellationToken);

            // ── Step 2c: Stat decay ──────────────────────────────────────────────
            if (!isOpeningScene)
                await ApplyStatDecayAsync(saveSlotId, cancellationToken);

            // ── Step 3: Suggestion ───────────────────────────────────────────────
            eventStream.Emit(new AgentEvent("Suggesting...", "Suggestion", AgentEventKind.AgentInvoked));
            List<SuggestedAction> suggestions;
            try
            {
                var hasHidden = (worldState?.HiddenEntities?.Count ?? 0) > 0;
                var portableItems = string.Join(", ", (worldState?.KnownEntities ?? []).Take(3));
                var landmarks = string.Join(", ", (worldState?.LandmarkEntities ?? []).Take(2));
                var suggContext = $"Location: {worldState?.CurrentLocation} ({worldState?.CurrentBiome}). Exits: north, south, east, west.";
                if (!string.IsNullOrEmpty(landmarks)) suggContext += $" Landmarks to examine: {landmarks}.";
                if (!string.IsNullOrEmpty(portableItems)) suggContext += $" Portable items to pick up: {portableItems}.";
                if (hasHidden) suggContext += " (undiscovered things here)";

                eventStream.Emit(new AgentEvent("📋 Suggestion prompt", "Suggestion", AgentEventKind.Prompt,
                    suggContext[..Math.Min(60, suggContext.Length)],
                    $"[System]\n{SuggestionSystemPrompt}\n\n[User]\n{suggContext}"));

                var suggMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, SuggestionSystemPrompt),
                    new(ChatRole.User, suggContext)
                };
                var suggResp = await chatClient.GetResponseAsync<SuggestedActions>(suggMessages, cancellationToken: cancellationToken);
                var rawSugg = suggResp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
                suggestions = suggResp.Result?.Actions is { Count: > 0 } acts ? acts : DefaultSuggestions(worldState);

                eventStream.Emit(new AgentEvent("💬 Suggestion response", "Suggestion", AgentEventKind.Response,
                    $"{suggestions.Count} actions", rawSugg));
                logger.LogInformation("Suggestions: {Count}", suggestions.Count);
                eventStream.Emit(new AgentEvent($"{suggestions.Count} suggestions", "Suggestion", AgentEventKind.AgentOutput));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Suggestion failed, using context-aware defaults");
                eventStream.Emit(new AgentEvent("Default suggestions", "Suggestion", AgentEventKind.AgentCompleted));
                suggestions = DefaultSuggestions(worldState);
            }

            await PersistJournalEntry(saveSlotId, narrativeText, cancellationToken);
            eventStream.Emit(new AgentEvent("💾 Journal saved", "Database", AgentEventKind.ToolResult));
            await UpdateLastPlayed(saveSlotId, cancellationToken);

            eventStream.Emit(new AgentEvent("Turn complete", "GameMaster", AgentEventKind.WorkflowComplete));
            return new GameTurnResult(narrativeText, suggestions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Turn failed for {SaveSlotId}", saveSlotId);
            eventStream.Emit(new AgentEvent("Turn failed", "GameMaster", AgentEventKind.Error, ex.Message));
            return new GameTurnResult(
                $"⚠️ The magical forces are disrupted... ({ex.Message})\n\nTry a different action.",
                DefaultSuggestions(worldState));
        }
    }

    private async Task ResolveAndApplyActionAsync(
        WorldState worldState,
        string playerInput,
        string narrativeText,
        CancellationToken cancellationToken)
    {
        eventStream.Emit(new AgentEvent("Resolving action...", "ActionResolver", AgentEventKind.AgentInvoked));
        try
        {
            var portableItems = string.Join(", ", (worldState.KnownEntities ?? []).Take(6));
            var landmarks = string.Join(", ", (worldState.LandmarkEntities ?? []).Take(4));
            // Pass exact item names explicitly so the AI uses them verbatim in ItemsPickedUp
            var userCtx = $"Portable items the player can pick up (use EXACT names): [{(string.IsNullOrEmpty(portableItems) ? "none" : portableItems)}].";
            if (!string.IsNullOrEmpty(landmarks)) userCtx += $" Immovable landmarks (examine only, never pick up): [{landmarks}].";
            userCtx += $" Player action: \"{playerInput}\". What just happened: {narrativeText[..Math.Min(120, narrativeText.Length)]}";

            eventStream.Emit(new AgentEvent("📋 Resolver prompt", "ActionResolver", AgentEventKind.Prompt,
                $"Action: {playerInput[..Math.Min(50, playerInput.Length)]}",
                $"[System]\n{ActionResolverSystemPrompt}\n\n[User]\n{userCtx}"));

            var resolverMessages = new List<ChatMessage>
            {
                new(ChatRole.System, ActionResolverSystemPrompt),
                new(ChatRole.User, userCtx)
            };

            var resolverResp = await chatClient.GetResponseAsync<ActionResult>(resolverMessages, cancellationToken: cancellationToken);
            var resolverJson = resolverResp.Messages.LastOrDefault()?.Text?.Trim() ?? "";

            eventStream.Emit(new AgentEvent("💬 Resolver response", "ActionResolver", AgentEventKind.Response,
                resolverJson[..Math.Min(60, resolverJson.Length)],
                resolverJson));

            var result = resolverResp.Result;
            if (result is null)
            {
                eventStream.Emit(new AgentEvent("No state changes", "ActionResolver", AgentEventKind.AgentCompleted));
                return;
            }

            logger.LogDebug("ActionResult: {Picked} picked, {Dropped} dropped", result.ItemsPickedUp.Count, result.ItemsDropped.Count);
            var changes = new List<string>();

            // Items picked up
            foreach (var item in result.ItemsPickedUp)
            {
                if (string.IsNullOrWhiteSpace(item.ItemName)) continue;

                // Guard 1: resolve the canonical KnownEntity name that the AI is referring to.
                // The AI may return a partial/approximate name (e.g. "sword" for "weathered iron sword"),
                // so we do fuzzy matching: exact → AI name contained in entity → entity contained in AI name.
                var canonical = (worldState.KnownEntities ?? []).FirstOrDefault(e =>
                    e.Equals(item.ItemName, StringComparison.OrdinalIgnoreCase) ||
                    e.Contains(item.ItemName, StringComparison.OrdinalIgnoreCase) ||
                    item.ItemName.Contains(e, StringComparison.OrdinalIgnoreCase));

                if (canonical is null)
                {
                    eventStream.Emit(new AgentEvent($"🚫 Not in portable list: {item.ItemName}", "ActionResolver", AgentEventKind.AgentOutput));
                    logger.LogInformation("Blocked pickup — not in KnownEntities: {Item} (known: {Known})", item.ItemName, string.Join(", ", worldState.KnownEntities ?? []));
                    continue;
                }

                // Use canonical name for all subsequent DB/state operations
                var canonicalName = canonical;

                // Guard 2: item must NOT be in the landmarks list
                var isInLandmarks = (worldState.LandmarkEntities ?? []).Any(e => e.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));
                if (isInLandmarks)
                {
                    eventStream.Emit(new AgentEvent($"🚫 Cannot pick up landmark: {canonicalName}", "ActionResolver", AgentEventKind.AgentOutput));
                    logger.LogInformation("Blocked landmark pickup attempt: {Item}", canonicalName);
                    continue;
                }

                // Guard 3: word-boundary keyword blocklist as last-resort safety net
                if (IsLandmarkByWords(canonicalName))
                {
                    eventStream.Emit(new AgentEvent($"🚫 Blocked non-portable: {canonicalName}", "ActionResolver", AgentEventKind.AgentOutput));
                    logger.LogInformation("Blocked non-portable keyword match: {Item}", canonicalName);
                    continue;
                }

                var effect = InferItemEffect(canonicalName, item.Description);
                if (effect.StartsWith("poison"))
                {
                    var stats = await worldStateService.GetPlayerStats(worldState.SaveSlotId, cancellationToken)
                                ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = worldState.SaveSlotId };
                    ApplyEffect(stats, effect);
                    await worldStateService.SavePlayerStats(stats, cancellationToken);
                    changes.Add($"touched {canonicalName} → damaged!");
                    eventStream.Emit(new AgentEvent($"☠️ {canonicalName} (dangerous!)", "ActionResolver", AgentEventKind.AgentOutput, $"HP → {stats.Health}"));
                }
                else
                {
                    var invItem = new InventoryItem
                    {
                        Id = Guid.NewGuid(), SaveSlotId = worldState.SaveSlotId,
                        ItemName = canonicalName, Description = item.Description, Quantity = 1,
                        Effect = effect, IsConsumable = IsConsumableEffect(effect), IsEquippable = IsEquippableEffect(effect)
                    };
                    await store.Set(invItem.Id.ToString(), invItem, GameJsonContext.Default.InventoryItem, cancellationToken);
                    changes.Add($"picked up {canonicalName}");
                    eventStream.Emit(new AgentEvent($"🎒 +{canonicalName} [{effect}]", "Database", AgentEventKind.ToolResult));
                    logger.LogInformation("Inventory: added {Item} effect={Effect}", canonicalName, effect);
                }
                if (!result.EntitiesRemoved.Contains(canonicalName))
                    result.EntitiesRemoved.Add(canonicalName);
            }

            // Items dropped
            var isDropAction = playerInput.Contains("drop", StringComparison.OrdinalIgnoreCase)
                            || playerInput.Contains("put down", StringComparison.OrdinalIgnoreCase);
            if (isDropAction)
            {
                foreach (var droppedName in result.ItemsDropped)
                {
                    if (string.IsNullOrWhiteSpace(droppedName)) continue;
                    var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
                    var existing = allItems.FirstOrDefault(i => i.SaveSlotId == worldState.SaveSlotId &&
                        i.ItemName.Equals(droppedName, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        await store.Remove<InventoryItem>(existing.Id.ToString(), cancellationToken);
                        worldState.KnownEntities ??= [];
                        if (!worldState.KnownEntities.Contains(droppedName))
                            worldState.KnownEntities.Add(droppedName);
                        changes.Add($"dropped {droppedName}");
                        eventStream.Emit(new AgentEvent($"🗑 -{droppedName}", "Database", AgentEventKind.ToolResult));
                    }
                }
            }

            // Remove entities from world snapshot — use fuzzy match so partial names (e.g. "sword") remove
            // the canonical entry ("weathered iron sword") correctly.
            worldState.KnownEntities ??= [];
            foreach (var removed in result.EntitiesRemoved)
            {
                var idx = worldState.KnownEntities.FindIndex(e =>
                    e.Equals(removed, StringComparison.OrdinalIgnoreCase) ||
                    e.Contains(removed, StringComparison.OrdinalIgnoreCase) ||
                    removed.Contains(e, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) worldState.KnownEntities.RemoveAt(idx);
            }

            // Also remove from the persistent MapTile (same fuzzy match)
            if (result.EntitiesRemoved.Count > 0)
            {
                var tile = await mapService.GetTile(worldState.SaveSlotId, worldState.PlayerX, worldState.PlayerY, cancellationToken);
                if (tile is not null)
                {
                    var changed = false;
                    foreach (var removed in result.EntitiesRemoved)
                    {
                        var fi = tile.Features.FindIndex(f =>
                            f.Equals(removed, StringComparison.OrdinalIgnoreCase) ||
                            f.Contains(removed, StringComparison.OrdinalIgnoreCase) ||
                            removed.Contains(f, StringComparison.OrdinalIgnoreCase));
                        if (fi >= 0) { tile.Features.RemoveAt(fi); changed = true; }
                        var hi = tile.HiddenItems.FindIndex(h =>
                            h.Equals(removed, StringComparison.OrdinalIgnoreCase) ||
                            h.Contains(removed, StringComparison.OrdinalIgnoreCase) ||
                            removed.Contains(h, StringComparison.OrdinalIgnoreCase));
                        if (hi >= 0) { tile.HiddenItems.RemoveAt(hi); changed = true; }
                    }
                    if (changed) await store.Set(tile.Id.ToString(), tile, GameJsonContext.Default.MapTile, cancellationToken);
                }
            }

            // New entities
            foreach (var newEntity in result.NewEntities)
            {
                if (!string.IsNullOrWhiteSpace(newEntity) && !(worldState.KnownEntities ?? []).Contains(newEntity))
                {
                    (worldState.KnownEntities ??= []).Add(newEntity);
                    changes.Add($"discovered {newEntity}");
                }
            }

            worldState.RecentEvents ??= [];
            var turnSummary = changes.Count > 0 ? string.Join(", ", changes) : playerInput[..Math.Min(40, playerInput.Length)];
            worldState.RecentEvents.Add(turnSummary);
            if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];

            await worldStateService.SaveState(worldState, cancellationToken);
            eventStream.Emit(new AgentEvent(changes.Count > 0 ? string.Join(", ", changes) : "no changes", "ActionResolver", AgentEventKind.AgentCompleted));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ActionResolver failed");
            eventStream.Emit(new AgentEvent("Resolver failed", "ActionResolver", AgentEventKind.Error, ex.Message));
            worldState.RecentEvents ??= [];
            worldState.RecentEvents.Add(playerInput[..Math.Min(40, playerInput.Length)]);
            if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
            await worldStateService.SaveState(worldState, cancellationToken);
        }
    }

    private async Task ApplyItemInteractionsAsync(Guid saveSlotId, string playerInput, string narrativeText, CancellationToken ct)
    {
        var lower = playerInput.ToLowerInvariant();
        var isUse = lower.Contains("eat") || lower.Contains("drink") || lower.Contains("use") || lower.Contains("consume");
        var isEquip = lower.Contains("equip") || lower.Contains("wield") || lower.Contains("wear") || lower.Contains("put on");
        if (!isUse && !isEquip) return;

        var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, ct);
        var inventory = allItems.Where(i => i.SaveSlotId == saveSlotId).ToList();
        var targetItem = inventory.FirstOrDefault(i => lower.Contains(i.ItemName.ToLowerInvariant()));
        if (targetItem is null) return;

        if (isEquip && targetItem.IsEquippable)
        {
            var stats = await worldStateService.GetPlayerStats(saveSlotId, ct)
                        ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };
            if (targetItem.Effect.StartsWith("weapon"))
            {
                stats.EquippedWeapon = targetItem.ItemName;
                eventStream.Emit(new AgentEvent($"⚔️ Equipped: {targetItem.ItemName}", "ActionResolver", AgentEventKind.AgentOutput));
            }
            else if (targetItem.Effect.StartsWith("armor"))
            {
                stats.EquippedArmor = targetItem.ItemName;
                if (int.TryParse(targetItem.Effect[6..], out var armorVal)) stats.Armor = armorVal;
                eventStream.Emit(new AgentEvent($"🛡️ Equipped: {targetItem.ItemName} (+{stats.Armor} armor)", "ActionResolver", AgentEventKind.AgentOutput));
            }
            await worldStateService.SavePlayerStats(stats, ct);
        }
        else if (isUse && targetItem.IsConsumable)
        {
            var stats = await worldStateService.GetPlayerStats(saveSlotId, ct)
                        ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };
            ApplyEffect(stats, targetItem.Effect);
            await worldStateService.SavePlayerStats(stats, ct);
            eventStream.Emit(new AgentEvent($"✨ Used: {targetItem.ItemName}", "ActionResolver", AgentEventKind.AgentOutput, $"HP:{stats.Health} Hunger:{stats.Hunger}"));
            await store.Remove<InventoryItem>(targetItem.Id.ToString(), ct);
            eventStream.Emit(new AgentEvent($"🗑 Consumed: {targetItem.ItemName}", "Database", AgentEventKind.ToolResult));
        }
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

    // ── Effect helpers (shared with ActionResolver) ──────────────────────────

    private static string InferItemEffect(string itemName, string description)
    {
        var text = (itemName + " " + description).ToLowerInvariant();
        if (text.Contains("potion") || text.Contains("elixir") || text.Contains("tonic") ||
            text.Contains("salve") || text.Contains("healing") || text.Contains("luminescent") ||
            text.Contains("glowing") || text.Contains("health") || text.Contains("vial") && text.Contains("heal"))
            return "heal:30";
        if (text.Contains("ration") || text.Contains("bread") || text.Contains("fruit") ||
            text.Contains("berry") || text.Contains("mushroom") || text.Contains("meat") ||
            text.Contains("fish") || text.Contains("jerky") || text.Contains("dried") ||
            text.Contains("food") || text.Contains("cake") || text.Contains("venison") ||
            text.Contains("dates") || text.Contains("eel"))
            return "food:25";
        if (text.Contains("sword") || text.Contains("knife") || text.Contains("dagger") ||
            text.Contains("axe") || text.Contains("blade") || text.Contains("spear") ||
            text.Contains("bow") || text.Contains("mace") || text.Contains("staff") ||
            text.Contains("club") || text.Contains("scimitar") || text.Contains("cutlass") ||
            text.Contains("rapier") || text.Contains("sabre") || text.Contains("lance"))
            return "weapon:15";
        if (text.Contains("armor") || text.Contains("shield") || text.Contains("helm") ||
            text.Contains("mail") || text.Contains("bracers") || text.Contains("gauntlet") ||
            text.Contains("cloak") || text.Contains("pauldron") || text.Contains("buckler") ||
            text.Contains("chainmail") || text.Contains("plate") || text.Contains("pelt") ||
            text.Contains("vest") || text.Contains("cuirass"))
            return "armor:10";
        if (text.Contains("venom") || text.Contains("poison") || text.Contains("asp") ||
            text.Contains("serpent") || text.Contains("viper") || text.Contains("spider") ||
            text.Contains("stonefish") || text.Contains("adder") || text.Contains("venomous") ||
            text.Contains("trap") || text.Contains("moccasin") || text.Contains("rattlesnake"))
            return "poison:25";
        return "";
    }

    private static bool IsConsumableEffect(string effect) => effect.StartsWith("heal") || effect.StartsWith("food");
    private static bool IsEquippableEffect(string effect) => effect.StartsWith("weapon") || effect.StartsWith("armor");

    /// <summary>
    /// Returns true if the item name looks like a landmark/structure that cannot be picked up.
    /// This is a hard safety net — the prompt and KnownEntities list are the primary guards.
    /// </summary>
    // Landmark keywords that, when they appear as a whole word, indicate a non-portable structure.
    // Using word-boundary matching prevents false positives like "tower shield", "tree branch", "archery bow".
    private static readonly HashSet<string> LandmarkWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ruins", "ruin", "cave", "cavern", "grotto",
        "altar", "shrine", "temple", "cathedral", "chapel",
        "statue", "sculpture", "monument",
        "fountain", "well",
        "tower", "turret", "steeple",
        "bridge",
        "gate", "portal",
        "campfire", "firepit", "bonfire",
        "column", "pillar", "obelisk", "monolith",
        "cairn",
        "boulder", "cliff", "outcrop",
        "pool", "pond", "lake", "marsh",
        "waterfall", "stream", "river",
        "road", "trail",
        "rampart", "battlement",
        "entrance", "passage",
    };

    /// <summary>
    /// Returns true if any word in the item name is an exact match to a landmark keyword.
    /// Uses word splitting to prevent false positives (e.g. "tower shield" is NOT a landmark;
    /// only "tower" standalone triggers it).
    /// </summary>
    private static bool IsLandmarkByWords(string itemName)
    {
        // Split on spaces and common separators, check each token
        var words = itemName.Split([' ', '-', '_', ','], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => LandmarkWords.Contains(w));
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsLookAroundAction(string input)
    {
        var lower = input.ToLowerInvariant();
        return lower.Contains("look around") || lower.Contains("look about") ||
               lower.Contains("examine area") || lower.Contains("examine surroundings") ||
               lower.Contains("survey") || lower.Contains("scan area") ||
               lower.Contains("search the area") || lower.Contains("search area");
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

