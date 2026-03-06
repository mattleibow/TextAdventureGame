using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AiTextAdventure.Tests;

/// <summary>
/// A controllable mock IChatClient for testing agent workflows.
/// Each call returns the next queued response.
/// </summary>
public class MockChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();
    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessages = [];

    public ChatClientMetadata Metadata => new("MockChatClient", new Uri("https://localhost"), "mock");

    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessages => _receivedMessages;
    public int CallCount => _receivedMessages.Count;

    /// <summary>Queue a response text to be returned on the next GetResponseAsync call.</summary>
    public MockChatClient QueueResponse(string text)
    {
        _responses.Enqueue(text);
        return this;
    }

    /// <summary>Queue a JSON-serialized object as the next response.</summary>
    public MockChatClient QueueJsonResponse<T>(T value)
    {
        _responses.Enqueue(JsonSerializer.Serialize(value));
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        _receivedMessages.Add(msgList);

        var text = _responses.Count > 0 ? _responses.Dequeue() : "Mock response";
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, text)])
        {
            ModelId = "mock"
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        var text = response.Messages.FirstOrDefault()?.Text ?? "";
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? key = null) => null;
}
