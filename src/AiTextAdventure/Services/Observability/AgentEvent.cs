namespace AiTextAdventure.Services.Observability;

public record AgentEvent(
    string Title,
    string AgentName,
    AgentEventKind Kind,
    string? Detail = null
)
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public enum AgentEventKind
{
    AgentInvoked, AgentCompleted, AgentInput, AgentOutput,
    ToolCall, ToolResult, Handoff,
    Streaming, WorkflowComplete, Error,
    SuperStepStarted, SuperStepCompleted,
    TokenUsage
}
