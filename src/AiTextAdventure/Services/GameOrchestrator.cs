using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services;

public record GameTurnResult(string Narrative, List<SuggestedAction> Suggestions);

/// <summary>
/// Orchestrates a single player turn by making direct IChatClient calls.
/// Keeps prompts short to stay within Apple Intelligence's small context window (~2k tokens).
/// Turn flow: Narrator → ActionResolver → apply state + stats → Suggestion
/// </summary>
public class GameOrchestrator(
    IChatClient chatClient,
    WorldStateService worldStateService,
    SaveSlotService saveSlotService,
    IDocumentStore store,
    EventStream eventStream,
    ILogger<GameOrchestrator> logger)
{
    // Narrator: concise second-person prose. Atmospheric by default; reveals items only on "look around".
    private const string NarratorSystemPrompt = """
        You are a text adventure narrator. Write vivid, second-person, present-tense prose.
        Rules:
        - 2-3 sentences MAXIMUM.
        - DEFAULT: describe only immediate atmosphere, mood, sounds, smells. Do NOT list items.
        - If action contains "look around" or "examine area": mention visible items and paths from context.
        - If player tries something impossible, say so in 1 sentence, then describe what IS nearby.
        - Pure atmospheric prose only. No lists, no headings, no game mechanics language.
        """;

    // ActionResolver: structured state changes only (pickup/drop/move). Keep JSON minimal.
    private const string ActionResolverSystemPrompt = """
        Resolve game state changes. Output ONLY valid JSON (no markdown):
        {"ItemsPickedUp":[],"ItemsDropped":[],"LocationChanged":null,"EntitiesRemoved":[],"NewEntities":[]}
        Rules:
        - ItemsPickedUp: [{"ItemName":"name","Description":"brief desc"}] only if item IS in Entities list.
        - ItemsDropped: item name strings the player dropped.
        - LocationChanged: new location name if player moved to an exit, else null.
        - EntitiesRemoved: names to remove from world (picked up or destroyed).
        - NewEntities: newly discovered entity names.
        - If nothing changed, return all empty/null.
        """;

    // Suggestion: exactly 3 grounded actions as compact JSON.
    private const string SuggestionSystemPrompt = """
        Output ONLY valid JSON (no markdown):
        {"Actions":[{"Label":"short label","ActionText":"action text"},{"Label":"label2","ActionText":"action2"},{"Label":"label3","ActionText":"action3"}]}
        Generate exactly 3 short action suggestions based on the location context.
        Rules:
        - If player has NOT looked around yet: first action MUST be {"Label":"Look around","ActionText":"look around carefully"}.
        - Reference specific items or exits from the context when present.
        - Label: 2-4 words. ActionText: 4-8 words.
        """;

    private const string WorldGenSystemPrompt = """
        Output ONLY valid JSON (no markdown):
        {"CurrentBiome":"forest","CurrentLocation":"Name","TimeOfDay":"dawn","RegionDescription":"2 sentences.","KnownEntities":["obvious large feature"],"HiddenEntities":["healing potion","dried rations","rusty knife","leather armor","venomous serpent"],"AvailableExits":["narrow path north","crumbling stone bridge"],"RecentEvents":["You have arrived."]}
        Generate a starting world for the adventure name given. Match the theme.
        KnownEntities: 1-2 immediately obvious, large, immovable features (e.g. "mossy stone altar", "towering oak").
        HiddenEntities: 5 SPECIFIC discoverable items — MUST include: healing item (potion/salve/tonic), food (rations/fruit/bread), weapon (knife/sword/axe/staff), armor/protection (bracers/cloak/mail/shield), and one danger (venomous creature/trap/poison vial). Use specific thematic names.
        AvailableExits: 2-3 specific paths/locations the player can travel to.
        Never use generic names like "item1" or "entity2".
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
            worldState = await GenerateWorldStateAsync(saveSlotId, gameName, cancellationToken);
            await worldStateService.SaveState(worldState, cancellationToken);
            logger.LogInformation("Generated world: {Biome} / {Location}", worldState.CurrentBiome, worldState.CurrentLocation);

            // Create fresh player stats for new game
            var stats = new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };
            await worldStateService.SavePlayerStats(stats, cancellationToken);
            eventStream.Emit(new AgentEvent("⚔️ Adventurer created HP:100 Hunger:0 Energy:100", "GameMaster", AgentEventKind.AgentOutput));
        }
        else
        {
            eventStream.Emit(new AgentEvent($"Resuming: {worldState.CurrentLocation}", "GameMaster", AgentEventKind.AgentInvoked));
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

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);

        // ── Pre-turn: handle "look around" — reveal hidden entities before narrating ─
        var isLookAround = IsLookAroundAction(playerInput);
        if (isLookAround && worldState is not null)
        {
            var revealed = worldState.HiddenEntities ?? [];
            if (revealed.Count > 0)
            {
                worldState.KnownEntities ??= [];
                foreach (var h in revealed)
                    if (!worldState.KnownEntities.Contains(h))
                        worldState.KnownEntities.Add(h);
                worldState.HiddenEntities = [];
                eventStream.Emit(new AgentEvent($"🔍 Revealed {revealed.Count} hidden items", "GameMaster", AgentEventKind.AgentOutput));
            }
        }

        var worldContext = BuildCompactContext(worldState, isLookAround);

        try
        {
            // ── Step 1: Narrator ────────────────────────────────────────────────
            eventStream.Emit(new AgentEvent("Narrating...", "Narrator", AgentEventKind.AgentInvoked));
            string narrativeText;
            try
            {
                var narrativeMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, NarratorSystemPrompt),
                    new(ChatRole.User, $"{worldContext}\nAction: {playerInput}")
                };
                var resp = await chatClient.GetResponseAsync(narrativeMessages, cancellationToken: cancellationToken);
                narrativeText = resp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(narrativeText))
                    narrativeText = "The world resists description. Try a different action.";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Narrator failed");
                narrativeText = $"⚠️ {ex.Message}\n\nTry a different action.";
            }

            logger.LogInformation("Narrative: {Length} chars", narrativeText.Length);
            eventStream.Emit(new AgentEvent("Narrative ready", "Narrator", AgentEventKind.AgentOutput,
                narrativeText[..Math.Min(80, narrativeText.Length)]));

            // ── Step 2: ActionResolver — detect and apply state changes ─────────
            // Skip ActionResolver for opening scene and look-around (pure discovery — no item/move changes)
            var isOpeningScene = playerInput == "Describe the opening scene.";
            if (worldState is not null && !isOpeningScene && !isLookAround)
            {
                await ResolveAndApplyActionAsync(worldState, playerInput, narrativeText, cancellationToken);
                worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            }
            else if (worldState is not null)
            {
                // Opening scene and look-around: just update recent events and save
                worldState.RecentEvents ??= [];
                var evtPrefix = isLookAround ? "Looked around" : "Opening";
                worldState.RecentEvents.Add($"{evtPrefix}: {narrativeText[..Math.Min(60, narrativeText.Length)]}");
                if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
                await worldStateService.SaveState(worldState, cancellationToken);
                worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            }

            // ── Step 2b: Apply item use/equip (keyword detection) ───────────────
            if (!isOpeningScene)
                await ApplyItemInteractionsAsync(saveSlotId, playerInput, narrativeText, cancellationToken);

            // ── Step 2c: Stat decay per turn ─────────────────────────────────────
            if (!isOpeningScene)
                await ApplyStatDecayAsync(saveSlotId, cancellationToken);

            // ── Step 3: Suggestion ──────────────────────────────────────────────
            eventStream.Emit(new AgentEvent("Suggesting...", "Suggestion", AgentEventKind.AgentInvoked));
            List<SuggestedAction> suggestions;
            try
            {
                var hasHidden = (worldState?.HiddenEntities?.Count ?? 0) > 0;
                var nearbyEntities = string.Join(", ", (worldState?.KnownEntities ?? []).Take(4));
                var exits = string.Join(", ", (worldState?.AvailableExits ?? []).Take(2));

                var suggContext = $"Location: {worldState?.CurrentLocation}. Biome: {worldState?.CurrentBiome}.";
                if (!string.IsNullOrEmpty(nearbyEntities)) suggContext += $" Nearby: {nearbyEntities}.";
                if (!string.IsNullOrEmpty(exits)) suggContext += $" Exits: {exits}.";
                if (hasHidden) suggContext += " (things to discover by looking around)";

                var suggMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, SuggestionSystemPrompt),
                    new(ChatRole.User, suggContext)
                };
                var suggResp = await chatClient.GetResponseAsync(suggMessages, cancellationToken: cancellationToken);
                var suggText = suggResp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
                suggestions = ParseSuggestions(suggText);
                logger.LogInformation("Suggestions: {Count}", suggestions.Count);
                eventStream.Emit(new AgentEvent($"{suggestions.Count} suggestions", "Suggestion", AgentEventKind.AgentOutput));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Suggestion call failed, using context-aware defaults");
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

    /// <summary>
    /// Calls the ActionResolver agent to detect state changes, then applies them to the DB.
    /// Handles: item pickup/drop, location change, entity add/remove.
    /// </summary>
    private async Task ResolveAndApplyActionAsync(
        WorldState worldState,
        string playerInput,
        string narrativeText,
        CancellationToken cancellationToken)
    {
        eventStream.Emit(new AgentEvent("Resolving action...", "ActionResolver", AgentEventKind.AgentInvoked));
        try
        {
            var entities = string.Join(", ", (worldState.KnownEntities ?? []).Take(6));
            var resolverMessages = new List<ChatMessage>
            {
                new(ChatRole.System, ActionResolverSystemPrompt),
                new(ChatRole.User, $"Entities: {entities}. Action: {playerInput}. Narrative: {narrativeText[..Math.Min(120, narrativeText.Length)]}")
            };

            var resolverResp = await chatClient.GetResponseAsync(resolverMessages, cancellationToken: cancellationToken);
            var resolverJson = resolverResp.Messages.LastOrDefault()?.Text?.Trim() ?? "";

            if (resolverJson.Contains("```"))
            {
                var s = resolverJson.IndexOf('{');
                var e = resolverJson.LastIndexOf('}');
                if (s >= 0 && e > s) resolverJson = resolverJson[s..(e + 1)];
            }

            var result = JsonSerializer.Deserialize(resolverJson, GameJsonContext.Default.ActionResult);
            if (result is null)
            {
                eventStream.Emit(new AgentEvent("No state changes", "ActionResolver", AgentEventKind.AgentCompleted));
                return;
            }

            logger.LogDebug("ActionResult: {Picked} picked, {Dropped} dropped, location={Location}",
                result.ItemsPickedUp.Count, result.ItemsDropped.Count, result.LocationChanged);

            var changes = new List<string>();

            // Apply: items picked up → add to inventory with inferred effects, remove from world
            foreach (var item in result.ItemsPickedUp)
            {
                if (string.IsNullOrWhiteSpace(item.ItemName)) continue;

                // Guard: only pick up items actually in the world
                var exists = (worldState.KnownEntities ?? []).Any(e =>
                    e.Equals(item.ItemName, StringComparison.OrdinalIgnoreCase));
                if (!exists) continue;

                var effect = InferItemEffect(item.ItemName, item.Description);
                var isDanger = effect.StartsWith("poison");

                if (isDanger)
                {
                    // Dangerous entities damage rather than enter inventory
                    var stats = await worldStateService.GetPlayerStats(worldState.SaveSlotId, cancellationToken)
                                ?? new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = worldState.SaveSlotId };
                    ApplyEffect(stats, effect);
                    await worldStateService.SavePlayerStats(stats, cancellationToken);
                    changes.Add($"touched {item.ItemName} → damaged!");
                    eventStream.Emit(new AgentEvent($"☠️ -{item.ItemName} (dangerous!)", "ActionResolver", AgentEventKind.AgentOutput, $"HP reduced to {stats.Health}"));
                }
                else
                {
                    var invItem = new InventoryItem
                    {
                        Id = Guid.NewGuid(),
                        SaveSlotId = worldState.SaveSlotId,
                        ItemName = item.ItemName,
                        Description = item.Description,
                        Quantity = 1,
                        Effect = effect,
                        IsConsumable = IsConsumableEffect(effect),
                        IsEquippable = IsEquippableEffect(effect)
                    };
                    await store.Set(invItem.Id.ToString(), invItem, GameJsonContext.Default.InventoryItem, cancellationToken);
                    changes.Add($"picked up {item.ItemName}");
                    var effectLabel = string.IsNullOrEmpty(effect) ? "" : $" [{effect}]";
                    eventStream.Emit(new AgentEvent($"🎒 +{item.ItemName}{effectLabel}", "Database", AgentEventKind.ToolResult, item.Description));
                    logger.LogInformation("Inventory: added {Item} effect={Effect}", item.ItemName, effect);
                }

                // Remove from world entities
                if (!result.EntitiesRemoved.Contains(item.ItemName))
                    result.EntitiesRemoved.Add(item.ItemName);
            }

            // Apply: items dropped — only process if player explicitly dropped something
            var isDropAction = playerInput.Contains("drop", StringComparison.OrdinalIgnoreCase)
                            || playerInput.Contains("put down", StringComparison.OrdinalIgnoreCase)
                            || playerInput.Contains("discard", StringComparison.OrdinalIgnoreCase)
                            || playerInput.Contains("leave behind", StringComparison.OrdinalIgnoreCase);

            if (isDropAction)
            {
                foreach (var droppedName in result.ItemsDropped)
                {
                    if (string.IsNullOrWhiteSpace(droppedName)) continue;
                    var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, cancellationToken);
                    var existing = allItems.FirstOrDefault(i =>
                        i.SaveSlotId == worldState.SaveSlotId &&
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

            // Apply: entities removed from world
            worldState.KnownEntities ??= [];
            foreach (var removed in result.EntitiesRemoved)
            {
                var idx = worldState.KnownEntities.FindIndex(e =>
                    e.Equals(removed, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) worldState.KnownEntities.RemoveAt(idx);
            }

            // Apply: new entities discovered
            foreach (var newEntity in result.NewEntities)
            {
                if (!string.IsNullOrWhiteSpace(newEntity) && !worldState.KnownEntities.Contains(newEntity))
                {
                    worldState.KnownEntities.Add(newEntity);
                    changes.Add($"discovered {newEntity}");
                }
            }

            // Apply: location change
            if (!string.IsNullOrWhiteSpace(result.LocationChanged))
            {
                worldState.CurrentLocation = result.LocationChanged;
                changes.Add($"moved to {result.LocationChanged}");
                eventStream.Emit(new AgentEvent($"📍 → {result.LocationChanged}", "ActionResolver", AgentEventKind.AgentOutput));
            }

            // Update recent events
            worldState.RecentEvents ??= [];
            var turnSummary = changes.Count > 0
                ? string.Join(", ", changes)
                : $"{playerInput[..Math.Min(40, playerInput.Length)]}";
            worldState.RecentEvents.Add(turnSummary);
            if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];

            await worldStateService.SaveState(worldState, cancellationToken);

            var summary = changes.Count > 0 ? string.Join(", ", changes) : "no changes";
            eventStream.Emit(new AgentEvent(summary, "ActionResolver", AgentEventKind.AgentCompleted));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ActionResolver failed");
            eventStream.Emit(new AgentEvent("Resolver failed", "ActionResolver", AgentEventKind.Error, ex.Message));
            // Non-fatal: update recent events so the game continues
            worldState.RecentEvents ??= [];
            worldState.RecentEvents.Add($"{playerInput[..Math.Min(40, playerInput.Length)]}");
            if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
            await worldStateService.SaveState(worldState, cancellationToken);
        }
    }

    /// <summary>
    /// Detects "use/eat/drink/equip/wield/wear" keywords in player input and applies item effects
    /// directly from inventory without requiring an LLM call (saves context window).
    /// </summary>
    private async Task ApplyItemInteractionsAsync(Guid saveSlotId, string playerInput, string narrativeText, CancellationToken ct)
    {
        var lower = playerInput.ToLowerInvariant();
        var isUse = lower.Contains("eat") || lower.Contains("drink") || lower.Contains("use") || lower.Contains("consume");
        var isEquip = lower.Contains("equip") || lower.Contains("wield") || lower.Contains("wear") || lower.Contains("put on");

        if (!isUse && !isEquip) return;

        var allItems = await store.GetAll<InventoryItem>(GameJsonContext.Default.InventoryItem, ct);
        var inventory = allItems.Where(i => i.SaveSlotId == saveSlotId).ToList();

        // Find the inventory item mentioned in the player input
        var targetItem = inventory.FirstOrDefault(i =>
            lower.Contains(i.ItemName.ToLowerInvariant()));

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
                if (int.TryParse(targetItem.Effect[6..], out var armorVal))
                    stats.Armor = armorVal;
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
            eventStream.Emit(new AgentEvent($"✨ Used: {targetItem.ItemName}", "ActionResolver", AgentEventKind.AgentOutput,
                $"HP:{stats.Health} Hunger:{stats.Hunger}"));

            // Remove from inventory if consumable
            await store.Remove<InventoryItem>(targetItem.Id.ToString(), ct);
            eventStream.Emit(new AgentEvent($"🗑 Consumed: {targetItem.ItemName}", "Database", AgentEventKind.ToolResult));
        }
    }

    /// <summary>
    /// Applies per-turn stat decay: hunger and tiredness increase; starvation/exhaustion deal damage.
    /// </summary>
    private async Task ApplyStatDecayAsync(Guid saveSlotId, CancellationToken ct)
    {
        try
        {
            var stats = await worldStateService.GetPlayerStats(saveSlotId, ct);
            if (stats is null) return;

            stats.Hunger = Math.Min(100, stats.Hunger + Random.Shared.Next(2, 5));
            stats.Tiredness = Math.Min(100, stats.Tiredness + Random.Shared.Next(1, 3));

            // Starvation and exhaustion deal passive damage
            if (stats.Hunger >= 80)
                stats.Health = Math.Max(0, stats.Health - 2);
            if (stats.Tiredness >= 90)
                stats.Health = Math.Max(0, stats.Health - 1);

            await worldStateService.SavePlayerStats(stats, ct);

            if (stats.Health <= 10)
                eventStream.Emit(new AgentEvent($"⚠️ Critical! HP:{stats.Health}", "GameMaster", AgentEventKind.Error));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stat decay failed (non-fatal)");
        }
    }

    private async Task<WorldState> GenerateWorldStateAsync(Guid saveSlotId, string gameName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Generating world for: {GameName}", gameName);
        eventStream.Emit(new AgentEvent("Building world...", "WorldGen", AgentEventKind.AgentInvoked));

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, WorldGenSystemPrompt),
                new(ChatRole.User, $"Game: \"{gameName}\"")
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var json = response.Messages.LastOrDefault()?.Text?.Trim() ?? "";

            if (json.Contains("```"))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            var generated = JsonSerializer.Deserialize(json, GameJsonContext.Default.WorldState);
            if (generated is not null)
            {
                generated.Id = Guid.NewGuid();
                generated.SaveSlotId = saveSlotId;

                // Sanitize KnownEntities: reject generic placeholder names
                generated.KnownEntities = (generated.KnownEntities ?? [])
                    .Where(e => !string.IsNullOrWhiteSpace(e) &&
                                !e.StartsWith("item", StringComparison.OrdinalIgnoreCase) &&
                                !e.StartsWith("entity", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Sanitize HiddenEntities
                generated.HiddenEntities = (generated.HiddenEntities ?? [])
                    .Where(e => !string.IsNullOrWhiteSpace(e) &&
                                !e.StartsWith("item", StringComparison.OrdinalIgnoreCase) &&
                                !e.StartsWith("entity", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (generated.HiddenEntities.Count < 3)
                    generated.HiddenEntities = DefaultHiddenEntities(generated.CurrentBiome);

                generated.AvailableExits = (generated.AvailableExits ?? [])
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .ToList();
                if (generated.AvailableExits.Count < 1)
                    generated.AvailableExits = DefaultExits(generated.CurrentBiome);

                eventStream.Emit(new AgentEvent($"🌍 {generated.CurrentLocation} ({generated.CurrentBiome})", "WorldGen", AgentEventKind.AgentOutput,
                    $"Hidden: {generated.HiddenEntities.Count} items | Exits: {generated.AvailableExits.Count}"));
                return generated;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "World gen failed, using default");
            eventStream.Emit(new AgentEvent("Using default world", "WorldGen", AgentEventKind.Error));
        }

        return new WorldState
        {
            Id = Guid.NewGuid(),
            SaveSlotId = saveSlotId,
            CurrentBiome = "forest",
            CurrentLocation = "Whispering Glade",
            TimeOfDay = "dawn",
            RegionDescription = "A misty forest clearing where ancient oaks stand sentinel over mossy stones.",
            KnownEntities = ["ancient oak", "mossy stone altar"],
            HiddenEntities = DefaultHiddenEntities("forest"),
            AvailableExits = DefaultExits("forest"),
            RecentEvents = [$"You have arrived in \"{gameName}\"."]
        };
    }

    private static List<string> DefaultHiddenEntities(string biome) => biome.ToLowerInvariant() switch
    {
        "desert" or "badlands" =>
            ["shimmering healing tonic", "salted camel jerky", "rusted iron scimitar", "sun-bleached bone shield", "desert horned viper"],
        "cave" or "dungeon" =>
            ["glowing healing mushroom", "dried cave moss cake", "iron-spiked war club", "iron-banded buckler", "venomous cave spider"],
        "ocean" or "coast" =>
            ["seaweed healing salve", "dried salted fish", "barnacle-crusted cutlass", "crab-shell pauldron", "stonefish trap"],
        "mountain" or "alpine" =>
            ["alpine healing herb", "frozen strip of venison", "stone-tipped climbing axe", "wolf-pelt cloak", "mountain adder"],
        _ => // forest default
            ["luminescent healing berry", "dried mushroom rations", "carved bone hunting knife", "bark-woven leather bracers", "venomous forest asp"]
    };

    private static List<string> DefaultExits(string biome) => biome.ToLowerInvariant() switch
    {
        "desert" or "badlands" => ["ruins to the east", "dried riverbed trail", "sandstone cliff path"],
        "cave" or "dungeon" => ["narrow stone tunnel", "underground river crossing", "crumbling archway"],
        "ocean" or "coast" => ["rocky coastal path", "hidden sea cave", "weathered stone jetty"],
        _ => ["narrow forest trail", "mossy stone bridge", "overgrown ancient road"]
    };

    // ── Item effect helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Infers a game effect from an item's name and description.
    /// Returns "heal:N", "food:N", "weapon:N", "armor:N", "poison:N", or "".
    /// </summary>
    private static string InferItemEffect(string itemName, string description)
    {
        var text = (itemName + " " + description).ToLowerInvariant();

        if (text.Contains("potion") || text.Contains("elixir") || text.Contains("vial") ||
            text.Contains("tonic") || text.Contains("salve") || text.Contains("healing") ||
            text.Contains("luminescent") || text.Contains("glowing") || text.Contains("health"))
            return "heal:30";

        if (text.Contains("ration") || text.Contains("bread") || text.Contains("fruit") ||
            text.Contains("berry") || text.Contains("mushroom") || text.Contains("meat") ||
            text.Contains("fish") || text.Contains("jerky") || text.Contains("dried") ||
            text.Contains("food") || text.Contains("cake") || text.Contains("venison"))
            return "food:25";

        if (text.Contains("sword") || text.Contains("knife") || text.Contains("dagger") ||
            text.Contains("axe") || text.Contains("blade") || text.Contains("spear") ||
            text.Contains("bow") || text.Contains("mace") || text.Contains("staff") ||
            text.Contains("wand") || text.Contains("club") || text.Contains("scimitar") ||
            text.Contains("cutlass") || text.Contains("rapier") || text.Contains("sabre"))
            return "weapon:15";

        if (text.Contains("armor") || text.Contains("shield") || text.Contains("helm") ||
            text.Contains("mail") || text.Contains("bracers") || text.Contains("gauntlet") ||
            text.Contains("cloak") || text.Contains("pauldron") || text.Contains("buckler") ||
            text.Contains("chainmail") || text.Contains("plate") || text.Contains("pelt"))
            return "armor:10";

        if (text.Contains("venom") || text.Contains("poison") || text.Contains("toxic") ||
            text.Contains("asp") || text.Contains("serpent") || text.Contains("viper") ||
            text.Contains("spider") || text.Contains("stonefish") || text.Contains("adder") ||
            text.Contains("venomous") || text.Contains("trap"))
            return "poison:25";

        return "";
    }

    private static bool IsConsumableEffect(string effect) =>
        effect.StartsWith("heal") || effect.StartsWith("food");

    private static bool IsEquippableEffect(string effect) =>
        effect.StartsWith("weapon") || effect.StartsWith("armor");

    private static void ApplyEffect(PlayerStats stats, string effect)
    {
        if (string.IsNullOrEmpty(effect)) return;
        var colon = effect.IndexOf(':');
        var kind = colon >= 0 ? effect[..colon] : effect;
        var value = colon >= 0 && int.TryParse(effect[(colon + 1)..], out var v) ? v : 0;

        switch (kind.ToLowerInvariant())
        {
            case "heal":
                stats.Health = Math.Min(stats.MaxHealth, stats.Health + value);
                break;
            case "food":
                stats.Hunger = Math.Max(0, stats.Hunger - value);
                break;
            case "poison":
            case "danger":
                var damage = Math.Max(1, value - stats.Armor);
                stats.Health = Math.Max(0, stats.Health - damage);
                break;
        }
    }

    // ── Exploration helpers ─────────────────────────────────────────────────

    private static bool IsLookAroundAction(string input)
    {
        var lower = input.ToLowerInvariant();
        return lower.Contains("look around") || lower.Contains("look about") ||
               lower.Contains("examine area") || lower.Contains("examine surroundings") ||
               lower.Contains("survey") || lower.Contains("scan area") ||
               lower.Contains("search the area") || lower.Contains("search area");
    }

    /// <summary>
    /// Compact context kept under ~300 chars to avoid exceeding Apple Intelligence's context window.
    /// </summary>
    private static string BuildCompactContext(WorldState? w, bool includeFull = false)
    {
        if (w is null) return "Location: unknown.";
        var entities = string.Join(", ", (w.KnownEntities ?? []).Take(5));
        var exits = string.Join(", ", (w.AvailableExits ?? []).Take(3));
        var recent = (w.RecentEvents ?? []).LastOrDefault() ?? "";
        var ctx = $"Biome: {w.CurrentBiome}. Location: {w.CurrentLocation}. Time: {w.TimeOfDay}.";
        if (!string.IsNullOrEmpty(entities)) ctx += $" Visible: {entities}.";
        if (includeFull && !string.IsNullOrEmpty(exits)) ctx += $" Exits: {exits}.";
        if (!string.IsNullOrEmpty(recent)) ctx += $" Last: {recent}";
        return ctx;
    }

    private static List<SuggestedAction> ParseSuggestions(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return DefaultSuggestions();
        try
        {
            if (json.Contains("```"))
            {
                var s = json.IndexOf('{');
                var e = json.LastIndexOf('}');
                if (s >= 0 && e > s) json = json[s..(e + 1)];
            }
            var parsed = JsonSerializer.Deserialize(json, GameJsonContext.Default.SuggestedActions);
            if (parsed?.Actions is { Count: > 0 })
                return parsed.Actions;
        }
        catch { }
        return DefaultSuggestions();
    }

    /// <summary>Context-aware default suggestions when the AI call fails.</summary>
    private static List<SuggestedAction> DefaultSuggestions(WorldState? worldState = null)
    {
        var suggestions = new List<SuggestedAction>
        {
            new("Look around", "look around carefully")
        };

        if (worldState?.KnownEntities?.Count > 0)
            suggestions.Add(new("Pick up", $"pick up the {worldState.KnownEntities[0]}"));
        else if (worldState?.AvailableExits?.Count > 0)
            suggestions.Add(new("Travel", $"go to the {worldState.AvailableExits[0]}"));
        else
            suggestions.Add(new("Continue", "continue forward cautiously"));

        suggestions.Add(new("Rest", "rest and recover your strength"));
        return suggestions;
    }

    private async Task PersistJournalEntry(Guid saveSlotId, string text, CancellationToken ct)
    {
        try
        {
            var entry = new JournalEntry
            {
                Id = Guid.NewGuid(),
                SaveSlotId = saveSlotId,
                EntryText = text,
                Timestamp = DateTime.UtcNow,
                Type = JournalEntryType.Narrative
            };
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
            if (slot is not null)
            {
                slot.LastPlayedAt = DateTime.UtcNow;
                await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, ct);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to update last played"); }
    }
}
