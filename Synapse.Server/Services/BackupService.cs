using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapse.Server.Extras;
using Synapse.Server.Models;

namespace Synapse.Server.Services;

public interface IBackupService
{
    public event Action<IReadOnlyList<IReadOnlyList<Backup>>>? BackupsLoaded;

    public Task LoadBackups();

    public Task SaveScores(int division, int index, IReadOnlyList<SavedScore> scores, IReadOnlyList<string>? activePlayers);
}

public class BackupService : IBackupService
{
    private readonly string _directory;
    private readonly ILogger<BackupService> _log;
    private readonly IMapService _mapService;
    private readonly IListingService _listingService;

    private readonly HashSet<string> _saving = [];

    private Backup[][]? _backups;

    public BackupService(
        ILogger<BackupService> log,
        IMapService mapService,
        IListingService listingService,
        IDirectoryService directoryService)
    {
        _log = log;
        _mapService = mapService;
        _listingService = listingService;
        _directory = Directory.CreateDirectory(Path.Combine(directoryService.EventDirectory, "backups")).FullName;
        _ = LoadBackups();
    }

    public event Action<IReadOnlyList<IReadOnlyList<Backup>>>? BackupsLoaded
    {
        add
        {
            if (_backups != null)
            {
                try
                {
                    value?.Invoke(_backups);
                }
                catch (Exception e)
                {
                    _log.LogCritical(e, "Exception while loading backup");
                }
            }

            InternalBackupsLoaded += value;
        }

        remove => InternalBackupsLoaded -= value;
    }

    private event Action<IReadOnlyList<IReadOnlyList<Backup>>>? InternalBackupsLoaded;

    public async Task LoadBackups()
    {
        try
        {

            int divisionsCount = _listingService.DivisionCount;
            int mapCount = _mapService.MapCount;
            Backup[][] backups = new Backup[divisionsCount][];
            for (int j = 0; j < divisionsCount; j++)
            {
                string[] files = Directory.EnumerateFiles(_directory, $"scores{j:D2}??.json").ToArray();
                Backup[] divisionBackups = new Backup[mapCount];
                for (int i = 0; i < mapCount; i++)
                {
                    string? file = files.FirstOrDefault(n => n.EndsWith($"scores{j:D2}{i:D2}.json"));
                    if (file == null)
                    {
                        divisionBackups[i] = new Backup
                        {
                            Scores = []
                        };
                        continue;
                    }

                    using StreamReader reader = new(file);
                    Backup backup = await JsonSerializer.DeserializeAsync<Backup>(reader.BaseStream, JsonService.Settings);
                    _log.LogInformation("Loaded score backup [{File}]", Path.GetFileName(file));
                    divisionBackups[i] = backup;
                }

                backups[j] = divisionBackups;
            }

            _mapService.Index = backups[0].Count(n => n.ActivePlayers != null);
            _backups = backups;
            InternalBackupsLoaded?.Invoke(backups);
        }
        catch (Exception e)
        {
            _log.LogCritical(e, "Exception while loading backup");
        }
    }

    public async Task SaveScores(int division, int index, IReadOnlyList<SavedScore> scores, IReadOnlyList<string>? activePlayers)
    {
        string path = Path.Combine(_directory, $"scores{division:D2}{index:D2}.json");
        if (_backups == null || !_saving.Add(path))
        {
            _log.LogError("Could not save backup");
            return;
        }

        try
        {
            bool backupExists = _backups[division].Length > index;
            Backup backup = new()
            {
                Index = index,
                Scores = scores.ToArray(),
                ActivePlayers = activePlayers?.ToArray() ?? (backupExists ? _backups[division][index].ActivePlayers : null)
            };

            if (backupExists)
            {
                _backups[division][index] = backup;
            }

            await using StreamWriter output = new(path);
            await JsonSerializer.SerializeAsync(output.BaseStream, backup, JsonService.PrettySettings);
            ////_log.LogInformation("Created backup {File}", path);
        }
        catch (Exception e)
        {
            _log.LogCritical(e, "Exception while saving backup");
        }

        _saving.Remove(path);
    }
}
