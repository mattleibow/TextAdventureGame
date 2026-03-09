using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services;

public record GameTurnResult(string Narrative, List<SuggestedAction> Suggestions);

/// <summary>
/// The AI Game Master. Processes player turns using tool calling.
/// The AI decides which tools to invoke (move, look_around, pick_up_item, etc.),
/// reads and writes game state through them, then narrates what happened.
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
    private const string GameMasterSystemPrompt = """
        You are the Game Master of a text adventure. You control the world through your tools.
        
        When the player gives you an action:
        1. Call get_world_state to understand the current situation (items, stats, recent events)
        2. Execute the player's intent using the right tools:
           - Moving somewhere → move_player
           - Looking around or searching → look_around
           - Picking up an item → pick_up_item (only items listed in PORTABLE ITEMS)
           - Dropping an item → drop_item
           - Eating, drinking, using an item → use_item
           - Equipping a weapon or armor → equip_item
           - Examining a landmark → describe it from get_current_tile data (no tool needed)
        3. Write a vivid 2-4 sentence narrative in second-person present tense ("You step into...")
        4. End your response with this exact line: SUGGESTIONS: <action1> | <action2> | <action3>
        
        Rules:
        - You are the sole decision-maker. Use tools freely to read and write game state.
        - Never invent items that aren't in PORTABLE ITEMS — only pick up what exists.
        - Be creative and atmospheric in your narrative. React to what the tools return.
        - Always end with SUGGESTIONS offering 3 distinct next actions (include at least one movement).
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

        return await ProcessTurnAsync(saveSlotId, "Describe the opening scene vividly.", ct);
    }

    public async Task<GameTurnResult> ProcessTurnAsync(Guid saveSlotId, string playerInput, CancellationToken ct = default)
    {
        logger.LogInformation("Turn: {Input}", playerInput[..Math.Min(60, playerInput.Length)]);
        eventStream.Emit(new AgentEvent($"▶ \"{playerInput[..Math.Min(40, playerInput.Length)]}\"", "GameMaster", AgentEventKind.AgentInvoked));

        var slot = await saveSlotService.GetSaveSlot(saveSlotId, ct);
        var gameName = slot?.Name ?? "Adventure";

        // Create per-turn tools capturing the current save slot context
        var tools = new GameTools(saveSlotId, gameName, worldStateService, mapService, store, eventStream, logger);
        var options = new ChatOptions { Tools = tools.GetAllTools() };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, GameMasterSystemPrompt),
            new(ChatRole.User, playerInput)
        };

        string narrative;
        List<SuggestedAction> suggestions;
        try
        {
            eventStream.Emit(new AgentEvent("📋 Prompt", "GameMaster", AgentEventKind.Prompt,
                playerInput[..Math.Min(60, playerInput.Length)],
                $"[System]\n{GameMasterSystemPrompt}\n\n[User]\n{playerInput}"));

            // UseFunctionInvocation middleware handles the tool-calling loop automatically.
            // The AI calls tools, receives results, then produces a final narrative response.
            var response = await chatClient.GetResponseAsync(messages, options, ct);
            var text = response.Messages.LastOrDefault()?.Text?.Trim() ?? "";

            eventStream.Emit(new AgentEvent("💬 Response", "GameMaster", AgentEventKind.Response,
                text[..Math.Min(80, text.Length)], text));

            (narrative, suggestions) = ParseTurnResponse(text);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameMaster turn failed");
            narrative = $"The world seems to pause for a moment. ({ex.Message})";
            suggestions = DefaultSuggestions();
            eventStream.Emit(new AgentEvent("Turn failed", "GameMaster", AgentEventKind.Error, ex.Message));
        }

        // Stat decay: automatic per-turn game mechanic (not an AI decision)
        await ApplyStatDecayAsync(saveSlotId, ct);

        await PersistJournalEntry(saveSlotId, narrative, ct);
        await UpdateLastPlayed(saveSlotId, ct);
        eventStream.Emit(new AgentEvent("Turn complete", "GameMaster", AgentEventKind.WorkflowComplete));
        return new GameTurnResult(narrative, suggestions);
    }

    // ── Response parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses the AI's free-text response. The Game Master ends responses with:
    ///   SUGGESTIONS: action1 | action2 | action3
    /// Everything before that line is the narrative.
    /// </summary>
    private static (string narrative, List<SuggestedAction> suggestions) ParseTurnResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ("The world holds its breath.", DefaultSuggestions());

        var suggIdx = text.LastIndexOf("SUGGESTIONS:", StringComparison.OrdinalIgnoreCase);
        if (suggIdx < 0)
            return (text.Trim(), DefaultSuggestions());

        var narrative = text[..suggIdx].Trim();
        var suggLine = text[(suggIdx + 12)..].Trim();
        var parts = suggLine.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var suggestions = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                var action = p.Trim();
                var words = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var label = string.Join(' ', words.Take(3));
                return new SuggestedAction(label, action);
            })
            .Take(5)
            .ToList();

        return (string.IsNullOrWhiteSpace(narrative) ? text.Trim() : narrative,
                suggestions.Count > 0 ? suggestions : DefaultSuggestions());
    }

    private static List<SuggestedAction> DefaultSuggestions() =>
    [
        new("Look around", "look around carefully"),
        new("Go north", "go north"),
        new("Check stats", "check my health and hunger"),
    ];

    // ── Stat decay (automatic per-turn game mechanic) ─────────────────────────

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

    // ── Persistence helpers ──────────────────────────────────────────────────

    private async Task PersistJournalEntry(Guid saveSlotId, string text, CancellationToken ct)
    {
        try
        {
            var entry = new JournalEntry
            {
                Id = Guid.NewGuid(), SaveSlotId = saveSlotId,
                EntryText = text, Timestamp = DateTime.UtcNow,
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
