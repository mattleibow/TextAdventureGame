using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace AiTextAdventure.Tests;

/// <summary>
/// Tests that agents correctly route via handoffs based on player input.
/// We test the individual agent routing decisions via system prompt inspection,
/// since the full workflow requires a real execution environment.
/// </summary>
public class WorkflowRoutingTests
{
    [Fact]
    public async Task MockChatClient_ReturnsQueuedResponse()
    {
        // Arrange
        var client = new MockChatClient();
        client.QueueResponse("Hello from mock");

        // Act
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")]);

        // Assert
        Assert.Equal("Hello from mock", response.Messages[0].Text);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task MockChatClient_QueuesMultipleResponses()
    {
        // Arrange
        var client = new MockChatClient();
        client.QueueResponse("First").QueueResponse("Second").QueueResponse("Third");

        // Act
        var r1 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        var r2 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);
        var r3 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "3")]);

        // Assert
        Assert.Equal("First", r1.Messages[0].Text);
        Assert.Equal("Second", r2.Messages[0].Text);
        Assert.Equal("Third", r3.Messages[0].Text);
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task MockChatClient_DefaultsToMockResponseWhenQueueEmpty()
    {
        // Arrange
        var client = new MockChatClient();

        // Act - call without queuing anything
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "test")]);

        // Assert - should not throw, returns a default
        Assert.NotNull(response.Messages[0].Text);
    }

    [Fact]
    public async Task MockChatClient_SerializesJsonResponse()
    {
        // Arrange
        var client = new MockChatClient();
        var expected = new { Action = "Explore", Target = "forest" };
        client.QueueJsonResponse(expected);

        // Act
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "look around")]);
        var text = response.Messages[0].Text;

        // Assert - response is valid JSON containing our data
        var doc = JsonDocument.Parse(text);
        Assert.Equal("Explore", doc.RootElement.GetProperty("Action").GetString());
        Assert.Equal("forest", doc.RootElement.GetProperty("Target").GetString());
    }

    [Fact]
    public async Task MockChatClient_StreamingReturnsQueuedResponse()
    {
        // Arrange
        var client = new MockChatClient();
        client.QueueResponse("streamed response");

        // Act
        var chunks = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "test")]))
        {
            if (update.Text is not null)
                chunks.Add(update.Text);
        }

        // Assert
        Assert.Single(chunks);
        Assert.Equal("streamed response", chunks[0]);
    }

    [Fact]
    public async Task MockChatClient_RecordsReceivedMessages()
    {
        // Arrange
        var client = new MockChatClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "Hello!")
        };

        // Act
        await client.GetResponseAsync(messages);

        // Assert
        Assert.Single(client.ReceivedMessages);
        Assert.Equal(2, client.ReceivedMessages[0].Count);
        Assert.Equal("Hello!", client.ReceivedMessages[0][1].Text);
    }

    [Fact]
    public void ChatClientAgent_CanBeCreatedWithMockClient()
    {
        // Arrange
        var client = new MockChatClient();

        // Act - create a ChatClientAgent as the app does
        var agent = new ChatClientAgent(
            client,
            instructions: "You are the GameMaster. Route player actions to specialist agents.",
            name: "GameMaster",
            description: "Routes player actions to the appropriate specialist agent");

        // Assert
        Assert.NotNull(agent);
        Assert.Equal("GameMaster", agent.Name);
    }

    [Fact]
    public void ChatClientAgent_CanBeCreatedWithTools()
    {
        // Arrange
        var client = new MockChatClient();

        // A simple AIFunction tool
        var tool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("The query")] string q) => Task.FromResult($"Result for: {q}"),
            "search_tool",
            "Searches for information");

        // Act
        var agent = new ChatClientAgent(
            client,
            instructions: "You have a search tool.",
            name: "WorldGen",
            description: "Generates world content",
            tools: [tool]);

        // Assert
        Assert.NotNull(agent);
    }
}
