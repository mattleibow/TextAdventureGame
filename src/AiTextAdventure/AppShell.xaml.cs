using AiTextAdventure.Views;

namespace AiTextAdventure;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Register routes for navigation
        Routing.RegisterRoute("game", typeof(GamePage));
        Routing.RegisterRoute("inventory", typeof(InventoryPage));
        Routing.RegisterRoute("journal", typeof(JournalPage));
    }
}
