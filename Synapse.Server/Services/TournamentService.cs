using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synapse.Server.Clients;
using Synapse.Server.Models;
using Synapse.Server.TournamentFormats;

namespace Synapse.Server.Services;

public interface ITournamentService
{
    public string GetColor(int division, int index, string id);

    public bool IsEliminated(int division, int index, string id);

    public Task SubmitScores(int index, IReadOnlyList<IReadOnlyList<IReadOnlyList<SavedScore>>> scores);
}

public class TournamentService : ITournamentService
{
    private readonly IBackupService _backupService;
    private readonly ITournamentFormat[] _formats;
    private readonly ILogger<TournamentService> _log;

    public TournamentService(
        ILogger<TournamentService> log,
        IListenerService listenerService,
        IListingService listingService,
        IBackupService backupService,
        IServiceProvider provider,
        IConfiguration config)
    {
        _log = log;
        _backupService = backupService;
        int divisionsCount = listingService.DivisionCount;
        IConfigurationSection eventSection = config.GetRequiredSection("Event");
        Type formatType = eventSection.GetRequiredSection("Format").Get<string>() switch
        {
            "Showcase" => typeof(ShowcaseFormat),
            "None" => typeof(NoneFormat),
            _ => throw new InvalidOperationException()
        };
        _formats = new ITournamentFormat[divisionsCount];

        bool showDivision = listingService.Listing is { Divisions.Count: > 0 };
        for (int i = 0; i < divisionsCount; i++)
        {
            string? divisionName = showDivision ? listingService.Listing!.Divisions[i].Name : null;
            ITournamentFormat format = (ITournamentFormat)ActivatorUtilities.CreateInstance(provider, formatType);
            int i1 = i;
            format.Log += Log;
            format.Broadcast += Broadcast;
            _formats[i] = format;
            continue;

#pragma warning disable CA2254
            // ReSharper disable TemplateIsNotCompileTimeConstantProblem
            void Log(LogLevel level, string message, object[] args)
            {
                if (showDivision)
                {
                    log.Log(level, "[{Division}] " + message, args.Prepend(divisionName).ToArray());
                }
                else
                {
                    log.Log(level, message, args);
                }
            }

            void Broadcast(string message, object[] args)
            {
                try
                {
                    if (showDivision)
                    {
                        foreach (IClient client in listenerService.Clients.Values.Where(n => n.Division == i1))
                        {
                            _ = client.SendPriorityServerMessage(message, args);
                        }

                        log.LogInformation("[{Division}] " + message, args.Prepend(divisionName).ToArray());
                    }
                    else
                    {
                        listenerService.BroadcastPriorityServerMessage(message, args);
                    }
                }
                catch (Exception e)
                {
                    _log.LogCritical(e, "Exception while broadcasting tournament message");
                }
            }
#pragma warning restore CA2254
        }

        backupService.BackupsLoaded += OnBackupsLoaded;
    }

    public string GetColor(int division, int index, string id)
    {
        return _formats[division].GetColor(index, id);
    }

    public bool IsEliminated(int division, int index, string id)
    {
        return _formats[division].IsEliminated(index, id);
    }

    public async Task SubmitScores(int index, IReadOnlyList<IReadOnlyList<IReadOnlyList<SavedScore>>> scores)
    {
        try
        {
            Task[] submitTasks = new Task[scores.Count];
            for (int i = 0; i < scores.Count; i++)
            {
                int i1 = i;
                submitTasks[i] = Task.Run(() => _formats[i1].SubmitScores(index, scores[i1][index]));
            }

            await Task.WhenAll(submitTasks);
        }
        catch (Exception e)
        {
            _log.LogCritical(e, "Exception while submitting scores to format");
        }

        Task[] backupTasks = new Task[scores.Count];
        for (int i = 0; i < scores.Count; i++)
        {
            backupTasks[i] = _backupService.SaveScores(
                i,
                index,
                scores[i][index],
                _formats[i].ActivePlayers[index]?.Select(n => n.Id).ToImmutableList() ?? ImmutableList<string>.Empty);
        }

        await Task.WhenAll(backupTasks);
    }

    private void OnBackupsLoaded(IReadOnlyList<IReadOnlyList<Backup>> backups)
    {
        for (int i = 0; i < backups.Count; i++)
        {
            _formats[i].OnBackupsLoaded(backups[i]);
        }
    }
}
