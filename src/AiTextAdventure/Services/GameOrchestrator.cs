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
/// Orchestrates a single player turn by making direct IChatClient calls with
/// carefully crafted system prompts. This approach is simpler and more reliable
/// than the multi-agent handoff workflow, which requires the LLM to understand
/// handoff protocol signals that the fallback client doesn't support.
/// </summary>
public class GameOrchestrator(
    IChatClient chatClient,
    WorldStateService worldStateService,
    SaveSlotService saveSlotService,
    IDocumentStore store,
    EventStream eventStream,
    ILogger<GameOrchestrator> logger)
{
    private const string NarratorSystemPrompt = """
        You are the Narrator of an adventure game. You write vivid, atmospheric, second-person
        prose that immerses the player in the world.

        Style guidelines:
        - Always use second person ("You see...", "You hear...", "Before you stands...")
        - Present tense
        - Vivid sensory details: what the player sees, hears, smells, feels
        - 2-4 short paragraphs maximum
        - Atmospheric and evocative, not mechanical
        - Match the tone of the world context

        You will be given the current world state and the player's action.
        Write ONLY the narrative prose. Do not include suggestions or metadata.
        """;

    private const string SuggestionSystemPrompt = """
        You are the Action Advisor for an adventure game. Based on the current situation,
        generate exactly 3-4 short, varied action suggestions for the player.

        Rules:
        - Each suggestion must be a short imperative phrase (3-8 words)
        - Mix different types: explore, discover, speak with someone, observe surroundings
        - At least one should be unexpected or creative
        - Must be relevant to the current situation
        - Keep all suggestions appropriate for a general audience

        Respond with ONLY valid JSON in this exact format:
        {"Actions":[{"Label":"Short Label","ActionText":"do the specific thing"},{"Label":"Another","ActionText":"do something else"}]}

        No markdown, no explanation, ONLY the JSON object.
        """;

    private const string WorldGenSystemPrompt = """
        You are the World Builder for an adventure game. Given a game name or theme, generate
        the starting world state as JSON.

        Respond with ONLY valid JSON in this exact format:
        {
          "CurrentBiome": "forest|cave|city|desert|ocean|mountain|dungeon|ruins",
          "CurrentLocation": "evocative location name (2-4 words)",
          "TimeOfDay": "dawn|morning|midday|afternoon|dusk|evening|night|midnight",
          "RegionDescription": "2-3 sentence description of this place and its atmosphere",
          "KnownEntities": ["entity1", "entity2", "entity3"],
          "RecentEvents": ["one sentence describing how the player arrived here"]
        }

        Make the world match the game name's theme. Be creative and evocative.
        No markdown, no explanation, ONLY the JSON object.
        """;

    public async Task<GameTurnResult> InitializeGameAsync(
        Guid saveSlotId,
        CancellationToken cancellationToken = default)
    {
        // Look up the game name from the save slot
        var slot = await saveSlotService.GetSaveSlot(saveSlotId, cancellationToken);
        var gameName = slot?.Name ?? "Adventure";
        logger.LogInformation("Initializing game for save {SaveSlotId} (name: {GameName})", saveSlotId, gameName);

        // Get or create WorldState — use AI to generate from game name if new
        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        if (worldState is null)
        {
            worldState = await GenerateWorldStateAsync(saveSlotId, gameName ?? "Adventure", cancellationToken);
            await worldStateService.SaveState(worldState, cancellationToken);
            logger.LogInformation("Generated world: {Biome} / {Location}", worldState.CurrentBiome, worldState.CurrentLocation);
        }

        // Generate opening narrative
        return await ProcessTurnAsync(saveSlotId, "You arrive. Look around and describe the opening scene vividly.", cancellationToken);
    }

    public async Task<GameTurnResult> ProcessTurnAsync(
        Guid saveSlotId,
        string playerInput,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Processing turn for save {SaveSlotId}: {Input}", saveSlotId, playerInput[..Math.Min(80, playerInput.Length)]);

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        var worldContext = BuildWorldContext(worldState);

        try
        {
            // Step 1: Generate narrative via Narrator
            logger.LogDebug("Calling Narrator agent...");
            eventStream.Emit(new AgentEvent("Generating narrative...", "Narrator", AgentEventKind.AgentInvoked));

            var narrativeMessages = new List<ChatMessage>
            {
                new(ChatRole.System, NarratorSystemPrompt),
                new(ChatRole.User, $"{worldContext}\n\nPlayer action: {playerInput}")
            };

            string narrativeText;
            try
            {
                var narrativeResponse = await chatClient.GetResponseAsync(narrativeMessages, cancellationToken: cancellationToken);
                narrativeText = narrativeResponse.Messages.LastOrDefault()?.Text?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(narrativeText))
                {
                    logger.LogWarning("Narrator returned empty response");
                    narrativeText = FallbackNarrative(playerInput);
                }
            }
            catch (Exception narrativeEx)
            {
                logger.LogError(narrativeEx, "Narrator call failed, using fallback narrative");
                narrativeText = FallbackNarrative(playerInput);
            }

            logger.LogInformation("Narrative: {Length} chars", narrativeText.Length);
            var preview = narrativeText.Length > 200 ? narrativeText[..200] + "..." : narrativeText;
            eventStream.Emit(new AgentEvent("Narrative ready", "Narrator", AgentEventKind.AgentOutput, preview));

            // Step 2: Generate action suggestions (isolated — failure here does NOT lose the narrative)
            logger.LogDebug("Calling Suggestion agent...");
            eventStream.Emit(new AgentEvent("Generating suggestions...", "Suggestion", AgentEventKind.AgentInvoked));

            List<SuggestedAction> suggestions;
            try
            {
                var suggestionMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, SuggestionSystemPrompt),
                    new(ChatRole.User, $"Location: {worldState?.CurrentLocation ?? "unknown"}\nSituation: {playerInput}\n\nGenerate 3-4 contextual action suggestions.")
                };

                var suggestionResponse = await chatClient.GetResponseAsync(suggestionMessages, cancellationToken: cancellationToken);
                var suggestionText = suggestionResponse.Messages.LastOrDefault()?.Text?.Trim() ?? "";
                suggestions = ParseSuggestions(suggestionText);
                logger.LogInformation("Suggestions: {Count}", suggestions.Count);
                var suggPreview = suggestionText[..Math.Min(100, suggestionText.Length)];
                eventStream.Emit(new AgentEvent($"{suggestions.Count} suggestions", "Suggestion", AgentEventKind.AgentOutput, suggPreview));
            }
            catch (Exception suggEx)
            {
                logger.LogWarning(suggEx, "Suggestion call failed (content filter or error), using defaults");
                eventStream.Emit(new AgentEvent("Using default suggestions", "Suggestion", AgentEventKind.AgentCompleted));
                suggestions = DefaultSuggestions();
            }

            // Step 3: Persist journal entry
            await PersistJournalEntry(saveSlotId, narrativeText, cancellationToken);
            await UpdateLastPlayed(saveSlotId, cancellationToken);

            eventStream.Emit(new AgentEvent("Turn complete", "GameMaster", AgentEventKind.WorkflowComplete));
            return new GameTurnResult(narrativeText, suggestions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing turn for save {SaveSlotId}", saveSlotId);
            eventStream.Emit(new AgentEvent($"Error: {ex.Message}", "GameMaster", AgentEventKind.Error, ex.ToString()[..Math.Min(300, ex.ToString().Length)]));
            return new GameTurnResult($"[Something went wrong: {ex.Message}]", DefaultSuggestions());
        }
    }

    private async Task<WorldState> GenerateWorldStateAsync(Guid saveSlotId, string gameName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Generating world state from game name: {GameName}", gameName);
        eventStream.Emit(new AgentEvent($"Building world for \"{gameName}\"...", "WorldGen", AgentEventKind.AgentInvoked));

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, WorldGenSystemPrompt),
                new(ChatRole.User, $"Game name: \"{gameName}\"\n\nGenerate the starting world state JSON.")
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var json = response.Messages.LastOrDefault()?.Text?.Trim() ?? "";

            // Strip markdown code fences if present
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
                logger.LogInformation("AI-generated world: {Biome} / {Location}", generated.CurrentBiome, generated.CurrentLocation);
                eventStream.Emit(new AgentEvent($"World: {generated.CurrentLocation}", "WorldGen", AgentEventKind.AgentOutput));
                return generated;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate world state from AI, using default");
            eventStream.Emit(new AgentEvent($"World gen failed, using default", "WorldGen", AgentEventKind.Error));
        }

        // Fallback default world
        return new WorldState
        {
            Id = Guid.NewGuid(),
            SaveSlotId = saveSlotId,
            CurrentBiome = "forest",
            CurrentLocation = "Whispering Glade",
            TimeOfDay = "dawn",
            RegionDescription = "A misty forest clearing where ancient oaks stand sentinel over mossy stones.",
            KnownEntities = ["Ancient Oak", "Moss-covered Stone", "Distant Light"],
            RecentEvents = [$"You have arrived in \"{gameName}\". The adventure begins."]
        };
    }

    private static string BuildWorldContext(WorldState? worldState)
    {
        if (worldState is null) return "World: unknown location, unknown time";

        return $"""
            World Context:
            - Biome: {worldState.CurrentBiome}
            - Location: {worldState.CurrentLocation}
            - Time: {worldState.TimeOfDay}
            - Description: {worldState.RegionDescription}
            - Nearby: {string.Join(", ", worldState.KnownEntities)}
            - Recent: {string.Join("; ", worldState.RecentEvents)}
            """;
    }

    private static List<SuggestedAction> ParseSuggestions(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return DefaultSuggestions();

        try
        {
            // Strip markdown fences if present
            var cleaned = json;
            if (cleaned.Contains("```"))
            {
                var start = cleaned.IndexOf('{');
                var end = cleaned.LastIndexOf('}');
                if (start >= 0 && end > start)
                    cleaned = cleaned[start..(end + 1)];
            }

            var parsed = JsonSerializer.Deserialize(cleaned, GameJsonContext.Default.SuggestedActions);
            if (parsed?.Actions is { Count: > 0 })
                return parsed.Actions;
        }
        catch (Exception)
        {
            // Fall through to defaults
        }

        return DefaultSuggestions();
    }

    private static List<SuggestedAction> DefaultSuggestions() =>
    [
        new SuggestedAction("Explore", "explore the area carefully"),
        new SuggestedAction("Look around", "look around for anything interesting"),
        new SuggestedAction("Wait", "wait and observe quietly"),
    ];

    private static string FallbackNarrative(string playerInput)
    {
        // Deterministic fallback when the LLM is unavailable or filtered
        var index = Math.Abs(playerInput.GetHashCode()) % 4;
        return index switch
        {
            0 => "You venture forward carefully, senses alert. The air here is thick with possibility — every shadow conceals a secret, every sound a story waiting to unfold.\n\nBefore you, the landscape stretches with quiet mystery. Ancient stones mark the passage of those who came before. You are not the first to walk this path, and you may not be the last.",
            1 => "Your senses sharpen as you survey the scene. The silence here is deep — not the silence of emptiness, but the silence of things waiting. Whatever inhabits this place has learned patience.\n\nSmall details catch your eye: marks on a stone, a feather caught on a branch, the faint impression of footprints in soft earth.",
            2 => "The world shifts as you act, responding to your presence. A faint breeze carries the scent of pine and distant rain. Something moves in the undergrowth — perhaps a creature, perhaps just the wind playing tricks.\n\nYou stand at the edge of what is known, looking into what is not. The path forward is yours to choose.",
            _ => "Time seems to slow as you take in your surroundings. The colours here are muted, as if the world itself holds its breath.\n\nIn the distance, something glints — metal? Water? You cannot be certain. Each direction carries its own whisper of promise.",
        };
    }

    private async Task PersistJournalEntry(Guid saveSlotId, string narrativeText, CancellationToken cancellationToken)
    {
        try
        {
            var entry = new JournalEntry
            {
                Id = Guid.NewGuid(),
                SaveSlotId = saveSlotId,
                EntryText = narrativeText,
                Timestamp = DateTime.UtcNow,
                Type = JournalEntryType.Narrative
            };
            await store.Set(entry.Id.ToString(), entry, GameJsonContext.Default.JournalEntry, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist journal entry");
        }
    }

    private async Task UpdateLastPlayed(Guid saveSlotId, CancellationToken cancellationToken)
    {
        try
        {
            var slots = await store.Query<SaveSlot>(
                s => s.Id == saveSlotId,
                GameJsonContext.Default.SaveSlot,
                cancellationToken);
            var slot = slots.FirstOrDefault();
            if (slot is not null)
            {
                slot.LastPlayedAt = DateTime.UtcNow;
                await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update last played timestamp");
        }
    }
}
