namespace AiTextAdventure.Services.Observability;

/// <summary>
/// Simple event bus for agent lifecycle events.
/// Uses a plain C# event so subscribers receive events synchronously on the emitting thread.
/// EventsPanelViewModel dispatches to the main thread inside its handler.
/// </summary>
public class EventStream
{
    public event Action<AgentEvent>? EventEmitted;

    public void Emit(AgentEvent evt) => EventEmitted?.Invoke(evt);
}
