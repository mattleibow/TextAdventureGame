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
/// The AI Game Master. Drives every player turn via two phases:
/// Phase 1 — tool calling: AI invokes game tools (move, look_around, pick_up_item, etc.) to execute
///            the player's action and returns a vivid narrative.
/// Phase 2 — structured output: AI returns <see cref="SuggestedActions"/> for the suggestion buttons.
/// No response parsing. No if/else game logic. AI decides everything.
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
    /// System prompt for Phase 1 (tool-calling). Guides the AI to use tools and write narrative.
    /// Deliberately omits any format instructions — structured output handles suggestions separately.
    /// </summary>
    private const string GameMasterSystemPrompt = """
        You are the Game Master of a text adventure. You have tools to read and change the game world.

        When the player gives you an action, follow these steps IN ORDER:
        1. Call get_world_state to understand the current situation.
        2. Call get_current_tile to get the scene description.
        3. Call EXACTLY ONE action tool based on what the player wants:
           - "pick up", "take", "grab", "collect" → call pick_up_item (NEVER call look_around instead)
           - "go", "walk", "move", "travel", any direction → call move_player
           - "look", "search", "explore", "examine" → call look_around
           - "drop", "put down", "discard" → call drop_item
           - "eat", "drink", "use", "consume" → call use_item
           - "equip", "wield", "wear" → call equip_item
        4. Write an atmospheric 2-4 sentence narrative in second-person present tense.
           React to what the tools returned. Focus on sights, sounds, and smells.

        CRITICAL RULES:
        - Call pick_up_item when the player wants to pick something up. NEVER narrate picking up without calling pick_up_item.
        - Call move_player when the player wants to move. NEVER narrate moving without calling move_player.
        - Only pick up items that exist in the PORTABLE ITEMS list from get_world_state.
        - DO NOT call look_around when the player wants to pick something up or move.
        """;

    /// <summary>System prompt for Phase 2 (structured suggestions). Minimal, focused.</summary>
    private const string SuggestionSystemPrompt = """
        You are a game assistant for a text adventure. Suggest exactly 3 player actions.
        Each suggestion must be ONE SINGLE action only — never compound or bundle multiple actions.
        Good: "go north", "pick up the Zlade", "look around".
        Bad: "go north and pick up the sword", "look around then talk to the guard".
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

        return await ProcessTurnAsync(saveSlotId, "Describe the opening scene. Focus on atmosphere, sights, and sounds.", ct);
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

        // ── Phase 1: Tool calling — AI uses tools to execute action and write narrative ──
        var toolCtx = new ToolContext(saveSlotId, gameName, worldStateService, mapService, store, eventStream, logger);
        var toolRegistry = new ToolRegistry(toolCtx);
        var phase1Options = new ChatOptions { Tools = toolRegistry.GetAllTools() };

        var phase1Messages = new List<ChatMessage>
        {
            new(ChatRole.System, GameMasterSystemPrompt),
            new(ChatRole.User, playerInput)
        };

        string narrative;
        try
        {
            eventStream.Emit(new AgentEvent("📋 GM prompt", "GameMaster", AgentEventKind.Prompt,
                playerInput[..Math.Min(60, playerInput.Length)],
                $"[System]\n{GameMasterSystemPrompt}\n\n[User]\n{playerInput}"));

            // UseFunctionInvocation() middleware manages the tool-calling loop.
            // AI calls tools, gets results, reasons, calls more tools, then writes narrative.
            var phase1Response = await chatClient.GetResponseAsync(phase1Messages, phase1Options, ct);
            narrative = phase1Response.Messages.LastOrDefault()?.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(narrative))
                narrative = "The world shifts imperceptibly around you.";

            eventStream.Emit(new AgentEvent("💬 GM narrative", "GameMaster", AgentEventKind.Response,
                narrative[..Math.Min(80, narrative.Length)], narrative));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameMaster phase 1 (tool calling) failed");
            narrative = $"The world pauses. ({ex.Message})";
            eventStream.Emit(new AgentEvent("Phase 1 failed", "GameMaster", AgentEventKind.Error, ex.Message));
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
