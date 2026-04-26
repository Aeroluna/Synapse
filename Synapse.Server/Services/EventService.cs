using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapse.Networking.Models;
using Synapse.Server.Clients;
using Synapse.Server.Extras;
using Synapse.Server.Stages;

namespace Synapse.Server.Services;

public interface IEventService
{
    public Stage CurrentStage { get; }

    public int StageIndex { get; }

    public Stage[] Stages { get; }

    public string Motd { get; set; }

    public void PrintStage(IClient client);

    public void PrintStatus(IClient client);

    public Task SendStatus(IClient client);

    public Task SetStage(int index, IClient client);

    public void UpdateStatus(bool resetMotd);
}

public class EventService : IEventService
{
    private readonly IListenerService _listenerService;
    private readonly ILogger<EventService> _log;

    private Status? _cachedStatus;

    private CancellationTokenSource _cancellationTokenSource = new();

    private string? _motdOverride;

    public EventService(
        ILogger<EventService> log,
        IListenerService listenerService,
        ILeaderboardService leaderboardService,
        IEnumerable<Stage> stages,
        IClient serverClient)
    {
        _log = log;
        _listenerService = listenerService;

        listenerService.ClientConnected += OnClientConnected;
        leaderboardService.ScoreSubmitted += OnScoreSubmitted;
        listenerService.StatusRequested += OnStatusRequested;

        Stages = stages.ToArray();
        for (int i = 0; i < Stages.Length; i++)
        {
            Stage stage = Stages[i];
            int stageIndex = i;
            stage.Finished += () =>
            {
                if (!stage.Active)
                {
                    return;
                }

                _ = SetStage(stageIndex + 1, serverClient);
            };
            stage.StatusUpdateRequested += UpdateStatus;
        }

        _ = BeginStage(Stages[0]);
    }

    public Stage CurrentStage => Stages[StageIndex];

    public Stage[] Stages { get; }

    public string Motd
    {
        get => GetStatus().Motd;
        set
        {
            _motdOverride = value;
            UpdateStatus(false);
        }
    }

    public int StageIndex { get; private set; }

    public void PrintStage(IClient client)
    {
        client.SendPriorityServerMessage("Active stage: {Stage}", CurrentStage.GetType().Name);
    }

    public void PrintStatus(IClient client)
    {
        CurrentStage.PrintStatus(client);
    }

    public async Task SendStatus(IClient client)
    {
        Status status = GetStatus();
        string message =
            JsonSerializer.Serialize(CurrentStage.AdjustStatus(status, client), JsonService.Settings);
        await client.Send(ClientOpcode.Status, message);
    }

    public async Task SetStage(int index, IClient client)
    {
        await _cancellationTokenSource.CancelAsync();
        _cancellationTokenSource = new CancellationTokenSource();
        CancellationToken token = _cancellationTokenSource.Token;
        Stage current = CurrentStage;
        Stage nextStage = Stages[index];
        client.LogAndSend(_log, "Stage set to [{Stage}] ({Index})", nextStage, index);
        await BeginStage(nextStage);

        if (token.IsCancellationRequested)
        {
            return;
        }

        current.Active = false;
        StageIndex = index;
        UpdateStatus(true);
    }

    public void UpdateStatus(bool resetMotd)
    {
        if (resetMotd)
        {
            _motdOverride = null;
        }

        _cachedStatus = null;
        _listenerService.AllClients(SendStatus);
    }

    private static async Task BeginStage(Stage stage)
    {
        await stage.Prepare();
        stage.Active = true;
        stage.Enter();
    }

    private Status GetStatus()
    {
        if (_cachedStatus != null)
        {
            return _cachedStatus;
        }

        Status status = CurrentStage.GetStatus();
        if (_motdOverride != null)
        {
            status = status with { Motd = _motdOverride };
        }

        _cachedStatus = status;

        return _cachedStatus;
    }

    private void OnClientConnected(IClient client) => _ = SendStatus(client);

    private void OnScoreSubmitted(IClient client) => _ = SendStatus(client);

    private void OnStatusRequested(IClient client) => _ = SendStatus(client);
}
