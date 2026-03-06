using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AiTextAdventure.Models;
using AiTextAdventure.Services;

namespace AiTextAdventure.ViewModels;

public record NarrativeParagraph(string Text)
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public partial class GameViewModel(
    GameOrchestrator orchestrator,
    ILogger<GameViewModel> logger) : ObservableObject
{
    private SidebarViewModel? _sidebar;

    // Set by GamePage after DI resolution
    public void SetSidebar(SidebarViewModel sidebar) => _sidebar = sidebar;
    [ObservableProperty]
    private string playerInput = "";

    [ObservableProperty]
    private bool isProcessing;

    [ObservableProperty]
    private Guid saveSlotId;

    [ObservableProperty]
    private string statusMessage = "Initializing...";

    private bool _initialized;

    public ObservableCollection<NarrativeParagraph> NarrativeHistory { get; } = [];
    public ObservableCollection<SuggestedAction> SuggestedActions { get; } = [];

    /// <summary>
    /// Called by GamePage.OnAppearing once the SaveSlotId has been set.
    /// Generates the opening narrative if this is the first visit.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized || SaveSlotId == Guid.Empty)
        {
            logger.LogDebug("InitializeAsync skipped: initialized={Initialized}, saveSlotId={SaveSlotId}", _initialized, SaveSlotId);
            return;
        }
        _initialized = true;
        logger.LogInformation("Initializing game view for save {SaveSlotId}", SaveSlotId);

        IsProcessing = true;
        StatusMessage = "Weaving the world...";
        try
        {
            var result = await orchestrator.InitializeGameAsync(SaveSlotId);
            logger.LogInformation("InitializeAsync complete: narrative={Length} chars, suggestions={Count}", result.Narrative.Length, result.Suggestions.Count);
            AddNarrative(result.Narrative);
            SetSuggestions(result.Suggestions);
            StatusMessage = "Ready";
            _ = _sidebar?.RefreshAsync(SaveSlotId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "InitializeAsync failed for save {SaveSlotId}", SaveSlotId);
            AddNarrative($"[Failed to load world: {ex.Message}]");
            StatusMessage = "Error";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task SubmitAction(string? actionText = null)
    {
        var input = actionText ?? PlayerInput;
        if (string.IsNullOrWhiteSpace(input))
        {
            logger.LogDebug("SubmitAction called with empty input, ignoring");
            return;
        }

        logger.LogInformation("SubmitAction: {Input}", input[..Math.Min(80, input.Length)]);
        IsProcessing = true;
        PlayerInput = "";
        StatusMessage = "Thinking...";

        try
        {
            var result = await orchestrator.ProcessTurnAsync(SaveSlotId, input);
            logger.LogInformation("SubmitAction complete: narrative={Length} chars, suggestions={Count}", result.Narrative.Length, result.Suggestions.Count);
            SetSuggestions([]);
            AddNarrative(result.Narrative);
            SetSuggestions(result.Suggestions);
            StatusMessage = "Ready";
            _ = _sidebar?.RefreshAsync(SaveSlotId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SubmitAction failed for save {SaveSlotId}", SaveSlotId);
            AddNarrative($"[Error: {ex.Message}]");
            StatusMessage = "Error occurred";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void AddNarrative(string text)
    {
        logger.LogDebug("AddNarrative: {Length} chars", text.Length);
        MainThread.BeginInvokeOnMainThread(() =>
            NarrativeHistory.Add(new NarrativeParagraph(text)));
    }

    private void SetSuggestions(IEnumerable<SuggestedAction> actions)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            SuggestedActions.Clear();
            foreach (var a in actions)
                SuggestedActions.Add(a);
        });
    }
}
