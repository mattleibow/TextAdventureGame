using Microsoft.Extensions.AI;

namespace AiTextAdventure.Services;

/// <summary>
/// Factory that creates the appropriate IChatClient for the current platform.
/// On Apple platforms (iOS 26+ / macOS 26+) this returns the real
/// <c>Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient</c> which calls
/// the on-device Apple Intelligence SLM via the Foundation framework.
/// On other platforms (Android, Windows) or when Apple Intelligence is unavailable,
/// it falls back to <see cref="FallbackChatClient"/> which generates deterministic
/// placeholder responses so the game remains playable during development.
/// </summary>
public static class AppleIntelligenceChatClientFactory
{
    public static IChatClient Create()
    {
#if IOS || MACCATALYST
        try
        {
            return new Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient();
        }
        catch
        {
            // Apple Intelligence not available on this device/OS version; use fallback
            return new FallbackChatClient();
        }
#else
        return new FallbackChatClient();
#endif
    }
}
