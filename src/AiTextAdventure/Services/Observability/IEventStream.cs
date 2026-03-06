using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Observability;

public interface IEventStream
{
    void Emit(AgentEvent evt);
    IObservable<AgentEvent> Events { get; }
}
