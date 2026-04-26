using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Controllers;
using Synapse.Extras;
using Synapse.Networking.Models;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using Zenject;
using Object = UnityEngine.Object;

namespace Synapse.Managers;

internal class MenuTakeoverManager : IDisposable, ITickable
{
    private static readonly string _takeoverFolder =
        (Path.GetDirectoryName(Application.streamingAssetsPath) ?? throw new InvalidOperationException()) +
        $"{Path.DirectorySeparatorChar}Synapse{Path.DirectorySeparatorChar}TakeoverBundles";

    private readonly SiraLog _log;
    private readonly Config _config;
    private readonly IInstantiator _instantiator;
    private readonly ListingManager _listingManager;
    private readonly GlobalParticleManager.ParticleHold _particleHold;
    private readonly CancellationTokenManager _cancellationTokenManager;

    private readonly GameObject[] _menuLogo;

    private BundleInfo? _bundleInfo;
    private uint? _lastHash;
    private GameObject? _prefab;
    private bool _enabled;

    private DateTime _startTime;
    private TextMeshPro? _countdownText;
    private bool _disableDust;
    private bool _disableLogo;

    [UsedImplicitly]
    private MenuTakeoverManager(
        SiraLog log,
        Config config,
        IInstantiator instantiator,
        ListingManager listingManager,
        MenuEnvironmentManager menuEnvironmentManager,
        GlobalParticleManager.ParticleHold particleHold,
        CancellationTokenManager cancellationTokenManager)
    {
        _log = log;
        _config = config;
        _instantiator = instantiator;
        config.Updated += OnConfigUpdated;
        _listingManager = listingManager;
        _particleHold = particleHold;
        _cancellationTokenManager = cancellationTokenManager;
        listingManager.ListingFound += OnListingFound;

        GameObject? menu = menuEnvironmentManager._data
            .FirstOrDefault(n => n.menuEnvironmentType == MenuEnvironmentManager.MenuEnvironmentType.Default)
            ?.wrapper;
        if (menu != null)
        {
            List<GameObject> logoObjects = [];
            Transform menuTransform = menu.transform;
            logoObjects.AddRange(
                from Transform childTransform in menuTransform
                where childTransform.name.Contains("Logo") || childTransform.name.StartsWith("GlowLines")
                select childTransform.gameObject);

            _menuLogo = logoObjects.ToArray();
        }
        else
        {
            _menuLogo = [];
        }
    }

    internal bool Enabled
    {
        ////get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Refresh();
        }
    }

    public void Tick()
    {
        if (_countdownText == null)
        {
            return;
        }

        TimeSpan span = _startTime.ToTimeSpan();
        if (span.Ticks < 0)
        {
            span = TimeSpan.Zero;
        }

        string timeText = $"{span.Days:D2}:{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
        _countdownText.text = timeText;
    }

    public void Dispose()
    {
        _listingManager.ListingFound -= OnListingFound;
    }

    private void Refresh()
    {
        bool doEnable = _enabled && !_config.DisableMenuTakeover;
        if (_prefab == null)
        {
            return;
        }

        _prefab.SetActive(doEnable);

        _particleHold.DustDisabled = doEnable && _disableDust;

        foreach (GameObject gameObject in _menuLogo)
        {
            gameObject.SetActive(!(doEnable && _disableLogo));
        }
    }

    private void OnConfigUpdated()
    {
        Refresh();
    }

    private void Reset()
    {
        if (_prefab == null)
        {
            return;
        }

        Object.Destroy(_prefab);
        _prefab = null;
    }

    private async Task Download(string filePath, Listing? listing)
    {
        if (_prefab != null)
        {
            return;
        }

        try
        {
            if (File.Exists(_takeoverFolder))
            {
                await LoadBundle(filePath, listing);
                return;
            }

            string? url = _bundleInfo?.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                _log.Error("No bundle listed");
                return;
            }

            _log.Debug($"Downloading menu takeover bundle from [{url}]");
            UnityWebRequest www = UnityWebRequest.Get(url);
            await www.SendAndVerify(null, _cancellationTokenManager.Reset());
            Directory.CreateDirectory(_takeoverFolder);
            File.WriteAllBytes(filePath, www.downloadHandler.data);
            await LoadBundle(filePath, listing);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.Error($"Exception while loading menu takeover bundle\n{e}");
        }
    }

    private async Task LoadBundle(string filePath, Listing? listing)
    {
        if (_bundleInfo == null)
        {
            throw new InvalidOperationException("No bundle info found.");
        }

        AssetBundle? bundle = await MediaExtensions.LoadFromFileAsync(filePath, _bundleInfo.Hash);
        if (bundle == null)
        {
            FileInfo file = new(filePath);
            if (file.Exists)
            {
                file.Delete();
            }

            throw new InvalidOperationException("Failed to load bundle.");
        }

        string[] prefabNames = bundle.GetAllAssetNames();
        if (prefabNames.Length > 1)
        {
            _log.Warn($"More than one asset found in assetbundle, using first [{prefabNames[0]}]");
        }

        GameObject obj = await bundle.LoadAssetAsyncTask<GameObject>(prefabNames[0]);
        _prefab = _instantiator.InstantiatePrefab(obj);
        _instantiator.InstantiateComponent<TakeoverPrefabAudioController>(_prefab);

        string? countdownPath = listing?.Takeover.CountdownTMP;
        if (!string.IsNullOrEmpty(countdownPath))
        {
            _countdownText = _prefab.transform.Find(countdownPath).GetComponent<TextMeshPro>();
        }

        bundle.Unload(false);

        _prefab.SetActive(false);
        Refresh();
    }

    private void OnListingFound(Listing? listing)
    {
        _bundleInfo = listing?.Takeover.Bundles.FirstOrDefault(b => b.GameVersion.MatchesGameVersion());
        if (_bundleInfo == null)
        {
            _log.Debug("No valid takeover bundle for current game version");
            Reset();
            return;
        }

        _startTime = listing?.Time ?? DateTime.MinValue;
        _disableDust = listing?.Takeover.DisableDust ?? false;
        _disableLogo = listing?.Takeover.DisableLogo ?? false;

        if (_bundleInfo.Hash == _lastHash)
        {
            return;
        }

        _lastHash = _bundleInfo.Hash;

        Reset();

        string listingTitle = listing == null
            ? "undefined"
            : new string(
                listing
                    .Title.Select(
                        j =>
                        {
                            if (char.IsLetter(j) || char.IsNumber(j))
                            {
                                return j;
                            }

                            return '_';
                        })
                    .ToArray());
        _ = Download(Path.Combine(_takeoverFolder, listingTitle), listing);
    }
}
