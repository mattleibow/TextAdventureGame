using Microsoft.Extensions.AI;

namespace AiTextAdventure.Services;

/// <summary>
/// Factory that creates the appropriate IChatClient for the current platform.
/// On Apple platforms (iOS 26+ / macOS 26+) this returns the real
/// <c>Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient</c> which calls
/// the on-device Apple Intelligence SLM via the Foundation framework.
/// On other platforms (Android, Windows) it returns a no-op stub that throws
/// <see cref="NotSupportedException"/> — those platforms don't have Apple Intelligence.
/// </summary>
public static class AppleIntelligenceChatClientFactory
{
    public static IChatClient Create()
    {
#if IOS || MACCATALYST
        return new Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient();
#else
        return new UnsupportedPlatformChatClient();
#endif
    }
}

/// <summary>
/// Fallback IChatClient for platforms that don't support Apple Intelligence.
/// </summary>
file sealed class UnsupportedPlatformChatClient : IChatClient
{
    public ChatClientMetadata Metadata =>
        new("UnsupportedPlatform", new Uri("https://localhost"), "none");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Apple Intelligence is only available on iOS 26+ and macOS 26+.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Apple Intelligence is only available on iOS 26+ and macOS 26+.");

    public void Dispose() { }

    public object? GetService(Type serviceType, object? key = null) => null;
}
