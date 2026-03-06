using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services;

namespace AiTextAdventure.ViewModels;

public partial class InventoryViewModel(IWorldStateService worldStateService) : ObservableObject
{
    [ObservableProperty]
    private Guid saveSlotId;

    [ObservableProperty]
    private bool isLoading;

    public ObservableCollection<InventoryItem> Items { get; } = [];

    public async Task LoadInventoryAsync()
    {
        IsLoading = true;
        try
        {
            var items = await worldStateService.GetInventory(SaveSlotId);
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
