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
/// Turn flow: Narrator → ActionResolver → apply state → Suggestion
/// </summary>
public class GameOrchestrator(
    IChatClient chatClient,
    WorldStateService worldStateService,
    SaveSlotService saveSlotService,
    IDocumentStore store,
    EventStream eventStream,
    ILogger<GameOrchestrator> logger)
{
    // Narrator: concise second-person prose, grounded in world state, max 2-3 sentences
    private const string NarratorSystemPrompt = """
        You are the Narrator of an adventure game. Write vivid, second-person, present-tense prose.
        Rules:
        - 2-3 sentences MAXIMUM. Be concise and atmospheric.
        - Use ONLY entities, locations, and features listed in the world context.
        - If the player tries to go somewhere not in the world context, say it cannot be found and describe what IS nearby.
        - If the player tries to pick up something not in the world context, say it is not there.
        - No lists, no headings, just prose.
        """;

    // ActionResolver: analyzes input + narrative to produce structured state changes
    private const string ActionResolverSystemPrompt = """
        You resolve game state changes. Output ONLY valid JSON (no markdown):
        {"ItemsPickedUp":[],"ItemsDropped":[],"LocationChanged":null,"EntitiesRemoved":[],"NewEntities":[]}
        Rules:
        - ItemsPickedUp: array of {"ItemName":"name","Description":"brief desc"} for items the player successfully picked up. Only items that are IN the world entities list.
        - ItemsDropped: array of item name strings the player dropped.
        - LocationChanged: new location name string if player moved, else null.
        - EntitiesRemoved: entity names to remove from the world (picked up or destroyed).
        - NewEntities: new entity names discovered or created.
        - If nothing changed, return all empty/null.
        """;

    // Suggestion: minimal prompt for exactly 3 grounded actions as JSON
    private const string SuggestionSystemPrompt = """
        Output ONLY this JSON (no markdown, no explanation):
        {"Actions":[{"Label":"Label","ActionText":"action text"},{"Label":"Label2","ActionText":"action text 2"},{"Label":"Label3","ActionText":"action text 3"}]}
        Generate exactly 3 short action suggestions (3-6 words each) relevant to the current location.
        Use only things that exist in the world context.
        """;

    private const string WorldGenSystemPrompt = """
        Output ONLY valid JSON (no markdown, no explanation):
        {"CurrentBiome":"forest","CurrentLocation":"Name","TimeOfDay":"dawn","RegionDescription":"2 sentences.","KnownEntities":["specific item 1","specific item 2","specific item 3","specific item 4","specific item 5"],"RecentEvents":["One sentence."]}
        Generate a starting world for the game name given. Match the theme.
        KnownEntities MUST be 5 specific, descriptive, thematic items/features (e.g. "rusted iron lantern", "moss-covered stone altar", "tattered treasure map"). Never use generic placeholders like "item1".
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
        var worldContext = BuildCompactContext(worldState);

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
            // Only resolve for non-opening-scene turns to avoid unnecessary LLM calls
            if (worldState is not null && playerInput != "Describe the opening scene.")
            {
                await ResolveAndApplyActionAsync(worldState, playerInput, narrativeText, cancellationToken);
                // Reload world state after changes were applied
                worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            }
            else if (worldState is not null)
            {
                // Still update recent events for opening scene
                worldState.RecentEvents ??= [];
                worldState.RecentEvents.Add($"Opening: {narrativeText[..Math.Min(60, narrativeText.Length)]}");
                if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
                await worldStateService.SaveState(worldState, cancellationToken);
                worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            }

            // ── Step 3: Suggestion ──────────────────────────────────────────────
            eventStream.Emit(new AgentEvent("Suggesting...", "Suggestion", AgentEventKind.AgentInvoked));
            List<SuggestedAction> suggestions;
            try
            {
                var nearbyEntities = string.Join(", ", (worldState?.KnownEntities ?? []).Take(4));
                var suggMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, SuggestionSystemPrompt),
                    new(ChatRole.User, $"Location: {worldState?.CurrentLocation}. Nearby: {nearbyEntities}.")
                };
                var suggResp = await chatClient.GetResponseAsync(suggMessages, cancellationToken: cancellationToken);
                var suggText = suggResp.Messages.LastOrDefault()?.Text?.Trim() ?? "";
                suggestions = ParseSuggestions(suggText);
                logger.LogInformation("Suggestions: {Count}", suggestions.Count);
                eventStream.Emit(new AgentEvent($"{suggestions.Count} suggestions", "Suggestion", AgentEventKind.AgentOutput));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Suggestion call failed, using defaults");
                eventStream.Emit(new AgentEvent("Default suggestions", "Suggestion", AgentEventKind.AgentCompleted));
                suggestions = DefaultSuggestions();
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
                DefaultSuggestions());
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

            logger.LogDebug("ActionResult: {Picked} picked, {Dropped} dropped, location={Location}, removed={Removed}, new={New}",
                result.ItemsPickedUp.Count, result.ItemsDropped.Count, result.LocationChanged,
                string.Join(",", result.EntitiesRemoved), string.Join(",", result.NewEntities));

            var changes = new List<string>();

            // Apply: items picked up → add to inventory, remove from world
            foreach (var item in result.ItemsPickedUp)
            {
                if (string.IsNullOrWhiteSpace(item.ItemName)) continue;

                // Guard: only pick up items that are actually in the world
                var exists = (worldState.KnownEntities ?? []).Any(e =>
                    e.Equals(item.ItemName, StringComparison.OrdinalIgnoreCase));
                if (!exists) continue;

                var invItem = new InventoryItem
                {
                    Id = Guid.NewGuid(),
                    SaveSlotId = worldState.SaveSlotId,
                    ItemName = item.ItemName,
                    Description = item.Description,
                    Quantity = 1
                };
                await store.Set(invItem.Id.ToString(), invItem, GameJsonContext.Default.InventoryItem, cancellationToken);
                changes.Add($"picked up {item.ItemName}");
                eventStream.Emit(new AgentEvent($"🎒 +{item.ItemName}", "Database", AgentEventKind.ToolResult, item.Description));
                logger.LogInformation("Inventory: added {Item}", item.ItemName);

                // Remove from world entities
                if (!result.EntitiesRemoved.Contains(item.ItemName))
                    result.EntitiesRemoved.Add(item.ItemName);
            }

            // Apply: items dropped → only process if player explicitly dropped something
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
            // Non-fatal: just update recent events so the game continues
            worldState.RecentEvents ??= [];
            worldState.RecentEvents.Add($"{playerInput[..Math.Min(40, playerInput.Length)]}");
            if (worldState.RecentEvents.Count > 3) worldState.RecentEvents = worldState.RecentEvents[^3..];
            await worldStateService.SaveState(worldState, cancellationToken);
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
                // Sanitize: reject generic placeholder names
                generated.KnownEntities = (generated.KnownEntities ?? [])
                    .Where(e => !string.IsNullOrWhiteSpace(e) &&
                                !e.StartsWith("item", StringComparison.OrdinalIgnoreCase) &&
                                !e.StartsWith("entity", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (generated.KnownEntities.Count < 3)
                    generated.KnownEntities = DefaultEntities(generated.CurrentBiome);
                eventStream.Emit(new AgentEvent($"🌍 {generated.CurrentLocation} ({generated.CurrentBiome})", "WorldGen", AgentEventKind.AgentOutput,
                    string.Join(", ", generated.KnownEntities.Take(3))));
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
            KnownEntities = DefaultEntities("forest"),
            RecentEvents = [$"You have arrived in \"{gameName}\"."]
        };
    }

    private static List<string> DefaultEntities(string biome) => biome.ToLowerInvariant() switch
    {
        "desert" or "badlands" => ["cracked clay pot", "bleached animal skull", "rusted iron compass", "sun-faded scroll", "obsidian shard"],
        "cave" or "dungeon" => ["flickering torch", "carved stone tablet", "iron-banded chest", "stalactite fragment", "rusted key"],
        "ocean" or "coast" => ["driftwood plank", "barnacle-covered chest", "sailor's compass", "salt-encrusted bottle", "torn fishing net"],
        _ => ["ancient oak staff", "moss-covered journal", "carved bone whistle", "iron lantern", "mysterious glowing stone"]
    };

    /// <summary>
    /// Compact context kept under ~300 chars to avoid exceeding Apple Intelligence's context window.
    /// </summary>
    private static string BuildCompactContext(WorldState? w)
    {
        if (w is null) return "Location: unknown.";
        var entities = string.Join(", ", (w.KnownEntities ?? []).Take(5));
        var recent = (w.RecentEvents ?? []).LastOrDefault() ?? "";
        return $"Biome: {w.CurrentBiome}. Location: {w.CurrentLocation}. Time: {w.TimeOfDay}. Here: {entities}. Last: {recent}";
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

    private static List<SuggestedAction> DefaultSuggestions() =>
    [
        new SuggestedAction("Explore", "explore the area carefully"),
        new SuggestedAction("Look around", "look around for anything interesting"),
        new SuggestedAction("Wait", "wait and observe quietly"),
    ];

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

