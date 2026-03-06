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
        - No lists, no headings, just prose.
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
        {"CurrentBiome":"forest","CurrentLocation":"Name","TimeOfDay":"dawn","RegionDescription":"2 sentences.","KnownEntities":["item1","item2","item3"],"RecentEvents":["One sentence."]}
        Generate a starting world for the game name given. Make it match the theme.
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
            worldState = await GenerateWorldStateAsync(saveSlotId, gameName, cancellationToken);
            await worldStateService.SaveState(worldState, cancellationToken);
            logger.LogInformation("Generated world: {Biome} / {Location}", worldState.CurrentBiome, worldState.CurrentLocation);
        }

        return await ProcessTurnAsync(saveSlotId, "Describe the opening scene.", cancellationToken);
    }

    public async Task<GameTurnResult> ProcessTurnAsync(
        Guid saveSlotId,
        string playerInput,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Turn: {Input}", playerInput[..Math.Min(60, playerInput.Length)]);

        var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
        var worldContext = BuildCompactContext(worldState);

        try
        {
            // Step 1: Narrator — concise, grounded narrative
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
                narrativeText[..Math.Min(100, narrativeText.Length)]));

            // Step 2: Suggestions — isolated short prompt
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

            // Keep RecentEvents trimmed to last 3 to limit future context sizes
            if (worldState is not null)
            {
                worldState.RecentEvents ??= [];
                worldState.RecentEvents.Add($"{playerInput[..Math.Min(40, playerInput.Length)]}: {narrativeText[..Math.Min(60, narrativeText.Length)]}");
                if (worldState.RecentEvents.Count > 3)
                    worldState.RecentEvents = worldState.RecentEvents[^3..];
                await worldStateService.SaveState(worldState, cancellationToken);
            }

            await PersistJournalEntry(saveSlotId, narrativeText, cancellationToken);
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
                eventStream.Emit(new AgentEvent($"World: {generated.CurrentLocation}", "WorldGen", AgentEventKind.AgentOutput));
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
            KnownEntities = ["Ancient Oak", "Moss-covered Stone", "Distant Light", "Narrow Path"],
            RecentEvents = [$"You have arrived in \"{gameName}\"."]
        };
    }

    /// <summary>
    /// Compact context kept under ~300 chars to avoid exceeding Apple Intelligence's context window.
    /// Only includes the single most recent event to limit token usage.
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
            var slots = await store.Query<SaveSlot>(s => s.Id == saveSlotId, GameJsonContext.Default.SaveSlot, ct);
            var slot = slots.FirstOrDefault();
            if (slot is not null)
            {
                slot.LastPlayedAt = DateTime.UtcNow;
                await store.Set(slot.Id.ToString(), slot, GameJsonContext.Default.SaveSlot, ct);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to update last played"); }
    }
}
