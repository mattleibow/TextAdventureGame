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

    // For turn headers: IsExpanded = whether this turn's events are visible.
    // Toggled by tapping the header. When toggled, calls OnToggle (set by EventsPanelViewModel).
    // For regular events: IsExpanded = whether the full-content panel is shown.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandIcon), nameof(IsExpandedContent))]
    private bool isExpanded = true;

    /// <summary>
    /// Callback invoked when this item is tapped.
    /// Turn headers: set by EventsPanelViewModel to toggle + rebuild display collection.
    /// Regular events: null (Toggle handles IsExpanded directly).
    /// </summary>
    public Action? OnToggle { get; set; }

    public bool IsExpandedContent => IsExpanded && IsRegularEvent;
    public string ExpandIcon => IsExpanded ? "▼" : "▶";

    [RelayCommand]
    private void Toggle()
    {
        if (OnToggle is not null)
            OnToggle(); // turn header: delegate to panel VM
        else
            IsExpanded = !IsExpanded; // regular event: toggle full-content panel
    }

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
    private readonly List<AgentEventViewModel> _allEvents = [];
    private AgentEventViewModel? _currentTurnHeader;

    [ObservableProperty]
    private bool isVisible = true;

    /// <summary>
    /// The filtered list bound to the CollectionView.
    /// Only contains items that should be visible (turn headers + events of expanded turns).
    /// Rebuilt whenever a turn is collapsed/expanded.
    /// </summary>
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
                // Collapse the previous turn to keep the list compact
                if (_currentTurnHeader is not null)
                {
                    _currentTurnHeader.IsExpanded = false;
                    // Rebuild display so collapsed events disappear
                    RefreshDisplayEvents();
                }
                _currentTurnHeader = vm;
                vm.IsExpanded = true;
                vm.OnToggle = () =>
                {
                    vm.IsExpanded = !vm.IsExpanded;
                    RefreshDisplayEvents();
                };
            }
            _allEvents.Add(vm);
            // Only add to display if visible (turn header or expanded parent)
            if (vm.IsTurnHeader || (_currentTurnHeader?.IsExpanded ?? true))
                Events.Add(vm);
        });
    }

    private void RefreshDisplayEvents()
    {
        Events.Clear();
        foreach (var vm in _allEvents)
        {
            // Turn headers are always in the display list
            if (vm.IsTurnHeader)
            {
                Events.Add(vm);
                continue;
            }
            // Regular events: find their parent turn header
            var parentTurn = FindParentTurn(vm);
            if (parentTurn?.IsExpanded ?? true)
                Events.Add(vm);
        }
    }

    private AgentEventViewModel? FindParentTurn(AgentEventViewModel regularEvent)
    {
        AgentEventViewModel? last = null;
        foreach (var vm in _allEvents)
        {
            if (vm == regularEvent) return last;
            if (vm.IsTurnHeader) last = vm;
        }
        return last;
    }

    [RelayCommand]
    private void ToggleVisibility() => IsVisible = !IsVisible;

    [RelayCommand]
    private void ClearEvents() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _allEvents.Clear();
            Events.Clear();
            _currentTurnHeader = null;
        });

    public void Unsubscribe() => _eventStream.EventEmitted -= OnEventEmitted;

    /// <summary>Re-subscribes after Unsubscribe was called (e.g., page re-appears without re-creation).</summary>
    public void Subscribe()
    {
        _eventStream.EventEmitted -= OnEventEmitted; // prevent double-subscription
        _eventStream.EventEmitted += OnEventEmitted;
    }
}
