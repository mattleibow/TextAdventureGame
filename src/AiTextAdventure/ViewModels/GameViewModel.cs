using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiTextAdventure.Models;
using AiTextAdventure.Services;

namespace AiTextAdventure.ViewModels;

public record NarrativeParagraph(string Text)
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public partial class GameViewModel(
    GameOrchestrator orchestrator) : ObservableObject
{
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
        if (_initialized || SaveSlotId == Guid.Empty) return;
        _initialized = true;

        IsProcessing = true;
        StatusMessage = "Loading world...";
        try
        {
            var result = await orchestrator.InitializeGameAsync(SaveSlotId);
            AddNarrative(result.Narrative);
            SetSuggestions(result.Suggestions);
            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
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
        if (string.IsNullOrWhiteSpace(input)) return;

        IsProcessing = true;
        PlayerInput = "";
        StatusMessage = "Thinking...";

        try
        {
            var result = await orchestrator.ProcessTurnAsync(SaveSlotId, input);
            SetSuggestions([]);
            AddNarrative(result.Narrative);
            SetSuggestions(result.Suggestions);
            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
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
