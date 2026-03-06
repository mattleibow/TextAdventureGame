using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextAdventure.Services;

/// <summary>
/// A deterministic fallback IChatClient used when Apple Intelligence is not available
/// (non-Apple platforms, older OS versions, or simulator/emulator environments).
/// Generates plausible narrative text and suggested actions from the player input
/// without requiring an on-device LLM, so the game is always playable during development.
/// </summary>
public class FallbackChatClient(ILogger<FallbackChatClient> logger) : IChatClient
{
    private static readonly string[] NarrativeTemplates =
    [
        "You venture forward carefully, senses alert. The air here is thick with possibility — every shadow conceals a secret, every sound a story waiting to unfold.\n\nBefore you, the landscape stretches with quiet menace. Ancient stones mark the passage of those who came before. You are not the first to walk this path, and you may not be the last.",
        "The world shifts as you act, responding to your presence. A faint breeze carries the scent of pine and distant rain. Something moves in the undergrowth — perhaps a creature, perhaps just the wind playing tricks.\n\nYou stand at the edge of what is known, looking into what is not. The path forward is yours to choose.",
        "Your senses sharpen as you survey the scene. The silence here is deep — not the silence of emptiness, but the silence of things waiting. Whatever haunts this place has learned patience.\n\nSmall details catch your eye: marks on a stone, a feather caught on a branch, the faint impression of footprints in soft earth.",
        "Time seems to slow as you take in your surroundings. The colours here are muted, as if the world itself holds its breath.\n\nIn the distance, something glints — metal? Water? You cannot be certain. The path branches, and each direction carries its own whisper of promise or peril.",
        "The shadows flicker as you move, ancient forces taking notice of your presence. This place has seen adventurers before — you can feel it in the weight of the air.\n\nA faint hum resonates beneath your feet, barely perceptible. It could be nothing. It could be everything.",
        "You pause, absorbing every detail of your environment. The world is alive here in ways that the mundane places never are — leaves rustle without wind, stones seem to breathe.\n\nSomewhere, a bird calls once and falls silent. The echo lingers longer than it should, as if the air here is reluctant to let sounds die.",
        "Movement in your peripheral vision — but when you look, nothing. Only the landscape, unchanged, indifferent.\n\nYet the feeling persists: you are being watched. Not with malice, not with welcome. Simply... observed. Whatever sees you is ancient and patient and utterly beyond surprise.",
        "You act, and the world responds. A door opens where there was only stone. A path appears where there was only undergrowth. The universe, it seems, rewards those who dare.\n\nAhead lies the unknown. Behind lies the already-lived. You face forward.",
    ];

    private static readonly string[] WorldStateTemplates =
    [
        """{"CurrentBiome":"forest","CurrentLocation":"Whispering Glade","TimeOfDay":"dawn","RegionDescription":"A misty forest clearing where ancient oaks stand sentinel over moss-covered stones. Pale light filters through the canopy, casting long shadows across the damp earth.","KnownEntities":["Ancient Oak","Moss-covered Stone","Hooded Stranger","Fireflies"],"RecentEvents":["You awoke here at dawn, drawn by a strange calling you cannot explain."]}""",
        """{"CurrentBiome":"ruins","CurrentLocation":"Shattered Citadel","TimeOfDay":"dusk","RegionDescription":"The crumbling remains of a once-great fortress loom against a bruised sky. Wind moans through broken battlements, carrying the smell of old stone and forgotten history.","KnownEntities":["Ruined Gate","Overgrown Courtyard","Flickering Torch","Iron Door"],"RecentEvents":["You followed a map here that led to the edge of civilisation."]}""",
        """{"CurrentBiome":"cave","CurrentLocation":"Echoing Depths","TimeOfDay":"midnight","RegionDescription":"Vast caverns stretch beyond the reach of your light. The drip of water echoes endlessly. Bioluminescent fungi cast an eerie blue glow across the wet stone walls.","KnownEntities":["Crystal Formation","Underground Stream","Ancient Carving","Dark Passage"],"RecentEvents":["You descended into this place seeking something valuable, or someone."]}""",
        """{"CurrentBiome":"mountain","CurrentLocation":"Storm's Edge Peak","TimeOfDay":"morning","RegionDescription":"A treacherous mountain pass battered by freezing winds. Below you, clouds obscure the valley. Above, the summit is hidden in swirling snow.","KnownEntities":["Stone Cairn","Ice Bridge","Sheltered Cave","Distant Eagle"],"RecentEvents":["You climbed here following rumours of a hidden temple at the summit."]}""",
    ];

    private int _callCount;

    public ChatClientMetadata Metadata =>
        new("FallbackChatClient", new Uri("https://localhost"), "fallback");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var systemPrompt = messageList.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        var userMessage = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var index = Interlocked.Increment(ref _callCount);

