using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services;

namespace AiTextAdventure.ViewModels;

/// <summary>
/// ViewModel for the right-side tabbed sidebar.
/// Tabs: Status | Pockets | Journal | Events
/// Call RefreshAsync after each game turn to update Status, Pockets, and Journal.
/// </summary>
public partial class SidebarViewModel(
    WorldStateService worldStateService,
    EventsPanelViewModel eventsPanel) : ObservableObject
{
    public EventsPanelViewModel EventsPanel { get; } = eventsPanel;

    // ── Tab selection ────────────────────────────────────────
    [ObservableProperty]
    private int selectedTab = 0; // 0=Status 1=Pockets 2=Journal 3=Events

    public bool IsStatusTab => SelectedTab == 0;
    public bool IsPocketsTab => SelectedTab == 1;
    public bool IsJournalTab => SelectedTab == 2;
    public bool IsEventsTab => SelectedTab == 3;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsStatusTab));
        OnPropertyChanged(nameof(IsPocketsTab));
        OnPropertyChanged(nameof(IsJournalTab));
        OnPropertyChanged(nameof(IsEventsTab));
    }

    [RelayCommand] private void SelectStatus() => SelectedTab = 0;
    [RelayCommand] private void SelectPockets() => SelectedTab = 1;
    [RelayCommand] private void SelectJournal() => SelectedTab = 2;
    [RelayCommand] private void SelectEvents() => SelectedTab = 3;

    // ── Status tab ──────────────────────────────────────────
    [ObservableProperty] private string currentLocation = "—";
    [ObservableProperty] private string currentBiome = "—";
    [ObservableProperty] private string timeOfDay = "—";
    [ObservableProperty] private string regionDescription = "";
    public ObservableCollection<string> NearbyEntities { get; } = [];

    // ── Pockets (inventory) tab ─────────────────────────────
    public ObservableCollection<InventoryItem> InventoryItems { get; } = [];

    // ── Journal tab ──────────────────────────────────────────
    public ObservableCollection<JournalEntry> JournalEntries { get; } = [];

    // ── Refresh ──────────────────────────────────────────────
    public async Task RefreshAsync(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        try
        {
            var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            if (worldState is not null)
            {
                // Property change notifications must fire on the main thread
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    CurrentLocation = worldState.CurrentLocation;
                    CurrentBiome = worldState.CurrentBiome;
                    TimeOfDay = worldState.TimeOfDay;
                    RegionDescription = worldState.RegionDescription;

                    NearbyEntities.Clear();
                    foreach (var e in worldState.KnownEntities ?? [])
                        NearbyEntities.Add(e);
                });
            }

            var items = await worldStateService.GetInventory(saveSlotId, cancellationToken);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                InventoryItems.Clear();
                foreach (var item in items)
                    InventoryItems.Add(item);
            });

            var journal = await worldStateService.GetJournalEntries(saveSlotId, cancellationToken);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                JournalEntries.Clear();
                foreach (var entry in journal.Take(10))
                    JournalEntries.Add(entry);
            });
        }
        catch
        {
            // Non-fatal: sidebar refresh failures don't affect gameplay
        }
    }
}
