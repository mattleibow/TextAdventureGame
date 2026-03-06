using AiTextAdventure.ViewModels;

namespace AiTextAdventure.Views;

public partial class JournalPage : ContentPage
{
    private readonly JournalViewModel _viewModel;

    public JournalPage(JournalViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadEntriesAsync();
    }
}
