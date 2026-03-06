using Microsoft.Extensions.AI;

namespace AiTextAdventure.Services;

/// <summary>
/// Stub adapter for Apple Intelligence on-device SLM.
/// Replace the stub implementations with actual Apple Intelligence SDK calls.
/// </summary>
public class AppleIntelligenceChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("AppleIntelligence", new Uri("https://localhost"), "apple-intelligence");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // TODO: Replace with actual Apple Intelligence SDK call
        throw new NotImplementedException("Replace with Apple Intelligence SDK call");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // TODO: Replace with actual Apple Intelligence SDK streaming call
        throw new NotImplementedException("Replace with Apple Intelligence SDK streaming call");
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? key = null) => null;
}
