using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Synapse.Server.Extras;
using Synapse.Server.Models;

namespace Synapse.Server.Services;

public interface IRoleService
{
    public IReadOnlyDictionary<string, RoleService.RoleData> RoleDatas { get; }

    public IReadOnlyDictionary<string, Role> Roles { get; }

    public bool AddRole(string id, string username, Role role);

    public bool AddRole(RoleService.RoleData user, Role role);

    public Task LoadAdmins(bool verbatim);

    public Task LoadRoles(bool verbatim);

    public bool RemoveRole(string id, Role role);

    public bool RemoveRole(RoleService.RoleData user, Role role);

    [Pure]
    public bool TryGetRoleData(string id, [NotNullWhen(true)] out RoleService.RoleData? roleData);
}

public class RoleService : IRoleService
{
    private readonly string _adminsPath;
    private readonly ILogger<RoleService> _log;
    private readonly ITimeoutService _timeoutService;
    private readonly IJsonService _jsonService;
    private readonly ConcurrentDictionary<string, RoleData> _roleDatas = new();

    private readonly ConcurrentDictionary<string, Role> _roles = new();

    private readonly string _rolesPath;

    public RoleService(
        ILogger<RoleService> log,
        ITimeoutService timeoutService,
        IJsonService jsonService,
        IDirectoryService directoryService)
    {
        _log = log;
        _timeoutService = timeoutService;
        _jsonService = jsonService;
        string currentDirectory = directoryService.ActiveDirectory;
        _rolesPath = Path.Combine(currentDirectory, "roles.json");
        _adminsPath = Path.Combine(currentDirectory, "admins.json");
        _ = LoadRoles(false);
        _ = LoadAdmins(false);
    }

    public IReadOnlyDictionary<string, RoleData> RoleDatas => _roleDatas;

    public IReadOnlyDictionary<string, Role> Roles => _roles;

    public bool AddRole(string id, string username, Role role)
    {
        if (!_roleDatas.TryGetValue(id, out RoleData? user))
        {
            _roleDatas.TryAdd(id, user = new RoleData(_log, id, username, _roles));
        }

        return AddRole(user, role);
    }

    public bool AddRole(RoleData user, Role role)
    {
        bool result = user.AddRole(role.Name);
        SaveAdmins(result);
        return result;
    }

    public async Task LoadAdmins(bool verbatim)
    {
        ConcurrentDictionary<string, SerializedRoleUser>? admins =
            await _jsonService.LoadJson<List<SerializedRoleUser>, ConcurrentDictionary<string, SerializedRoleUser>>(
                _adminsPath,
                n => new ConcurrentDictionary<string, SerializedRoleUser>(n.ToDictionary(j => j.Id, j => j)),
                verbatim);
        if (verbatim && admins != null)
        {
            _log.LogInformation("Successfully loaded admins [{Path}]", _adminsPath);
        }

        _roleDatas.Clear();
        if (admins != null)
        {
            foreach ((string id, SerializedRoleUser serializedRoleUser) in admins)
            {
                RoleData roleData = new(_log, id, serializedRoleUser.Username, _roles, serializedRoleUser.Roles);
                _roleDatas.TryAdd(id, roleData);
            }
        }
    }

    public async Task LoadRoles(bool verbatim)
    {
        List<Role>? roles =
            await _jsonService.LoadJson<List<Role>, List<Role>>(
                _rolesPath,
                n => n,
                verbatim);
        if (verbatim && roles != null)
        {
            _log.LogInformation("Successfully loaded roles [{Path}]", _rolesPath);
        }

        _roles.Clear();
        if (roles == null)
        {
            _roles.TryAdd(
                "moderator",
                new Role
                {
                    Name = "moderator",
                    Priority = 10,
                    Color = "red",
                    Permission = Permission.Moderator
                });
            SaveRoles(true);
        }
        else
        {
            roles.ForEach(n => _roles.TryAdd(n.Name, n));
        }

        foreach (RoleData roleDataValue in _roleDatas.Values)
        {
            roleDataValue.SetDirty(true);
        }
    }

    public bool RemoveRole(string id, Role role)
    {
        return _roleDatas.TryGetValue(id, out RoleData? user) && RemoveRole(user, role);
    }

    public bool RemoveRole(RoleData user, Role role)
    {
        bool result = user.RemoveRole(role.Name);
        SaveAdmins(result);
        return result;
    }

    [Pure]
    public bool TryGetRoleData(string id, [NotNullWhen(true)] out RoleData? roleData)
    {
        return _roleDatas.TryGetValue(id, out roleData);
    }

    private void SaveAdmins(bool cont)
    {
        if (cont)
        {
            _timeoutService.Timeout(
                () => _ = _jsonService.SaveJson(
                    _roleDatas.Values.Select(n => n.ToSerialized()),
                    _adminsPath),
                2000);
        }
    }

    private void SaveRoles(bool cont)
    {
        if (cont)
        {
            _timeoutService.Timeout(() => _ = _jsonService.SaveJson(_roles.Values, _rolesPath), 2000);
        }
    }

    public class RoleData
    {
        private readonly ConcurrentDictionary<string, Role> _allRoles;
        private readonly ILogger _log;
        private readonly ConcurrentDictionary<string, byte> _roles;
        private string? _color;
        private bool _dirty = true;
        private int _immunity;
        private Permission _permission;

        internal RoleData(
            ILogger log,
            string id,
            string username,
            ConcurrentDictionary<string, Role> allRoles,
            IEnumerable<string>? roles = null)
        {
            _log = log;
            Id = id;
            Username = username;
            _allRoles = allRoles;
            _roles = roles == null
                ? new ConcurrentDictionary<string, byte>()
                : new ConcurrentDictionary<string, byte>(roles.ToDictionary(n => n, _ => (byte)0));
        }

        public string? Color
        {
            get
            {
                Refresh();
                return _color;
            }
        }

        public string Id { get; }

        public int Immunity
        {
            get
            {
                Refresh();
                return _immunity;
            }
        }

        public Permission Permission
        {
            get
            {
                Refresh();
                return _permission;
            }
        }

        public IReadOnlyList<string> Roles => _roles.Keys.ToImmutableList();

        public string Username { get; }

        public bool AddRole(string role)
        {
            bool result = _roles.TryAdd(role, 0);
            SetDirty(result);
            return result;
        }

        public bool RemoveRole(string role)
        {
            bool result = _roles.TryRemove(role, out _);
            SetDirty(result);
            return result;
        }

        public SerializedRoleUser ToSerialized()
        {
            return new SerializedRoleUser
            {
                Id = Id,
                Username = Username,
                Roles = Roles
            };
        }

        public override string ToString()
        {
            return $"[({Id}) {Username}: {string.Join(", ", Roles)}]";
        }

        internal void SetDirty(bool cont)
        {
            if (cont)
            {
                _dirty = true;
            }
        }

        private void Refresh()
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;

            string[] roleNames = _roles.Keys.ToArray();
            List<Role> roles = new(roleNames.Length);
            foreach (string roleName in roleNames)
            {
                if (_allRoles.TryGetValue(roleName, out Role? role))
                {
                    roles.Add(role);
                }
                else
                {
                    _log.LogError("No role named {Role}", roleName);
                }
            }

            roles = roles.OrderByDescending(n => n.Priority).ToList();

            _immunity = roles.FirstOrDefault()?.Priority ?? 0;

            _color = (from role in roles where role.Color != null select role.Color).FirstOrDefault();

            _permission = 0;
            roles.ForEach(n => _permission |= n.Permission);
        }
    }
}
