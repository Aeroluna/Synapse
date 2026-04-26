using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Synapse.Server.Extras;
using Synapse.Server.Models;

namespace Synapse.Server.Services;

public interface IBlacklistService
{
    public IReadOnlyDictionary<string, byte> BannedIps { get; }

    public IReadOnlyDictionary<string, SerializedBannedUser> Blacklist { get; }

    public IReadOnlyDictionary<string, SerializedUser>? Whitelist { get; }

    public bool AddBannedIp(string ip);

    public bool AddBlacklist(string id, string username, string? reason, DateTime? banTime);

    public bool AddWhitelist(string id, string username);

    public Task LoadBannedIps(bool verbatim);

    public Task LoadBlacklist(bool verbatim);

    public Task LoadWhitelist(bool verbatim);

    public bool RemoveBannedIp(string ip);

    public bool RemoveBlacklist(SerializedUser user);

    public bool RemoveWhitelist(SerializedUser user);
}

public class BlacklistService : IBlacklistService
{
    private readonly ILogger<BlacklistService> _log;
    private readonly ITimeoutService _timeoutService;
    private readonly IJsonService _jsonService;

    private readonly string _bannedIpsPath;

    private readonly string _blacklistPath;

    private readonly string _whitelistPath;

    private ConcurrentDictionary<string, byte> _bannedIps = new(); // wouldve preferred ConcurrentHashset

    private ConcurrentDictionary<string, SerializedBannedUser> _blacklist = new();
    private ConcurrentDictionary<string, SerializedUser>? _whitelist;

    public BlacklistService(
        ILogger<BlacklistService> log,
        ITimeoutService timeoutService,
        IJsonService jsonService,
        IDirectoryService directoryService)
    {
        _log = log;
        _timeoutService = timeoutService;
        _jsonService = jsonService;
        string currentDirectory = directoryService.ActiveDirectory;
        _blacklistPath = Path.Combine(currentDirectory, "blacklist.json");
        _whitelistPath = Path.Combine(currentDirectory, "whitelist.json");
        _bannedIpsPath = Path.Combine(currentDirectory, "bannedips.json");
        if (File.Exists(_whitelistPath))
        {
            _whitelist = new ConcurrentDictionary<string, SerializedUser>();
            _ = LoadWhitelist(false);
        }

        _ = LoadBlacklist(false);
        _ = LoadBannedIps(false);
    }

    public IReadOnlyDictionary<string, byte> BannedIps => _bannedIps;

    public IReadOnlyDictionary<string, SerializedBannedUser> Blacklist => _blacklist;

    public IReadOnlyDictionary<string, SerializedUser>? Whitelist => _whitelist;

    public bool AddBannedIp(string ip)
    {
        bool result = _bannedIps.TryAdd(ip, 0);
        SaveBannedIps(result);
        return result;
    }

    public bool AddBlacklist(string id, string username, string? reason, DateTime? banTime)
    {
        bool result = _blacklist.TryAdd(
            id,
            new SerializedBannedUser
            {
                Id = id,
                Username = username,
                Reason = reason ?? string.Empty,
                BanTime = banTime
            });
        SaveBlacklist(result);
        return result;
    }

    public bool AddWhitelist(string id, string username)
    {
        _whitelist ??= new ConcurrentDictionary<string, SerializedUser>();
        bool result = _whitelist.TryAdd(
            id,
            new SerializedUser
            {
                Id = id,
                Username = username
            });
        SaveWhitelist(result);
        return result;
    }

    public async Task LoadBannedIps(bool verbatim)
    {
        ConcurrentDictionary<string, byte>? ips =
            await _jsonService.LoadJson<List<string>, ConcurrentDictionary<string, byte>>(
                _bannedIpsPath,
                n => new ConcurrentDictionary<string, byte>(n.ToDictionary(j => j, _ => (byte)0)),
                verbatim);
        if (verbatim && ips != null)
        {
            _log.LogInformation("Successfully loaded bannedips [{Path}]", _bannedIpsPath);
        }

        _bannedIps = ips ?? new ConcurrentDictionary<string, byte>();
    }

    public async Task LoadBlacklist(bool verbatim)
    {
        ConcurrentDictionary<string, SerializedBannedUser>? users =
            await _jsonService.LoadJson<List<SerializedBannedUser>, ConcurrentDictionary<string, SerializedBannedUser>>(
                _blacklistPath,
                n => new ConcurrentDictionary<string, SerializedBannedUser>(n.ToDictionary(j => j.Id, j => j)),
                verbatim);
        if (verbatim && users != null)
        {
            _log.LogInformation("Successfully loaded blacklist [{Path}]", _blacklistPath);
        }

        _blacklist = users ?? new ConcurrentDictionary<string, SerializedBannedUser>();
    }

    public async Task LoadWhitelist(bool verbatim)
    {
        ConcurrentDictionary<string, SerializedUser>? users =
            await _jsonService.LoadJson<List<SerializedUser>, ConcurrentDictionary<string, SerializedUser>>(
                _whitelistPath,
                n => new ConcurrentDictionary<string, SerializedUser>(n.ToDictionary(j => j.Id, j => j)),
                verbatim);
        if (verbatim && users != null)
        {
            _log.LogInformation("Successfully loaded whitelist [{Path}]", _whitelistPath);
        }

        _whitelist = users;
    }

    public bool RemoveBannedIp(string ip)
    {
        bool result = _bannedIps.TryRemove(ip, out _);
        SaveBannedIps(result);
        return result;
    }

    public bool RemoveBlacklist(SerializedUser user)
    {
        bool result = _blacklist.TryRemove(user.Id, out _);
        SaveBlacklist(result);
        return result;
    }

    public bool RemoveWhitelist(SerializedUser user)
    {
        if (_whitelist == null)
        {
            return false;
        }

        bool result = _whitelist.TryRemove(user.Id, out _);
        SaveWhitelist(result);
        return result;
    }

    private void SaveBannedIps(bool cont)
    {
        if (cont)
        {
            _timeoutService.Timeout(() => _ = _jsonService.SaveJson(_bannedIps, _bannedIpsPath), 2000);
        }
    }

    private void SaveBlacklist(bool cont)
    {
        if (cont)
        {
            _timeoutService.Timeout(() => _ = _jsonService.SaveJson(_blacklist.Values, _blacklistPath), 2000);
        }
    }

    private void SaveWhitelist(bool cont)
    {
        if (cont && _whitelist != null)
        {
            _timeoutService.Timeout(() => _ = _jsonService.SaveJson(_whitelist.Values, _whitelistPath), 2000);
        }
    }
}
