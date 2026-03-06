using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.ViewModels;

public partial class AgentEventViewModel(AgentEvent evt) : ObservableObject
{
    public string TimeStamp => evt.Timestamp.ToString("HH:mm:ss.fff");
    public string AgentName => evt.AgentName;
    public string Title => evt.Title;
    public string? Detail => evt.Detail;

    public string Icon => evt.Kind switch
    {
        AgentEventKind.AgentInvoked => "🚀",
        AgentEventKind.AgentCompleted => "✅",
        AgentEventKind.AgentInput => "📥",
        AgentEventKind.AgentOutput => "📤",
        AgentEventKind.ToolCall => "🔧",
        AgentEventKind.ToolResult => "🔧",
        AgentEventKind.Handoff => "🤝",
        AgentEventKind.Streaming => "💬",
        AgentEventKind.WorkflowComplete => "🏁",
        AgentEventKind.Error => "❌",
        AgentEventKind.SuperStepStarted => "⏩",
        AgentEventKind.SuperStepCompleted => "⏩",
        AgentEventKind.TokenUsage => "📊",
        _ => "•"
    };

    public Color Color => evt.Kind switch
    {
        AgentEventKind.Error => Colors.Red,
        AgentEventKind.WorkflowComplete => Colors.Green,
        AgentEventKind.Handoff => Colors.Orange,
        AgentEventKind.ToolCall or AgentEventKind.ToolResult => Colors.Purple,
        AgentEventKind.Streaming => Colors.CornflowerBlue,
        _ => Colors.Gray
    };
}

public partial class EventsPanelViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _subscription;

    [ObservableProperty]
    private bool isVisible = true;

    public ObservableCollection<AgentEventViewModel> Events { get; } = [];

    public EventsPanelViewModel(EventStream eventStream)
    {
        _subscription = eventStream.Events.Subscribe(evt =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
                Events.Add(new AgentEventViewModel(evt)));
        });
    }

    [RelayCommand]
    private void ToggleVisibility() => IsVisible = !IsVisible;

    [RelayCommand]
    private void ClearEvents() => Events.Clear();

    public void Dispose() => _subscription.Dispose();
}