        var text = GenerateResponse(systemPrompt, userMessage, index);
        logger.LogDebug("FallbackChatClient response #{Index}: {Length} chars (system prompt type: {Type})",
            index, text.Length, ClassifySystemPrompt(systemPrompt));

        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, text)])
        {
            ModelId = "fallback"
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Text);
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? key = null) => null;

    private string GenerateResponse(string systemPrompt, string userMessage, int callIndex)
    {
        var promptType = ClassifySystemPrompt(systemPrompt);

        return promptType switch
        {
            "worldgen" => GenerateWorldState(userMessage, callIndex),
            "suggestion" => GenerateSuggestions(userMessage, callIndex),
            "resolver" => GenerateActionResult(userMessage, callIndex),
            _ => GenerateNarrative(userMessage, callIndex)  // narrator or unknown
        };
    }

    private static string ClassifySystemPrompt(string systemPrompt)
    {
        var lower = systemPrompt.ToLowerInvariant();
        if (lower.Contains("worldgen") || lower.Contains("world state") || lower.Contains("currentbiome"))
            return "worldgen";
        if (lower.Contains("suggestion") || lower.Contains("action suggestions") || lower.Contains("json array"))
            return "suggestion";
        if (lower.Contains("itemspickedup") || lower.Contains("entitiespickedup") || lower.Contains("locationchanged") || lower.Contains("entitiesremoved"))
            return "resolver";
        return "narrator";
    }

    private string GenerateWorldState(string gameName, int callIndex)
    {
        // Pick a template based on the game name hash for consistency
        var index = Math.Abs(gameName.GetHashCode()) % WorldStateTemplates.Length;
        return WorldStateTemplates[index];
    }

    private string GenerateNarrative(string userMessage, int callIndex)
    {
        var index = Math.Abs(userMessage.GetHashCode()) % NarrativeTemplates.Length;
        return NarrativeTemplates[index];
    }

    private static string GenerateSuggestions(string context, int callIndex)
    {
        // Vary suggestions slightly based on call index
        var setIndex = callIndex % 4;
        var suggestions = setIndex switch
        {
            0 => new[]
            {
                new { Label = "Explore", ActionText = "explore the area carefully" },
                new { Label = "Look around", ActionText = "look around for anything interesting" },
                new { Label = "Listen", ActionText = "stand still and listen intently" },
                new { Label = "Search", ActionText = "search for hidden passages or items" },
            },
            1 => new[]
            {
                new { Label = "Move forward", ActionText = "move cautiously forward" },
                new { Label = "Examine", ActionText = "examine the nearest object closely" },
                new { Label = "Rest", ActionText = "rest and recover your senses" },
                new { Label = "Call out", ActionText = "call out to see if anyone is near" },
            },
            2 => new[]
            {
                new { Label = "Investigate", ActionText = "investigate the strange markings" },
                new { Label = "Take cover", ActionText = "take cover behind a nearby object" },
                new { Label = "Pick up", ActionText = "pick up the nearest interesting item" },
                new { Label = "Retreat", ActionText = "retreat and reconsider your approach" },
            },
            _ => new[]
            {
                new { Label = "Push on", ActionText = "push on despite the uncertainty" },
                new { Label = "Wait", ActionText = "wait and see what happens next" },
                new { Label = "Use item", ActionText = "use an item from your pack" },
                new { Label = "Make camp", ActionText = "find a safe spot to make camp" },
            },
        };

        return JsonSerializer.Serialize(new { Actions = suggestions });
    }

    private static string GenerateActionResult(string context, int callIndex)
    {
        // Parse the context to determine what happened
        var lower = context.ToLowerInvariant();

        // Detect pickup actions
        var isPickup = lower.Contains("pick up") || lower.Contains("grab") || lower.Contains("take");
        var isDrop = lower.Contains("drop") || lower.Contains("put down");

        // Extract the entity from "Entities: X, Y, Z. Action: pick up X."
        string? pickedUpItem = null;
        if (isPickup)
        {
            // Try to match entity names from the entities list
            var entitiesMatch = System.Text.RegularExpressions.Regex.Match(context, @"Entities:\s*([^.]+)\.");
            if (entitiesMatch.Success)
            {
                var entitiesStr = entitiesMatch.Groups[1].Value;
                var entities = entitiesStr.Split(',').Select(e => e.Trim()).ToList();
                foreach (var entity in entities)
                {
                    if (!string.IsNullOrEmpty(entity) && lower.Contains(entity.ToLowerInvariant()))
                    {
                        pickedUpItem = entity;
                        break;
                    }
                }
                // Fallback: just take first entity if player said "pick up" generically
                if (pickedUpItem is null && entities.Count > 0)
                    pickedUpItem = entities[0];
            }
        }

        if (pickedUpItem is not null)
        {
            return JsonSerializer.Serialize(new
            {
                ItemsPickedUp = new[] { new { ItemName = pickedUpItem, Description = $"A {pickedUpItem} found in the area." } },
                ItemsDropped = Array.Empty<string>(),
                LocationChanged = (string?)null,
                EntitiesRemoved = new[] { pickedUpItem },
                NewEntities = Array.Empty<string>()
            });
        }

        // No state change
        return """{"ItemsPickedUp":[],"ItemsDropped":[],"LocationChanged":null,"EntitiesRemoved":[],"NewEntities":[]}""";
    }
}
