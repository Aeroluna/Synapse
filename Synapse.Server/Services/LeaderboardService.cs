using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapse.Networking.Models;
using Synapse.Server.Clients;
using Synapse.Server.Extras;
using Synapse.Server.Models;

namespace Synapse.Server.Services;

public interface ILeaderboardService
{
    public event Action<IClient>? ScoreSubmitted;

    public IReadOnlyList<IReadOnlyList<IReadOnlyList<SavedScore>>> AllScores { get; }

    public void BroadcastLeaderboard(int index);

    public void DropScores(int division, int index);

    public void GenerateTestScores();

    public void RemoveScore(int division, int index, SavedScore score);

    public Task SubmitTournamentScores(int index);

    public bool TryGetScore(int index, IClient serverClient, out SavedScore score);
}

public class LeaderboardService : ILeaderboardService
{
    private static readonly LeaderboardCell _placeholderCell = new()
    {
        Rank = -1,
        PlayerName = "...",
        Percentage = -1,
        Score = -1
    };

    private static readonly Random _random = new();

    private readonly ILogger<LeaderboardService> _log;
    private readonly ITimeoutService _timeoutService;
    private readonly IMapService _mapService;
    private readonly IBackupService _backupService;
    private readonly IListenerService _listenerService;
    private readonly ITournamentService _tournamentService;

    // i feel like im committing a crime with how nested these are
    private readonly ConcurrentList<ConcurrentList<ImmutableList<LeaderboardCell>?>> _cachedAllScores;
    private readonly ConcurrentList<ConcurrentList<ImmutableList<LeaderboardCell>?>> _cachedScores;
    private readonly ConcurrentList<ConcurrentList<ConcurrentDictionary<string, SavedScore>>> _savedIds;
    private readonly ConcurrentList<ConcurrentList<ConcurrentList<SavedScore>>> _sortedAllScores;
    private readonly ConcurrentList<ConcurrentList<ConcurrentList<SavedScore>>> _sortedScores;

    public LeaderboardService(
        ILogger<LeaderboardService> log,
        ITimeoutService timeoutService,
        IMapService mapService,
        IListenerService listenerService,
        ITournamentService tournamentService,
        IBackupService backupService,
        IListingService listingService)
    {
        _log = log;
        _timeoutService = timeoutService;
        _mapService = mapService;
        _listenerService = listenerService;
        _tournamentService = tournamentService;
        _backupService = backupService;
        listenerService.ScoreSubmissionReceived += OnScoreSubmissionReceived;
        listenerService.LeaderboardRequested += OnLeaderboardRequested;

        int mapCount = mapService.MapCount;
        int divisionCount = listingService.DivisionCount;

        Func<int, ConcurrentList<ImmutableList<LeaderboardCell>?>> cacheFill = _ =>
            ConcurrentList<ImmutableList<LeaderboardCell>?>.Prefilled(mapCount, _ => null);
        _cachedScores =
            ConcurrentList<ConcurrentList<ImmutableList<LeaderboardCell>?>>.Prefilled(divisionCount, cacheFill);
        _cachedAllScores =
            ConcurrentList<ConcurrentList<ImmutableList<LeaderboardCell>?>>.Prefilled(divisionCount, cacheFill);

        _savedIds = ConcurrentList<ConcurrentList<ConcurrentDictionary<string, SavedScore>>>.Prefilled(
            divisionCount,
            _ => ConcurrentList<ConcurrentDictionary<string, SavedScore>>.Prefilled(
                mapCount,
                _ => new ConcurrentDictionary<string, SavedScore>()));

        Func<int, ConcurrentList<ConcurrentList<SavedScore>>> scoreFill = _ =>
            ConcurrentList<ConcurrentList<SavedScore>>.Prefilled(mapCount, _ => new ConcurrentList<SavedScore>());
        _sortedScores = ConcurrentList<ConcurrentList<ConcurrentList<SavedScore>>>.Prefilled(divisionCount, scoreFill);
        _sortedAllScores =
            ConcurrentList<ConcurrentList<ConcurrentList<SavedScore>>>.Prefilled(divisionCount, scoreFill);

        backupService.BackupsLoaded += OnBackupsLoaded;
    }

    public event Action<IClient>? ScoreSubmitted;

    public IReadOnlyList<IReadOnlyList<IReadOnlyList<SavedScore>>> AllScores => _sortedAllScores.AsReadOnly();

    public void BroadcastLeaderboard(int index)
    {
        _timeoutService.Timeout(
            () => _listenerService.AllClients(n => n.Send(ClientOpcode.InvalidateScores, (byte)index)),
            4000);
    }

