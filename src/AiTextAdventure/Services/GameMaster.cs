using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;
using AiTextAdventure.Services.Tools;

namespace AiTextAdventure.Services;

public record GameTurnResult(string Narrative, List<SuggestedAction> Suggestions);

/// <summary>
/// The AI Game Master. Drives every player turn via three phases:
/// Phase 1a — tool executor: AI calls game tools (move, look_around, pick_up_item, etc.) to execute
///             the player's action. Returns only "Done." — narrative is NOT its job.
/// Phase 1b — narrator: A focused AI call that reads the tool results and writes vivid atmospheric prose.
///             Because it only writes (no tools), it produces better narrative quality.
/// Phase 2 — structured output: AI returns <see cref="SuggestedActions"/> for the suggestion buttons.
/// </summary>
public class GameMaster(
    IChatClient chatClient,
    WorldStateService worldStateService,
    SaveSlotService saveSlotService,
    MapService mapService,
    IDocumentStore store,
    EventStream eventStream,
    ILogger<GameMaster> logger)
{
    private int _turnNumber = 0;

    /// <summary>
    /// System prompt for Phase 1a (tool executor). Only calls tools — deliberately no narrative.
    /// </summary>
    private const string ExecutorSystemPrompt = """
        You are the action executor for a text adventure game. You have tools to read and change the game world.

        You ONLY run when a player has EXPLICITLY requested an action. Never call action tools speculatively.

        When the player gives you an action, follow these steps IN ORDER:
        1. Call get_world_state to understand the current situation.
        2. Call get_current_tile to get the scene description. (optional — skip if you have enough context)
        3. Call EXACTLY the action tool(s) the player explicitly requested:
           - "pick up", "take", "grab", "collect" → call pick_up_item
           - "go", "walk", "move", "travel", any direction → call move_player
           - "look", "search", "explore", "examine" → call look_around
           - "drop", "put down", "discard" → call drop_item
           - "eat", "drink", "use", "consume" → call use_item
           - "equip", "wield", "wear" → call equip_item
        4. STOP. Respond with only: "Done."

        CRITICAL RULES:
        - Only call action tools for actions the player EXPLICITLY requested in this turn.
        - NEVER pick up items, move, or interact with anything unless the player asked you to.
        - After calling the requested tool(s), STOP IMMEDIATELY. Do not call any additional tools.
        - Do not call drop_item unless the player said "drop". Do not call pick_up_item unless they said "pick up".
        - Only pick up items that exist in the PORTABLE ITEMS list from get_world_state.
        - DO NOT write any story, narrative, or description — only call tools, then say "Done."
        """;

    /// <summary>System prompt for Phase 1b (narrator). Pure storytelling — no tools, no game logic.</summary>
    private const string NarratorSystemPrompt = """
        You are the Narrator of a text adventure game. Write vivid, atmospheric prose.
        You will be given what just happened (tool results) and where the player is.
        Write exactly 2-4 sentences in second-person present tense ("You...").
        Focus on sights, sounds, smells, and feeling. Do not list items mechanically.
        IMPORTANT: Use all item and place names EXACTLY as written — do not translate or rename them.
        """;

    /// <summary>System prompt for Phase 2 (structured suggestions). Minimal, focused.</summary>
    private const string SuggestionSystemPrompt = """
        You are a game assistant for a text adventure. Suggest exactly 3 player actions.
        Each suggestion must be ONE SINGLE action only — never compound or bundle multiple actions.
        Good: "go north", "pick up the Zlade", "look around".
        Bad: "go north and pick up the Zword", "look around then talk to the guard".
        Include at least one movement and one item/environment interaction.
        """;

    public async Task<GameTurnResult> InitializeGameAsync(Guid saveSlotId, CancellationToken ct = default)
    {
        var slot = await saveSlotService.GetSaveSlot(saveSlotId, ct);
        var gameName = slot?.Name ?? "Adventure";
        logger.LogInformation("Initializing game for {SaveSlotId} ({Name})", saveSlotId, gameName);

        var worldState = await worldStateService.GetCurrentState(saveSlotId, ct);
        if (worldState is null)
        {
            eventStream.Emit(new AgentEvent($"New game: {gameName}", "GameMaster", AgentEventKind.AgentInvoked));

            await mapService.GenerateSurroundingTiles(saveSlotId, 0, 0, gameName, ct);

            var startTile = await mapService.GetTile(saveSlotId, 0, 0, ct)
                ?? new MapTile { Id = Guid.NewGuid(), SaveSlotId = saveSlotId, X = 0, Y = 0, Biome = "forest", LocationName = "Clearing", Description = "A forest clearing." };
            startTile.IsVisited = true;
            startTile.DiscoveredAt = DateTime.UtcNow;
            await store.Set(startTile.Id.ToString(), startTile, GameJsonContext.Default.MapTile, ct);

            worldState = new WorldState
            {
                Id = Guid.NewGuid(), SaveSlotId = saveSlotId,
                TimeOfDay = "dawn", PlayerX = 0, PlayerY = 0,
                RecentEvents = [$"You have arrived in \"{gameName}\"."]
            };
            mapService.SyncTileToWorldState(worldState, startTile);
            await worldStateService.SaveState(worldState, ct);
            logger.LogInformation("Generated world: {Biome} / {Location}", worldState.CurrentBiome, worldState.CurrentLocation);

            var stats = new PlayerStats { Id = Guid.NewGuid(), SaveSlotId = saveSlotId };
            await worldStateService.SavePlayerStats(stats, ct);
            eventStream.Emit(new AgentEvent("⚔️ Adventurer created HP:100", "GameMaster", AgentEventKind.AgentOutput));
        }
        else
        {
            eventStream.Emit(new AgentEvent($"Resuming: {worldState.CurrentLocation} ({worldState.PlayerX},{worldState.PlayerY})", "GameMaster", AgentEventKind.AgentInvoked));
            _ = mapService.GenerateSurroundingTiles(saveSlotId, worldState.PlayerX, worldState.PlayerY, gameName, ct);
        }

        return await DescribeOpeningSceneAsync(saveSlotId, ct);
    }

    /// <summary>
    /// Generates the opening scene narrative WITHOUT running the Executor.
    /// Skipping the Executor prevents the AI from calling action tools (pick_up_item, etc.)
    /// in response to a "describe the scene" prompt — which caused auto-collection of all items.
    /// </summary>
    private async Task<GameTurnResult> DescribeOpeningSceneAsync(Guid saveSlotId, CancellationToken ct)
    {
        eventStream.Emit(new AgentEvent("Turn 0", "GameMaster", AgentEventKind.TurnStart, "Opening scene"));
        eventStream.Emit(new AgentEvent("▶ Opening scene (no tools)", "GameMaster", AgentEventKind.AgentInvoked));

        var worldState = await worldStateService.GetCurrentState(saveSlotId, ct);

        string narrative;
        try
        {
            var narratorContext = BuildNarratorContext(
                "Describe the opening scene vividly.",
                [],   // No tool actions — this is a pure scene description
                worldState);

            eventStream.Emit(new AgentEvent("📖 Narrator", "Narrator", AgentEventKind.Prompt,
                narratorContext[..Math.Min(60, narratorContext.Length)],
                $"[System]\n{NarratorSystemPrompt}\n\n[User]\n{narratorContext}"));

            var response = await chatClient.GetResponseAsync(
                [new(ChatRole.System, NarratorSystemPrompt), new(ChatRole.User, narratorContext)],
                cancellationToken: ct);

            narrative = response.Messages.LastOrDefault()?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(narrative))
                narrative = "You stand at the threshold of a new adventure.";

            eventStream.Emit(new AgentEvent("💬 Narrative", "Narrator", AgentEventKind.Response,
                narrative[..Math.Min(80, narrative.Length)], narrative));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Opening scene narrator failed");
            narrative = "You stand at the threshold of a new adventure, the world waiting to be explored.";
            eventStream.Emit(new AgentEvent("Narrator failed", "Narrator", AgentEventKind.Error, ex.Message));
        }

        List<SuggestedAction> suggestions;
        try
        {
            var suggCtx = BuildSuggestionContext(worldState, narrative);
            var suggResponse = await chatClient.GetResponseAsync<SuggestedActions>(
                [new(ChatRole.System, SuggestionSystemPrompt), new(ChatRole.User, suggCtx)],
                cancellationToken: ct);
            suggestions = suggResponse.Result?.Actions is { Count: > 0 } acts ? acts : DefaultSuggestions();
            eventStream.Emit(new AgentEvent("💬 Suggestions", "Suggestion", AgentEventKind.Response,
                $"{suggestions.Count} actions", string.Join(", ", suggestions.Select(s => s.Label))));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Opening scene suggestions failed — using defaults");
            suggestions = DefaultSuggestions();
        }

        await PersistJournalEntry(saveSlotId, narrative, ct);
        await UpdateLastPlayed(saveSlotId, ct);
        eventStream.Emit(new AgentEvent("Turn complete", "GameMaster", AgentEventKind.WorkflowComplete));
        return new GameTurnResult(narrative, suggestions);
    }

    public async Task<GameTurnResult> ProcessTurnAsync(Guid saveSlotId, string playerInput, CancellationToken ct = default)
    {
        var turnNum = Interlocked.Increment(ref _turnNumber);
        logger.LogInformation("Turn {Turn}: {Input}", turnNum, playerInput[..Math.Min(60, playerInput.Length)]);

        // Emit turn start so EventsPanelViewModel can render a collapsible group header
        eventStream.Emit(new AgentEvent($"Turn {turnNum}", "GameMaster", AgentEventKind.TurnStart,
            playerInput[..Math.Min(60, playerInput.Length)]));
        eventStream.Emit(new AgentEvent($"▶ \"{playerInput[..Math.Min(40, playerInput.Length)]}\"", "GameMaster", AgentEventKind.AgentInvoked));

        var slot = await saveSlotService.GetSaveSlot(saveSlotId, ct);
        var gameName = slot?.Name ?? "Adventure";

        // ── Phase 1a: Action Executor — AI calls tools, does NOT write narrative ──
        var toolCtx = new ToolContext(saveSlotId, gameName, worldStateService, mapService, store, eventStream, logger);
        var toolRegistry = new ToolRegistry(toolCtx);
        var executorOptions = new ChatOptions { Tools = toolRegistry.GetAllTools() };

        var executorMessages = new List<ChatMessage>
        {
            new(ChatRole.System, ExecutorSystemPrompt),
            new(ChatRole.User, playerInput)
        };

        try
        {
            eventStream.Emit(new AgentEvent("🔧 Executor", "Executor", AgentEventKind.Prompt,
                playerInput[..Math.Min(60, playerInput.Length)],
                $"[System]\n{ExecutorSystemPrompt}\n\n[User]\n{playerInput}"));

            // UseFunctionInvocation() middleware manages the tool-calling loop.
            await chatClient.GetResponseAsync(executorMessages, executorOptions, ct);

            eventStream.Emit(new AgentEvent("✅ Actions complete", "Executor", AgentEventKind.Response,
                $"{toolCtx.ActionLog.Count} action(s) performed",
                string.Join("\n", toolCtx.ActionLog)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameMaster Phase 1a (executor) failed");
            eventStream.Emit(new AgentEvent("Executor failed", "Executor", AgentEventKind.Error, ex.Message));
        }

        // ── Phase 1b: Narrator — writes vivid prose from tool results ──
        string narrative;
        try
        {
            var worldState = await worldStateService.GetCurrentState(saveSlotId, ct);
            var narratorContext = BuildNarratorContext(playerInput, toolCtx.ActionLog, worldState);

            eventStream.Emit(new AgentEvent("📖 Narrator", "Narrator", AgentEventKind.Prompt,
                narratorContext[..Math.Min(60, narratorContext.Length)],
                $"[System]\n{NarratorSystemPrompt}\n\n[User]\n{narratorContext}"));

            var narratorResponse = await chatClient.GetResponseAsync(
                [new(ChatRole.System, NarratorSystemPrompt), new(ChatRole.User, narratorContext)],
                cancellationToken: ct);

            narrative = narratorResponse.Messages.LastOrDefault()?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(narrative))
                narrative = "The world shifts imperceptibly around you.";

            eventStream.Emit(new AgentEvent("💬 Narrative", "Narrator", AgentEventKind.Response,
                narrative[..Math.Min(80, narrative.Length)], narrative));
        }
        catch (Exception ex) when (ex.Message.Contains("unsafe", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("content", StringComparison.OrdinalIgnoreCase))
        {
            // Content filter hit — retry with a minimal safe prompt
            logger.LogWarning("Narrator content filter hit, retrying with minimal prompt");
            try
            {
                var worldState2 = await worldStateService.GetCurrentState(saveSlotId, ct);
                var minimalCtx = $"Player is in {worldState2?.CurrentLocation ?? "an unknown place"} ({worldState2?.CurrentBiome ?? "unknown"} biome). Write 2 atmospheric sentences about their surroundings.";
                var retryResponse = await chatClient.GetResponseAsync(
                    [new(ChatRole.System, NarratorSystemPrompt), new(ChatRole.User, minimalCtx)],
                    cancellationToken: ct);
                narrative = retryResponse.Messages.LastOrDefault()?.Text?.Trim()
                    ?? "The world shifts imperceptibly around you.";
                eventStream.Emit(new AgentEvent("💬 Narrative (retry)", "Narrator", AgentEventKind.Response,
                    narrative[..Math.Min(80, narrative.Length)], narrative));
            }
            catch (Exception retryEx)
            {
                logger.LogError(retryEx, "Narrator retry also failed");
                narrative = "The world shifts imperceptibly around you.";
                eventStream.Emit(new AgentEvent("Narrator failed", "Narrator", AgentEventKind.Error, retryEx.Message));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameMaster Phase 1b (narrator) failed");
            narrative = $"The world pauses. ({ex.Message})";
            eventStream.Emit(new AgentEvent("Narrator failed", "Narrator", AgentEventKind.Error, ex.Message));
        }

        // ── Phase 2: Structured suggestions — GetResponseAsync<SuggestedActions> ──
        List<SuggestedAction> suggestions;
        try
        {
            var worldState = await worldStateService.GetCurrentState(saveSlotId, ct);
            var suggCtx = BuildSuggestionContext(worldState, narrative);

            eventStream.Emit(new AgentEvent("📋 Suggestion prompt", "Suggestion", AgentEventKind.Prompt,
                suggCtx[..Math.Min(60, suggCtx.Length)],
                $"[System]\n{SuggestionSystemPrompt}\n\n[User]\n{suggCtx}"));

            var suggResponse = await chatClient.GetResponseAsync<SuggestedActions>(
                [new(ChatRole.System, SuggestionSystemPrompt), new(ChatRole.User, suggCtx)],
                cancellationToken: ct);

            var rawSugg = suggResponse.Messages.LastOrDefault()?.Text?.Trim() ?? "";
            suggestions = suggResponse.Result?.Actions is { Count: > 0 } acts ? acts : DefaultSuggestions();

            eventStream.Emit(new AgentEvent("💬 Suggestions", "Suggestion", AgentEventKind.Response,
                $"{suggestions.Count} actions", rawSugg));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GameMaster phase 2 (suggestions) failed — using defaults");
            suggestions = DefaultSuggestions();
        }

        // ── Stat decay: automatic per-turn game mechanic ──
        await ApplyStatDecayAsync(saveSlotId, ct);

        await PersistJournalEntry(saveSlotId, narrative, ct);
        await UpdateLastPlayed(saveSlotId, ct);
        eventStream.Emit(new AgentEvent("Turn complete", "GameMaster", AgentEventKind.WorkflowComplete));
        return new GameTurnResult(narrative, suggestions);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Builds a compact context string for the Narrator (Phase 1b).</summary>
    private static string BuildNarratorContext(string playerInput, List<string> actionLog, WorldState? worldState)
    {
        var parts = new List<string>();
        // Give the Narrator a sanitized action description. We avoid raw player input (which may
        // contain item names that Apple Intelligence re-describes with real-world terms, triggering
        // the content filter). Instead, classify the action type to give minimal but useful context.
        var actionDesc = ClassifyAction(playerInput);
        parts.Add($"The player {actionDesc}.");
        if (actionLog.Count > 0)
            parts.Add($"What happened:\n{string.Join("\n", actionLog)}");
        if (worldState is not null)
            parts.Add($"Current location: {worldState.CurrentLocation} ({worldState.CurrentBiome})");
        parts.Add("Write 2-4 sentences of vivid atmospheric narrative:");
        return string.Join("\n", parts);
    }

    /// <summary>Classifies a player input into a safe, generic action description for the Narrator.</summary>
    private static string ClassifyAction(string input)
    {
        var lower = input.ToLowerInvariant();
        if (lower.Contains("pick up") || lower.Contains("take") || lower.Contains("grab") || lower.Contains("collect"))
            return "picked up an item from the ground";
        if (lower.Contains("go ") || lower.Contains("walk") || lower.Contains("move") || lower.Contains("travel")
            || lower.Contains("north") || lower.Contains("south") || lower.Contains("east") || lower.Contains("west"))
            return "moved to a new location";
        if (lower.Contains("look") || lower.Contains("search") || lower.Contains("explore") || lower.Contains("examine"))
            return "looked around carefully";
        if (lower.Contains("drop") || lower.Contains("put down") || lower.Contains("discard"))
            return "dropped an item";
        if (lower.Contains("eat") || lower.Contains("drink") || lower.Contains("use") || lower.Contains("consume"))
            return "used a consumable item";
        if (lower.Contains("equip") || lower.Contains("wield") || lower.Contains("wear"))
            return "equipped an item";
        return "took an action";
    }

    /// <summary>Builds a compact context string for the suggestion AI call.</summary>
    private static string BuildSuggestionContext(WorldState? worldState, string narrative)
    {
        var parts = new List<string>();
        if (worldState is not null)
        {
            parts.Add($"Location: {worldState.CurrentLocation} ({worldState.CurrentBiome}).");
            var items = worldState.KnownEntities ?? [];
            if (items.Count > 0) parts.Add($"Visible items: {string.Join(", ", items.Take(3))}.");
            var landmarks = worldState.LandmarkEntities ?? [];
            if (landmarks.Count > 0) parts.Add($"Landmarks: {string.Join(", ", landmarks.Take(2))}.");
            var hidden = worldState.HiddenEntities ?? [];
            if (hidden.Count > 0) parts.Add("Unexplored items remain here.");
        }
        parts.Add($"Just happened: {narrative[..Math.Min(100, narrative.Length)]}");
        return string.Join(' ', parts);
    }

    private static List<SuggestedAction> DefaultSuggestions() =>
    [
        new("Look around", "look around carefully"),
        new("Go north", "go north"),
        new("Check stats", "check my health and hunger"),
    ];

    private async Task ApplyStatDecayAsync(Guid saveSlotId, CancellationToken ct)
    {
        try
        {
            var stats = await worldStateService.GetPlayerStats(saveSlotId, ct);
            if (stats is null) return;
            stats.Hunger = Math.Min(100, stats.Hunger + Random.Shared.Next(2, 5));
            stats.Tiredness = Math.Min(100, stats.Tiredness + Random.Shared.Next(1, 3));
            if (stats.Hunger >= 80) stats.Health = Math.Max(0, stats.Health - 2);
            if (stats.Tiredness >= 90) stats.Health = Math.Max(0, stats.Health - 1);
            await worldStateService.SavePlayerStats(stats, ct);
            if (stats.Health <= 10)
                eventStream.Emit(new AgentEvent($"⚠️ Critical! HP:{stats.Health}", "GameMaster", AgentEventKind.Error));
        }
        catch (Exception ex) { logger.LogWarning(ex, "Stat decay failed (non-fatal)"); }
    }

    private async Task PersistJournalEntry(Guid saveSlotId, string text, CancellationToken ct)
    {
        try
        {
            var entry = new JournalEntry
            {
                Id = Guid.NewGuid(), SaveSlotId = saveSlotId,
                EntryText = text, Timestamp = DateTime.UtcNow, Type = JournalEntryType.Narrative
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
