using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Models;
using AiTextAdventure.Services;
using AiTextAdventure.Services.Observability;
using AiTextAdventure.ViewModels;
using AiTextAdventure.Views;
#if DEBUG
using MauiDevFlow.Agent;
#endif

namespace AiTextAdventure;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.AddMauiDevFlowAgent();
#endif

        var services = builder.Services;

        // -- Logging --
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
#if DEBUG
            logging.AddDebug();
#endif
        });

        // -- IChatClient (Apple Intelligence SLM) --
        // Only supported on Apple platforms (iOS 18.4+ / macOS 15.4+ with Apple Intelligence enabled).
        // Throws PlatformNotSupportedException on Android/Windows -- the game requires Apple Intelligence.
        services.AddSingleton<IChatClient>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
#if IOS || MACCATALYST
            IChatClient raw = new Microsoft.Maui.Essentials.AI.AppleIntelligenceChatClient();
            return raw.AsBuilder()
                .UseLogging(loggerFactory)
                .UseFunctionInvocation()
                .Build();
#else
            throw new PlatformNotSupportedException(
                "AI Text Adventure requires Apple Intelligence and is only supported on iOS and Mac Catalyst.");
#endif
        });

        // -- Persistence (Shiny.SqliteDocumentDb) --
        // See: https://shinylib.net/data/sqlite-docdb/
        services.AddSqliteDocumentStore(opts =>
        {
            var dbPath = Path.Combine(FileSystem.AppDataDirectory, "adventure.db");
            opts.ConnectionString = $"Data Source={dbPath}";
            opts.JsonSerializerOptions = GameJsonContext.Default.Options;
        });

        // -- Observability --
        services.AddSingleton<EventStream>();

        // -- Services --
        services.AddSingleton<WorldStateService>();
        services.AddSingleton<SaveSlotService>();
        services.AddSingleton<MapService>();
        services.AddSingleton<GameMaster>();

        // -- ViewModels --
        services.AddTransient<MainMenuViewModel>();
        services.AddTransient<GameViewModel>();
        services.AddTransient<EventsPanelViewModel>();
        services.AddTransient<SidebarViewModel>();
        services.AddTransient<InventoryViewModel>();
        services.AddTransient<JournalViewModel>();

        // -- Pages --
        services.AddTransient<MainMenuPage>();
        services.AddTransient<GamePage>();
        services.AddTransient<InventoryPage>();
        services.AddTransient<JournalPage>();

        var app = builder.Build();

        // Create database indexes at startup
        Task.Run(async () =>
        {
            try
            {
                var store = (SqliteDocumentStore)app.Services.GetRequiredService<IDocumentStore>();
                await DatabaseInitializer.CreateIndexes(store);
            }
            catch
            {
                // Non-fatal: indexes improve performance but queries still work without them
            }
        });

        return app;
    }
}