    public void DropScores(int division, int index)
    {
        _savedIds[division][index].Clear();
        _sortedAllScores[division][index].Clear();
        _sortedScores[division][index].Clear();
        _cachedScores[division][index] = null;
        _cachedAllScores[division][index] = null;
        _backupService.SaveScores(division, index, _sortedAllScores[division][index], null);
        BroadcastLeaderboard(index);
    }

    public void GenerateTestScores()
    {
        int index = _mapService.Index;

        List<FakeClient> list = FakeClient.Fakes.ToList();
        //int count = _random.Next(list.Count / 4);
        //list.RemoveRange(_random.Next(list.Count - count), count);
        list.ForEach(n =>
        {
            int score = _random.Next(0, 9999999);
            SubmitScore(n, n.Division, index, score, score / 9999999f);
        });
    }

    public void RemoveScore(int division, int index, SavedScore score)
    {
        _savedIds[division][index].Remove(score.Id, out _);
        _sortedAllScores[division][index].Remove(score);
        _sortedScores[division][index].Remove(score);
        _cachedScores[division][index] = null;
        _cachedAllScores[division][index] = null;
        _backupService.SaveScores(division, index, _sortedAllScores[division][index], null);
        BroadcastLeaderboard(index);
    }

    public async Task SubmitTournamentScores(int index)
    {
        await _tournamentService.SubmitScores(index, _sortedScores);

        foreach (ConcurrentList<ImmutableList<LeaderboardCell>?> scoreList in _cachedScores)
        {
            scoreList[index] = null;
        }

        foreach (ConcurrentList<ImmutableList<LeaderboardCell>?> scoreList in _cachedAllScores)
        {
            scoreList[index] = null;
        }

        BroadcastLeaderboard(index);
    }

    public bool TryGetScore(int index, IClient client, out SavedScore score)
    {
        foreach (ConcurrentList<ConcurrentDictionary<string, SavedScore>> list in _savedIds)
        {
            if (list[index].TryGetValue(client.Id, out score))
            {
                return true;
            }
        }

        score = default;
        return false;
    }

    private IReadOnlyList<LeaderboardCell> GetLeaderboard(
        ConcurrentList<ConcurrentList<ConcurrentList<SavedScore>>> sortedScores,
        ConcurrentList<ConcurrentList<ImmutableList<LeaderboardCell>?>> cache,
        int division,
        int index,
        string id,
        out int leaderboardSpecialIndex)
    {
        ConcurrentList<SavedScore> mapSortedScores = sortedScores[division][index];
        int playerScoreIndex = mapSortedScores.FindIndex(n => n.Id == id);

        ConcurrentList<ImmutableList<LeaderboardCell>?> divisionCache = cache[division];
        ImmutableList<LeaderboardCell> scores = divisionCache[index] ??= Enumerable
            .Range(0, Math.Min(12, mapSortedScores.Count))
            .Select(n =>
            {
                SavedScore score = mapSortedScores[n];
                return new LeaderboardCell
                {
                    Rank = n,
                    PlayerName = score.Username,
                    Percentage = score.Percentage,
                    Score = score.Score,
                    Color = _tournamentService.GetColor(division, index, score.Id)
                };
            })
            .ToImmutableList();

        if (playerScoreIndex <= 11)
        {
            leaderboardSpecialIndex = playerScoreIndex;
            return scores;
        }

        leaderboardSpecialIndex = 11;
        List<LeaderboardCell> modifiedScores = scores.ToList();
        SavedScore playerScore = mapSortedScores[playerScoreIndex];
        modifiedScores[10] = _placeholderCell;
        modifiedScores[11] = new LeaderboardCell
        {
            Rank = playerScoreIndex,
            PlayerName = playerScore.Username,
            Percentage = playerScore.Percentage,
            Score = playerScore.Score,
            Color = _tournamentService.GetColor(division, index, playerScore.Id)
        };

        return modifiedScores;
    }

