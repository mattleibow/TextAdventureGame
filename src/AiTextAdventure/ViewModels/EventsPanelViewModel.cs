using System.Collections.ObjectModel;
using System.ComponentModel;
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

    // ── Turn grouping ────────────────────────────────────────────────────────
    public bool IsTurnHeader => evt.Kind == AgentEventKind.TurnStart;
    public bool IsRegularEvent => evt.Kind != AgentEventKind.TurnStart;
    public string TurnLabel => $"⏱ {evt.Title}  —  {(evt.Detail is { Length: > 0 } d ? $"\"{d}\"" : "")}";

    // Turn collapse/expand:
    // - Turn header VMs own IsExpanded and toggle on tap
    // - Regular event VMs hold a reference to their parent turn header and derive IsVisible from it
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandIcon), nameof(IsExpandedContent))]
    private bool isExpanded = true;

    private AgentEventViewModel? _parentTurn;
    public AgentEventViewModel? ParentTurn
    {
        get => _parentTurn;
        set
        {
            if (_parentTurn is not null) _parentTurn.PropertyChanged -= OnParentPropertyChanged;
            _parentTurn = value;
            if (_parentTurn is not null) _parentTurn.PropertyChanged += OnParentPropertyChanged;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    private void OnParentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsExpanded)) OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>
    /// True when this item should be visible in the list.
    /// Turn headers are always visible. Regular events are visible when their parent turn is expanded.
    /// </summary>
    public bool IsVisible => IsTurnHeader || (_parentTurn?.IsExpanded ?? true);

    /// <summary>True when this is a regular event AND the user has tapped to expand its full content.</summary>
    public bool IsExpandedContent => IsExpanded && IsRegularEvent;

    public string ExpandIcon => IsExpanded ? "▼" : "▶";

    // Turn header: tap toggles collapse/expand of the whole turn
    // Regular event: tap toggles the full-content panel
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public string Icon => evt.Kind switch
    {
        AgentEventKind.TurnStart => "⏱",
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
    private AgentEventViewModel? _currentTurnHeader;

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
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var vm = new AgentEventViewModel(evt);
            if (evt.Kind == AgentEventKind.TurnStart)
            {
                // Collapse the previous turn so the list stays readable
                if (_currentTurnHeader is not null)
                    _currentTurnHeader.IsExpanded = false;
                _currentTurnHeader = vm;
                // New turns start expanded
                vm.IsExpanded = true;
            }
            else
            {
                vm.ParentTurn = _currentTurnHeader;
            }
            Events.Add(vm);
        });
    }

    [RelayCommand]
    private void ToggleVisibility() => IsVisible = !IsVisible;

    [RelayCommand]
    private void ClearEvents() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Events.Clear();
            _currentTurnHeader = null;
        });

    public void Unsubscribe() => _eventStream.EventEmitted -= OnEventEmitted;
}
