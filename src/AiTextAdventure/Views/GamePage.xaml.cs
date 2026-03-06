using AiTextAdventure.ViewModels;

namespace AiTextAdventure.Views;

[QueryProperty(nameof(SaveSlotIdStr), "saveSlotId")]
public partial class GamePage : ContentPage
{
    private readonly GameViewModel _gameViewModel;
    private readonly SidebarViewModel _sidebarViewModel;

    public string SaveSlotIdStr
    {
        set
        {
            if (Guid.TryParse(value, out var id))
                _gameViewModel.SaveSlotId = id;
        }
    }

    public GamePage(GameViewModel gameViewModel, SidebarViewModel sidebarViewModel)
    {
        InitializeComponent();
        _gameViewModel = gameViewModel;
        _sidebarViewModel = sidebarViewModel;

        // Wire sidebar to game so it refreshes after each turn
        _gameViewModel.SetSidebar(sidebarViewModel);

        // Left panel: game narrative + input
        BindingContext = gameViewModel;

        // Right panel: tabbed sidebar
        SidebarPanel.BindingContext = sidebarViewModel;
        // The EventsPanel inside the sidebar also needs its own BindingContext
        // but it's accessed via SidebarViewModel.EventsPanel, so the binding path works.
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _gameViewModel.InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _sidebarViewModel.EventsPanel.Unsubscribe();
    }
}
