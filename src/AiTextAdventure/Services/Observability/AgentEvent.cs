namespace AiTextAdventure.Services.Observability;

public record AgentEvent(
    string Title,
    string AgentName,
    AgentEventKind Kind,
    string? Detail = null,
    string? FullContent = null   // Full prompt text, response, tool args, etc. shown when expanded
)
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public enum AgentEventKind
{
    TurnStart,     // Start of a new player turn — renders as a collapsible group header
    AgentInvoked, AgentCompleted, AgentInput, AgentOutput,
    Prompt,        // System/user prompt sent to LLM
    Response,      // Full LLM response text
    ToolCall, ToolResult, Handoff,
    Streaming, WorkflowComplete, Error,
    SuperStepStarted, SuperStepCompleted,
    TokenUsage
}