    private void OnBackupsLoaded(IReadOnlyList<IReadOnlyList<Backup>> divisionBackups)
    {
        for (int i = 0; i < divisionBackups.Count; i++)
        {
            IReadOnlyList<Backup> backups = divisionBackups[i];
            for (int j = 0; j < backups.Count; j++)
            {
                Backup backup = backups[j];
                SavedScore[] orderedScores = backup.Scores.OrderByDescending(n => n.Score).ToArray();
                _savedIds[i][j] =
                    new ConcurrentDictionary<string, SavedScore>(orderedScores.ToDictionary(n => n.Id, n => n));
                _cachedScores[i][j] = null;
                _cachedAllScores[i][j] = null;
                _sortedAllScores[i][j] = new ConcurrentList<SavedScore>(orderedScores);
                if (backup.ActivePlayers != null)
                {
                    _sortedScores[i][j] =
                        new ConcurrentList<SavedScore>(orderedScores.Where(n => backup.ActivePlayers.Contains(n.Id)));
                }
                else
                {
                    _sortedScores[i][j] = new ConcurrentList<SavedScore>(orderedScores);
                }
            }
        }
    }

    private void OnLeaderboardRequested(IClient client, int index, int division, bool showEliminated)
    {
        _ = SendLeaderboard(client, index, division, showEliminated);
    }

    private void OnScoreSubmissionReceived(IClient client, ScoreSubmission scoreSubmission)
    {
        SubmitScore(
            client,
            scoreSubmission.Division,
            scoreSubmission.Index,
            scoreSubmission.Score,
            scoreSubmission.Percentage);
    }

    private async Task SendLeaderboard(IClient client, int index, int division, bool showEliminated)
    {
        if (index > _mapService.MapCount || index < 0)
        {
            await client.SendRefusal($"Index out of range ({index})");
            return;
        }

        string id = client.Id;

        IReadOnlyList<LeaderboardCell> scores = GetLeaderboard(
            showEliminated ? _sortedAllScores : _sortedScores,
            showEliminated ? _cachedAllScores : _cachedScores,
            division,
            index,
            id,
            out int leaderboardSpecialIndex);

        LeaderboardScores leaderboardScores = new()
        {
            Index = index,
            Title = _mapService.Maps[index].Name,
            PlayerScoreIndex = leaderboardSpecialIndex,
            Scores = scores,
            AliveCount = _sortedScores[division][index].Count,
            ScoreCount = _sortedAllScores[division][index].Count,
        };

        await client.Send(
            ClientOpcode.LeaderboardScores,
            JsonSerializer.Serialize(leaderboardScores, JsonService.Settings));
    }

    private void SubmitScore(IClient client, int division, int index, int score, float percentage)
    {
        try
        {
            if (index > _mapService.Index)
            {
                _ = client.SendRefusal("Not accepting scores for map");
                return;
            }

            if (percentage > 1 || percentage < 0 || score < 0)
            {
                _ = client.SendRefusal("Invalid score");
                return;
            }

            string id = client.Id;

            if (!_savedIds.Any(list => list[index].ContainsKey(id)))
            {
                _log.LogInformation(
                    "[{Client}] scored [{Score} ({Percentage})] on [{Map}]",
                    client,
                    score,
                    percentage,
                    _mapService.Maps[index].Name);
                Submit();
                ScoreSubmitted?.Invoke(client);
            }
            else if (_mapService.Maps[index].Ruleset?.AllowResubmission ?? false)
            {
                if (_sortedAllScores.Any(k => k[index].Remove(n => n.Id == client.Id && n.Score <= score)))
                {
                    foreach (ConcurrentList<ConcurrentList<SavedScore>> k in _sortedScores)
                    {
                        if (k[index].Remove(n => n.Id == client.Id && n.Score <= score))
                        {
                            break;
                        }
                    }

                    _log.LogInformation(
                        "[{Client}] rescored [{Score} ({Percentage})] on [{Map}]",
                        client,
                        score,
                        percentage,
                        _mapService.Maps[index].Name);
                    Submit();
                }
            }
            else
            {
                _ = client.SendRefusal("Already scored");
            }

            void Submit()
            {
                SavedScore savedScore = new(score, percentage, id, client.DisplayUsername);
                _savedIds[division][index][id] = savedScore;
                _sortedAllScores[division][index].InsertIntoSortedList(savedScore);
                _cachedAllScores[division][index] = null;
                if (!_tournamentService.IsEliminated(division, index, id) &&
                    !client.HasPermission(Permission.NoQualify))
                {
                    _sortedScores[division][index].InsertIntoSortedList(savedScore);
                    _cachedScores[division][index] = null;
                }

                _timeoutService.Timeout(
                    () => _backupService.SaveScores(division, index, _sortedAllScores[division][index], null),
                    2000);

                BroadcastLeaderboard(index);
            }
        }
        catch (Exception e)
        {
            _log.LogCritical(e, "Exception while submitting score for [{Client}]", client);
        }
    }
}
