using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextAdventure.Services;

/// <summary>
/// Factory that creates the appropriate IChatClient for the current platform.
/// On Apple platforms (iOS 26+ / macOS 26+) returns the real AppleIntelligenceChatClient.
/// On other platforms returns FallbackChatClient for local development.
/// </summary>
public static class AppleIntelligenceChatClientFactory
{
    public static IChatClient Create(ILoggerFactory loggerFactory)
    {
#if IOS || MACCATALYST
        // Use the real on-device Apple Intelligence SLM directly.
        // If it's unavailable at this moment, let it throw — the caller will catch and
        // show a user-visible error message in the chat narrative.
        return new Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient();
#else
        return new FallbackChatClient(loggerFactory.CreateLogger<FallbackChatClient>());
#endif
    }
}
