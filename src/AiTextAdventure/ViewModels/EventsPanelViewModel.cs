using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiTextAdventure.Services.Observability;

namespace AiTextAdventure.ViewModels;

public partial class AgentEventViewModel(AgentEvent evt) : ObservableObject
{
    public string TimeStamp => evt.Timestamp.ToString("HH:mm:ss");
    public string AgentName => evt.AgentName;
    public string Title => evt.Title;
    public string? Detail => evt.Detail;
    public string? FullContent => evt.FullContent;

    public bool HasFullContent => !string.IsNullOrWhiteSpace(evt.FullContent);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandIcon))]
    private bool isExpanded = false;

    public string ExpandIcon => IsExpanded ? "▼" : "▶";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public string Icon => evt.Kind switch
    {
        AgentEventKind.AgentInvoked => "🚀",
        AgentEventKind.AgentCompleted => "✅",
        AgentEventKind.AgentInput => "📥",
        AgentEventKind.AgentOutput => "📤",
        AgentEventKind.Prompt => "📋",
        AgentEventKind.Response => "💬",
        AgentEventKind.ToolCall => "🔧",
        AgentEventKind.ToolResult => "📦",
        AgentEventKind.Handoff => "🤝",
        AgentEventKind.Streaming => "⏳",
        AgentEventKind.WorkflowComplete => "🏁",
        AgentEventKind.Error => "❌",
        AgentEventKind.SuperStepStarted => "⏩",
        AgentEventKind.SuperStepCompleted => "⏩",
        AgentEventKind.TokenUsage => "📊",
        _ => "•"
    };

    // Named AgentColor to avoid collision with the Color type in compiled XAML bindings
    public Color AgentColor => evt.Kind switch
    {
        AgentEventKind.Error => Colors.Red,
        AgentEventKind.WorkflowComplete => Colors.LightGreen,
        AgentEventKind.Handoff => Colors.Orange,
        AgentEventKind.ToolCall or AgentEventKind.ToolResult => Colors.Violet,
        AgentEventKind.Prompt => Color.FromArgb("#5B9BD5"),
        AgentEventKind.Response => Color.FromArgb("#70AD47"),
        AgentEventKind.Streaming => Colors.CornflowerBlue,
        AgentEventKind.AgentInvoked => Colors.Gold,
        _ => Color.FromArgb("#7A7A9A")
    };
}

public partial class EventsPanelViewModel : ObservableObject
{
    private readonly EventStream _eventStream;

    [ObservableProperty]
    private bool isVisible = true;

    public ObservableCollection<AgentEventViewModel> Events { get; } = [];

    public EventsPanelViewModel(EventStream eventStream)
    {
        _eventStream = eventStream;
        _eventStream.EventEmitted += OnEventEmitted;
    }

    private void OnEventEmitted(AgentEvent evt)
    {
        // EventEmitted fires on the emitting thread (may be background).
        // Marshal the collection update to the main thread.
        MainThread.BeginInvokeOnMainThread(() =>
            Events.Add(new AgentEventViewModel(evt)));
    }

    [RelayCommand]
    private void ToggleVisibility() => IsVisible = !IsVisible;

    [RelayCommand]
    private void ClearEvents() =>
        MainThread.BeginInvokeOnMainThread(() => Events.Clear());

    public void Unsubscribe() => _eventStream.EventEmitted -= OnEventEmitted;
}
