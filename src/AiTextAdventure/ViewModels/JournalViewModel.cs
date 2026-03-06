using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services;

namespace AiTextAdventure.ViewModels;

public partial class JournalViewModel(WorldStateService worldStateService) : ObservableObject
{
    [ObservableProperty]
    private Guid saveSlotId;

    [ObservableProperty]
    private bool isLoading;

    public ObservableCollection<JournalEntry> Entries { get; } = [];

    public async Task LoadEntriesAsync()
    {
        IsLoading = true;
        try
        {
            var entries = await worldStateService.GetJournalEntries(SaveSlotId);
            Entries.Clear();
            foreach (var entry in entries)
                Entries.Add(entry);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
