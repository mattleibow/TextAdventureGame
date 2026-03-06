using AiTextAdventure.ViewModels;

namespace AiTextAdventure.Views;

[QueryProperty(nameof(SaveSlotIdStr), "saveSlotId")]
public partial class GamePage : ContentPage
{
    private readonly GameViewModel _gameViewModel;
    private readonly EventsPanelViewModel _eventsViewModel;

    public string SaveSlotIdStr
    {
        set
        {
            if (Guid.TryParse(value, out var id))
                _gameViewModel.SaveSlotId = id;
        }
    }

    public GamePage(GameViewModel gameViewModel, EventsPanelViewModel eventsViewModel)
    {
        InitializeComponent();
        _gameViewModel = gameViewModel;
        _eventsViewModel = eventsViewModel;

        // Left panel: game narrative, input, suggested actions
        BindingContext = gameViewModel;

        // Right panel: agent events (separate BindingContext on the named Grid)
        EventsPanel.BindingContext = eventsViewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Kick off the opening narrative once the SaveSlotId query property has been set
        await _gameViewModel.InitializeAsync();
    }
}
