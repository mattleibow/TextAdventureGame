using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services.Observability;
using AiTextAdventure.Services.Tools;
using Microsoft.Agents.AI.Workflows;

namespace AiTextAdventure.Services;

public record GameTurnResult(string Narrative, List<SuggestedAction> Suggestions);

/// <summary>Structured data passed from the Action Executor to the Narrator inside the workflow.</summary>
internal record GameActionContext(string PlayerInput, IReadOnlyList<string> ActionLog, string? Location, string? Biome);

/// <summary>
/// The AI Game Master. Drives every player turn using a 3-phase Agent Framework workflow:
/// 
/// Workflow: ActionExecutor → NarratorExecutor → SuggestionExecutor
/// 
/// Phase 1a (ActionExecutor): AI calls game tools (move, look_around, pick_up_item, etc.) to execute
///     the player's action. Does NOT write narrative — outputs only a GameActionContext summary.
/// Phase 1b (NarratorExecutor): Receives the GameActionContext and writes vivid atmospheric prose.
///     Yields the narrative string to the workflow caller.
/// Phase 2 (SuggestionExecutor): Receives the narrative and generates structured action suggestions.
///     Yields the List{SuggestedAction} to the workflow caller.
/// 
/// Results are collected from WorkflowOutputEvent instances while watching the streaming run.
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

    /// <summary>System prompt for Phase 1a (tool executor). Only calls tools — no narrative.</summary>
    private const string ExecutorSystemPrompt = """
        You are the action executor for a text adventure game. You have tools to read and change the game world.

        When the player gives you an action, follow these steps IN ORDER:
        1. Call get_world_state to understand the current situation.
        2. Call get_current_tile to get the scene description.
        3. Call EXACTLY the action tool(s) the player wants:
           - "pick up", "take", "grab", "collect" → call pick_up_item (NEVER call look_around instead)
           - "go", "walk", "move", "travel", any direction → call move_player
           - "look", "search", "explore", "examine" → call look_around
           - "drop", "put down", "discard" → call drop_item
           - "eat", "drink", "use", "consume" → call use_item
           - "equip", "wield", "wear" → call equip_item
        4. After all tools complete, respond with only: "Done."

        CRITICAL RULES:
        - Call pick_up_item when the player wants to pick something up. NEVER narrate picking up without calling pick_up_item.
        - Call move_player when the player wants to move. NEVER narrate moving without calling move_player.
        - Only pick up items that exist in the PORTABLE ITEMS list from get_world_state.
        - DO NOT call look_around when the player wants to pick something up or move.
        - DO NOT write any story, narrative, or description — only call tools, then say "Done."
        """;

    /// <summary>System prompt for Phase 1b (narrator). Pure storytelling — no tools, no game logic.</summary>
    private const string NarratorSystemPrompt = """
        You are the Narrator of a text adventure game. Write vivid, atmospheric prose.
        You will be given what just happened (tool results) and where the player is.
        Write exactly 2-4 sentences in second-person present tense ("You...").
        Focus on sights, sounds, smells, and feeling. Do not list items mechanically.
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

        // Build the per-turn ToolContext (WriteLock, ActionLog, DB access) and tool registry
        var toolCtx = new ToolContext(saveSlotId, gameName, worldStateService, mapService, store, eventStream, logger);
        var toolRegistry = new ToolRegistry(toolCtx);

        // Build the 3-phase workflow (action → narrator → suggestions)
        var workflow = BuildGameWorkflow(toolCtx, toolRegistry);

        string narrative = "The world shifts imperceptibly around you.";
        List<SuggestedAction> suggestions = DefaultSuggestions();

        try
        {
            // Start the streaming workflow run — the initial ChatMessage is the player's input
            await using var run = await InProcessExecution.RunStreamingAsync<ChatMessage>(
                workflow,
                new ChatMessage(ChatRole.User, playerInput),
                sessionId: $"{saveSlotId}:turn{turnNum}",
                cancellationToken: ct);

            // Watch for WorkflowOutputEvent — both Narrator and Suggestion executors yield outputs
            await foreach (var evt in run.WatchStreamAsync(ct))
            {
                switch (evt)
                {
                    case WorkflowOutputEvent { Data: string narrativeText } when !string.IsNullOrWhiteSpace(narrativeText):
                        narrative = narrativeText;
                        eventStream.Emit(new AgentEvent("💬 Narrative", "Narrator", AgentEventKind.Response,
                            narrativeText[..Math.Min(80, narrativeText.Length)], narrativeText));
                        break;

                    case WorkflowOutputEvent { Data: List<SuggestedAction> acts } when acts.Count > 0:
                        suggestions = acts;
                        eventStream.Emit(new AgentEvent("💬 Suggestions", "Suggestion", AgentEventKind.Response,
                            $"{acts.Count} actions", string.Join(", ", acts.Select(a => a.Label))));
                        break;

                    case ExecutorInvokedEvent invoked:
                        eventStream.Emit(new AgentEvent($"⚙️ {invoked.ExecutorId}", "Workflow", AgentEventKind.AgentInvoked));
                        break;

                    case ExecutorCompletedEvent completed:
                        eventStream.Emit(new AgentEvent($"✅ {completed.ExecutorId}", "Workflow", AgentEventKind.AgentOutput));
                        break;

                    case ExecutorFailedEvent failed:
                        logger.LogError(failed.Data, "Workflow executor {Id} failed", failed.ExecutorId);
                        eventStream.Emit(new AgentEvent($"❌ {failed.ExecutorId}", "Workflow", AgentEventKind.Error,
                            failed.Data?.Message));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameMaster workflow failed on turn {Turn}", turnNum);
            narrative = $"The world pauses. ({ex.Message})";
            eventStream.Emit(new AgentEvent("Workflow failed", "GameMaster", AgentEventKind.Error, ex.Message));
        }

        // Post-turn: stat decay, journal persistence, save slot update
        await ApplyStatDecayAsync(saveSlotId, ct);
        await PersistJournalEntry(saveSlotId, narrative, ct);
        await UpdateLastPlayed(saveSlotId, ct);
        eventStream.Emit(new AgentEvent("Turn complete", "GameMaster", AgentEventKind.WorkflowComplete));
        return new GameTurnResult(narrative, suggestions);
    }

    // ── Workflow builder ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the 3-phase sequential workflow for a single game turn:
    /// ActionExecutor → NarratorExecutor → SuggestionExecutor
    /// </summary>
    private Workflow BuildGameWorkflow(ToolContext toolCtx, ToolRegistry toolRegistry)
    {
        // ── Phase 1a: Action Executor ────────────────────────────────────────
        // Accepts the player's ChatMessage, runs tool-calling AI, returns GameActionContext
        Func<ChatMessage, IWorkflowContext, CancellationToken, ValueTask<GameActionContext>> actionFunc =
            async (msg, wfCtx, ct) =>
            {
                var playerInput = msg.Text ?? "";
                var options = new ChatOptions { Tools = toolRegistry.GetAllTools() };
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, ExecutorSystemPrompt),
                    new(ChatRole.User, playerInput)
                };

                toolCtx.EventStream.Emit(new AgentEvent("🔧 Executor", "Executor", AgentEventKind.Prompt,
                    playerInput[..Math.Min(60, playerInput.Length)],
                    $"[System]\n{ExecutorSystemPrompt}\n\n[User]\n{playerInput}"));

                await chatClient.GetResponseAsync(messages, options, ct);

                var worldState = await toolCtx.WorldStateService.GetCurrentState(toolCtx.SaveSlotId, ct);
                var context = new GameActionContext(
                    playerInput, toolCtx.ActionLog,
                    worldState?.CurrentLocation, worldState?.CurrentBiome);

                toolCtx.EventStream.Emit(new AgentEvent("✅ Actions complete", "Executor", AgentEventKind.Response,
                    $"{toolCtx.ActionLog.Count} action(s) performed",
                    string.Join("\n", toolCtx.ActionLog)));

                return context;
            };
        var actionBinding = actionFunc.BindAsExecutor("ActionExecutor", ExecutorOptions.Default, threadsafe: true);

        // ── Phase 1b: Narrator Executor ──────────────────────────────────────
        // Accepts GameActionContext, writes vivid narrative, yields it AND forwards to Suggestion
        Func<GameActionContext, IWorkflowContext, CancellationToken, ValueTask<ChatMessage>> narratorFunc =
            async (context, wfCtx, ct) =>
            {
                var narratorContext = BuildNarratorContext(context);

                toolCtx.EventStream.Emit(new AgentEvent("📖 Narrator", "Narrator", AgentEventKind.Prompt,
                    narratorContext[..Math.Min(60, narratorContext.Length)],
                    $"[System]\n{NarratorSystemPrompt}\n\n[User]\n{narratorContext}"));

                var response = await chatClient.GetResponseAsync(
                    [new(ChatRole.System, NarratorSystemPrompt), new(ChatRole.User, narratorContext)],
                    cancellationToken: ct);

                var narrative = response.Messages.LastOrDefault()?.Text?.Trim()
                    ?? "The world shifts imperceptibly around you.";

                // Yield narrative to the workflow caller (collected via WorkflowOutputEvent)
                await wfCtx.YieldOutputAsync(narrative, ct);

                // Forward narrative as ChatMessage to the Suggestion executor
                return new ChatMessage(ChatRole.Assistant, narrative);
            };
        var narratorBinding = narratorFunc.BindAsExecutor("NarratorExecutor", ExecutorOptions.Default, threadsafe: true);

        // ── Phase 2: Suggestion Executor ─────────────────────────────────────
        // Accepts the narrative ChatMessage, generates suggestions, yields them to workflow caller
        Func<ChatMessage, IWorkflowContext, CancellationToken, ValueTask> suggestionFunc =
            async (narrativeMsg, wfCtx, ct) =>
            {
                var worldState = await toolCtx.WorldStateService.GetCurrentState(toolCtx.SaveSlotId, ct);
                var suggCtx = BuildSuggestionContext(worldState, narrativeMsg.Text ?? "");

                toolCtx.EventStream.Emit(new AgentEvent("📋 Suggestion prompt", "Suggestion", AgentEventKind.Prompt,
                    suggCtx[..Math.Min(60, suggCtx.Length)],
                    $"[System]\n{SuggestionSystemPrompt}\n\n[User]\n{suggCtx}"));

                try
                {
                    var suggResponse = await chatClient.GetResponseAsync<SuggestedActions>(
                        [new(ChatRole.System, SuggestionSystemPrompt), new(ChatRole.User, suggCtx)],
                        cancellationToken: ct);

                    var acts = suggResponse.Result?.Actions is { Count: > 0 } a ? a : DefaultSuggestions();
                    // Yield List<SuggestedAction> to the workflow caller (collected via WorkflowOutputEvent)
                    await wfCtx.YieldOutputAsync(acts, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Suggestion phase failed — using defaults");
                    await wfCtx.YieldOutputAsync(DefaultSuggestions(), ct);
                }
            };
        var suggestionBinding = suggestionFunc.BindAsExecutor("SuggestionExecutor", ExecutorOptions.Default, threadsafe: true);

        // Wire the sequential workflow: Action → Narrator → Suggestion
        return new WorkflowBuilder(actionBinding)
            .AddEdge(actionBinding, narratorBinding)
            .AddEdge(narratorBinding, suggestionBinding)
            .Build(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Builds the narrator context string from action results.</summary>
    private static string BuildNarratorContext(GameActionContext context)
    {
        var parts = new List<string> { $"Player action: {context.PlayerInput}" };
        if (context.ActionLog.Count > 0)
            parts.Add($"What happened:\n{string.Join("\n", context.ActionLog)}");
        else
            parts.Add("What happened: No specific action was performed.");
        if (context.Location is not null)
            parts.Add($"Current location: {context.Location} ({context.Biome})");
        parts.Add("Write 2-4 sentences of vivid narrative:");
        return string.Join("\n", parts);
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
