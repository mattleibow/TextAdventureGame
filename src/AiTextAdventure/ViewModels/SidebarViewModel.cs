using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiTextAdventure.Models.Documents;
using AiTextAdventure.Services;
using AiTextAdventure.Views;

namespace AiTextAdventure.ViewModels;

/// <summary>
/// ViewModel for the right-side tabbed sidebar.
/// Tabs: Status(0) | Pockets(1) | Journal(2) | Map(3) | Events(4)
/// Call RefreshAsync after each game turn to update data.
/// </summary>
public partial class SidebarViewModel(
    WorldStateService worldStateService,
    MapService mapService,
    EventsPanelViewModel eventsPanel) : ObservableObject
{
    public EventsPanelViewModel EventsPanel { get; } = eventsPanel;

    /// <summary>Raised on the main thread after map tile data is updated so the view can invalidate.</summary>
    public event Action? MapInvalidated;

    // Direct exposures to avoid deep-path compiled binding issues
    public ObservableCollection<AgentEventViewModel> AgentEvents => EventsPanel.Events;
    public System.Windows.Input.ICommand ClearAgentEventsCommand => EventsPanel.ClearEventsCommand;

    // ── Tab selection ────────────────────────────────────────
    [ObservableProperty]
    private int selectedTab = 0; // 0=Status 1=Pockets 2=Journal 3=Map 4=Events

    public bool IsStatusTab  => SelectedTab == 0;
    public bool IsPocketsTab => SelectedTab == 1;
    public bool IsJournalTab => SelectedTab == 2;
    public bool IsMapTab     => SelectedTab == 3;
    public bool IsEventsTab  => SelectedTab == 4;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsStatusTab));
        OnPropertyChanged(nameof(IsPocketsTab));
        OnPropertyChanged(nameof(IsJournalTab));
        OnPropertyChanged(nameof(IsMapTab));
        OnPropertyChanged(nameof(IsEventsTab));
    }

    [RelayCommand] private void SelectStatus()  => SelectedTab = 0;
    [RelayCommand] private void SelectPockets() => SelectedTab = 1;
    [RelayCommand] private void SelectJournal() => SelectedTab = 2;
    [RelayCommand] private void SelectMap()     => SelectedTab = 3;
    [RelayCommand] private void SelectEvents()  => SelectedTab = 4;

    // ── Status tab — world ───────────────────────────────────
    [ObservableProperty] private string currentLocation = "—";
    [ObservableProperty] private string currentBiome = "—";
    [ObservableProperty] private string timeOfDay = "—";
    [ObservableProperty] private string regionDescription = "";
    public ObservableCollection<string> NearbyEntities  { get; } = [];  // portable items
    public ObservableCollection<string> LandmarkEntities { get; } = [];  // inspect-only features
    public ObservableCollection<string> AvailableExits  { get; } = [];

    public bool HasExits     => AvailableExits.Count > 0;
    public bool HasHidden    => HiddenCount > 0;
    public bool HasLandmarks => LandmarkEntities.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden))]
    private int hiddenCount = 0;

    // ── Status tab — player stats ────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthBar), nameof(HealthDisplay))]
    private int health = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthBar), nameof(HealthDisplay))]
    private int maxHealth = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HungerBar), nameof(HungerDisplay))]
    private int hunger = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnergyBar), nameof(EnergyDisplay))]
    private int tiredness = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GearDisplay))]
    private int armor = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GearDisplay))]
    private string? equippedWeapon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GearDisplay))]
    private string? equippedArmor;

    public string HealthBar    => BuildBar(Health, MaxHealth);
    public string HungerBar    => BuildBar(Hunger, 100);
    public string EnergyBar    => BuildBar(100 - Tiredness, 100);
    public string HealthDisplay => $"{Health}/{MaxHealth}";
    public string HungerDisplay => $"{Hunger}/100";
    public string EnergyDisplay => $"{100 - Tiredness}/100";
    public string GearDisplay
    {
        get
        {
            var parts = new List<string>();
            if (Armor > 0)                          parts.Add($"🛡️ {Armor} DEF");
            if (!string.IsNullOrEmpty(EquippedWeapon)) parts.Add($"⚔️ {EquippedWeapon}");
            if (!string.IsNullOrEmpty(EquippedArmor))  parts.Add($"🥋 {EquippedArmor}");
            return parts.Count > 0 ? string.Join("  ", parts) : "No gear equipped";
        }
    }

    private static string BuildBar(int value, int max, int width = 10)
    {
        var pct = max > 0 ? Math.Clamp((double)value / max, 0.0, 1.0) : 0.0;
        var filled = (int)(pct * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    // ── Pockets (inventory) tab ─────────────────────────────
    public ObservableCollection<InventoryItem> InventoryItems { get; } = [];

    // ── Journal tab ──────────────────────────────────────────
    public ObservableCollection<JournalEntry> JournalEntries { get; } = [];

    // ── Map tab ──────────────────────────────────────────────
    public MapDrawable MapDrawable { get; } = new();

    [ObservableProperty] private string coordinatesDisplay = "(0,0)";

    // ── Refresh ──────────────────────────────────────────────
    public async Task RefreshAsync(Guid saveSlotId, CancellationToken cancellationToken = default)
    {
        try
        {
            var worldState = await worldStateService.GetCurrentState(saveSlotId, cancellationToken);
            if (worldState is not null)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    CurrentLocation = worldState.CurrentLocation;
                    CurrentBiome    = worldState.CurrentBiome;
                    TimeOfDay       = worldState.TimeOfDay;
                    RegionDescription = worldState.RegionDescription;
                    HiddenCount = worldState.HiddenEntities?.Count ?? 0;

                    CoordinatesDisplay = $"({worldState.PlayerX},{worldState.PlayerY})";
                    MapDrawable.PlayerX = worldState.PlayerX;
                    MapDrawable.PlayerY = worldState.PlayerY;

                    NearbyEntities.Clear();
                    foreach (var e in worldState.KnownEntities ?? [])
                        NearbyEntities.Add(e);

                    LandmarkEntities.Clear();
                    foreach (var e in worldState.LandmarkEntities ?? [])
                        LandmarkEntities.Add(e);

                    AvailableExits.Clear();
                    foreach (var e in worldState.AvailableExits ?? [])
                        AvailableExits.Add(e);

                    OnPropertyChanged(nameof(HasExits));
                    OnPropertyChanged(nameof(HasHidden));
                    OnPropertyChanged(nameof(HasLandmarks));
                });
            }

            var playerStats = await worldStateService.GetPlayerStats(saveSlotId, cancellationToken);
            if (playerStats is not null)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Health          = playerStats.Health;
                    MaxHealth       = playerStats.MaxHealth;
                    Hunger          = playerStats.Hunger;
                    Tiredness       = playerStats.Tiredness;
                    Armor           = playerStats.Armor;
                    EquippedWeapon  = playerStats.EquippedWeapon;
                    EquippedArmor   = playerStats.EquippedArmor;
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

            // Refresh map tiles
            var tiles = await mapService.GetAllTiles(saveSlotId, cancellationToken);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MapDrawable.Tiles = tiles;
                OnPropertyChanged(nameof(MapDrawable));
                MapInvalidated?.Invoke();
            });
        }
        catch
        {
            // Non-fatal: sidebar refresh failures don't affect gameplay
        }
    }
}

