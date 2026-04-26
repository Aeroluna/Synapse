using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.SystemConsole.Themes;
using Synapse.Server.Clients;
using Synapse.Server.Extras;
using Synapse.Server.Services;
using Synapse.Server.Stages;
using ListenerService = Synapse.Server.Services.ListenerService;

Log.Logger.Information("Starting...");

using IHost host = Host
    .CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IListenerService, ListenerService>();
        services.AddSingleton<ICommandService, CommandService>();
        services.AddSingleton<IEventService, EventService>();
        services.AddSingleton<ILeaderboardService, LeaderboardService>();
        services.AddSingleton<IMapService, MapService>();
        services.AddSingleton<IDirectoryService, DirectoryService>();
        services.AddSingleton<ITournamentService, TournamentService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IBlacklistService, BlacklistService>();
        services.AddSingleton<IRoleService, RoleService>();
        services.AddSingleton<IListingService, ListingService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IClient, ServerClient>();
        services.AddSingleton<ITimeService, TimeService>();

        services.AddTransient<ITimeoutService, TimeoutService>();
        services.AddTransient<IJsonService, JsonService>();

        services.AddSingleton<Stage, IntroStage>();
        services.AddSingleton<Stage, PlayStage>();
        services.AddSingleton<Stage, FinishStage>();

        services.AddHttpClient(
            "listing",
            (_, client) =>
            {
                string fullUrl = context.Configuration.GetRequiredSection("Listing").Get<string>() ??
                                 throw new InvalidOperationException();
                int id = fullUrl.LastIndexOf('/');
                client.BaseAddress = new Uri(fullUrl[..(id + 1)]);
            });

        services.AddHttpClient(
            "steam",
            (_, client) => { client.BaseAddress = new Uri("https://api.steampowered.com/"); });
    })
    .UseSerilog((context, serviceProvider, config) =>
    {
        IDirectoryService directoryService =
            serviceProvider.GetService<IDirectoryService>() ?? throw new InvalidOperationException();
        string filePath = Path.Combine(directoryService.EventDirectory, "server.log");
        string jsonFilePath = Path.Combine(directoryService.EventDirectory, "server.log.json");
        config
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Console(theme: AnsiConsoleTheme.Literate, applyThemeToRedirectedOutput: true)
            .WriteTo.File(filePath)
            .WriteTo.File(new CompactJsonFormatter(), jsonFilePath);
    })
    .Build();

IListenerService listener = host.Services.GetService<IListenerService>() ?? throw new InvalidOperationException();
_ = listener.RunAsync();

ICommandService command = host.Services.GetService<ICommandService>() ?? throw new InvalidOperationException();
command.Run(); // blocks

await listener.Stop();
