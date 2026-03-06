using System.Reactive.Subjects;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.Services.Observability;

public class EventStream : IDisposable
{
    private readonly Subject<AgentEvent> _subject = new();

    public IObservable<AgentEvent> Events => _subject;

    public void Emit(AgentEvent evt) => _subject.OnNext(evt);

    public void Dispose() => _subject.Dispose();
}
