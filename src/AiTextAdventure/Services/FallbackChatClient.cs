using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AiTextAdventure.Services;

/// <summary>
/// A deterministic fallback IChatClient used when Apple Intelligence is not available
/// (non-Apple platforms, older OS versions, or simulator/emulator environments).
/// Generates plausible narrative text and suggested actions from the player input
/// without requiring an on-device LLM, so the game is always playable during development.
/// </summary>
public class FallbackChatClient : IChatClient
{
    private static readonly string[] Narratives =
    [
        "You venture forward carefully, senses alert. The air here is thick with possibility.",
        "The world seems to hold its breath as you act. Something shifts in the distance.",
        "Your actions ripple through the space around you. The environment responds subtly.",
        "A moment passes. The scene before you has changed in small but meaningful ways.",
        "You proceed with quiet determination. The path ahead remains uncertain but intriguing.",
        "The shadows flicker as you move. Ancient forces take notice of your presence.",
        "Time seems to slow as the consequences of your choice unfold around you.",
        "The world acknowledges what you have done. New possibilities emerge from the moment.",
    ];

    private static readonly string[] AgentNames =
        ["GameMaster", "WorldGen", "NpcDialog", "Interaction", "Guardian", "Narrator", "Suggestion"];

    private int _callCount;

    public ChatClientMetadata Metadata =>
        new("FallbackChatClient", new Uri("https://localhost"), "fallback");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _callCount);
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var text = GenerateResponse(lastUserMessage, index);
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

    private string GenerateResponse(string userMessage, int callIndex)
    {
        // The Suggestion agent expects JSON with suggested actions
        if (userMessage.Contains("suggest", StringComparison.OrdinalIgnoreCase) ||
            callIndex % 7 == 0)
        {
            var actions = new
            {
                Actions = new[]
                {
                    new { Label = "Explore", ActionText = "explore the area carefully" },
                    new { Label = "Look around", ActionText = "look around for anything interesting" },
                    new { Label = "Wait", ActionText = "wait and observe quietly" },
                }
            };
            return JsonSerializer.Serialize(actions);
        }

        // For Guardian — always pass (valid JSON verdict)
        if (userMessage.Contains("consistency", StringComparison.OrdinalIgnoreCase) ||
            callIndex % 5 == 0)
        {
            return JsonSerializer.Serialize(new { Pass = true, Violations = Array.Empty<string>(), Suggestion = (string?)null });
        }

        // Handoff instruction for GameMaster — route to WorldGen by default
        if (callIndex % 7 == 1)
            return "I'll route this to WorldGen for environment generation.";

        // Narrative response for everything else
        var narrativeIndex = Math.Abs(userMessage.GetHashCode()) % Narratives.Length;
        return Narratives[narrativeIndex];
    }
}
