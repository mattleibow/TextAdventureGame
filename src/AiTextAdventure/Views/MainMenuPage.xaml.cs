using AiTextAdventure.Models.Documents;
using AiTextAdventure.ViewModels;

namespace AiTextAdventure.Views;

public partial class MainMenuPage : ContentPage
{
    private readonly MainMenuViewModel _viewModel;

    public MainMenuPage(MainMenuViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadSaveSlotsAsync();
    }

    private async void OnPlaySlotClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: SaveSlot slot })
        {
            await Shell.Current.GoToAsync($"game?saveSlotId={slot.Id}");
        }
    }
}
