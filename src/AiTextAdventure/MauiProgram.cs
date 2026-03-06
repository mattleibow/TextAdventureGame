using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.SqliteDocumentDb;
using AiTextAdventure.Agents;
using AiTextAdventure.Models;
using AiTextAdventure.Services;
using AiTextAdventure.Services.Observability;
using AiTextAdventure.ViewModels;
using AiTextAdventure.Views;
using AiTextAdventure.Workflows;

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
        // Wrap with MEAI middleware pipeline for observability + function invocation
        // See: https://learn.microsoft.com/en-us/dotnet/ai/ichatclient
        services.AddSingleton<IChatClient>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            // Use real Apple Intelligence on-device SLM on supported Apple platforms;
            // falls back to an unsupported-platform stub on Android/Windows.
            IChatClient raw = AppleIntelligenceChatClientFactory.Create();
            return raw.AsBuilder()
                .UseLogging(loggerFactory)
                .UseFunctionInvocation()
                .Build();
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
        services.AddSingleton<IEventStream, EventStream>();

        // -- Services --
        services.AddSingleton<IWorldStateService, WorldStateService>();
        services.AddSingleton<ISaveSlotService, SaveSlotService>();
        services.AddSingleton<AgentFactory>();
        services.AddSingleton<GameWorkflowFactory>();
        services.AddSingleton<IGameOrchestrator, GameOrchestrator>();

        // -- ViewModels --
        services.AddTransient<MainMenuViewModel>();
        services.AddTransient<GameViewModel>();
        services.AddTransient<EventsPanelViewModel>();
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
