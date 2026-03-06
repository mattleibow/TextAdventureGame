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
        BindingContext = gameViewModel;
    }
}
