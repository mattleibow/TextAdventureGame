using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services;

namespace AiTextAdventure.ViewModels;

public partial class MainMenuViewModel(ISaveSlotService saveSlotService) : ObservableObject
{
    [ObservableProperty]
    private string newGameName = "";

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private SaveSlot? selectedSlot;

    public ObservableCollection<SaveSlot> SaveSlots { get; } = [];

    public async Task LoadSaveSlotsAsync()
    {
        IsLoading = true;
        try
        {
            var slots = await saveSlotService.GetSaveSlots();
            SaveSlots.Clear();
            foreach (var slot in slots.OrderByDescending(s => s.LastPlayedAt))
                SaveSlots.Add(slot);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task NewGame()
    {
        if (string.IsNullOrWhiteSpace(NewGameName)) return;

        IsLoading = true;
        try
        {
            var slot = await saveSlotService.CreateSaveSlot(NewGameName.Trim());
            SaveSlots.Insert(0, slot);
            SelectedSlot = slot;
            NewGameName = "";
            // Navigate directly into the game after creating the slot
            await Shell.Current.GoToAsync($"game?saveSlotId={slot.Id}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSlot(SaveSlot slot)
    {
        await saveSlotService.DeleteSaveSlot(slot.Id);
        SaveSlots.Remove(slot);
        if (SelectedSlot == slot)
            SelectedSlot = null;
    }
}
