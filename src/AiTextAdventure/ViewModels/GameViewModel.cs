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
    IGameOrchestrator orchestrator) : ObservableObject
{
    [ObservableProperty]
    private string playerInput = "";

    [ObservableProperty]
    private bool isProcessing;

    [ObservableProperty]
    private Guid saveSlotId;

    [ObservableProperty]
    private string statusMessage = "Ready";

    public ObservableCollection<NarrativeParagraph> NarrativeHistory { get; } = [];
    public ObservableCollection<SuggestedAction> SuggestedActions { get; } = [];

    [RelayCommand]
    private async Task SubmitAction(string? actionText = null)
    {
        var input = actionText ?? PlayerInput;
        if (string.IsNullOrWhiteSpace(input)) return;

        IsProcessing = true;
        PlayerInput = "";
        SuggestedActions.Clear();
        StatusMessage = "Thinking...";

        try
        {
            var result = await orchestrator.ProcessTurnAsync(SaveSlotId, input);

            NarrativeHistory.Add(new NarrativeParagraph(result.Narrative));

            foreach (var suggestion in result.Suggestions)
                SuggestedActions.Add(suggestion);

            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            NarrativeHistory.Add(new NarrativeParagraph($"[Error: {ex.Message}]"));
            StatusMessage = "Error occurred";
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
