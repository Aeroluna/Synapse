﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Controllers;
using Synapse.Extras;
using Synapse.Models;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Synapse.Managers
{
    internal class PrefabManager
    {
        private static readonly string _folder =
            (Path.GetDirectoryName(Application.streamingAssetsPath) ?? throw new InvalidOperationException()) +
            $"{Path.DirectorySeparatorChar}Synapse{Path.DirectorySeparatorChar}Bundles";

        private static readonly int _death = Animator.StringToHash("death");

        private readonly SiraLog _log;
        private readonly CancellationTokenManager _cancellationTokenManager;

        private Listing? _listing;
        private string _filePath = string.Empty;

        private GameObject? _prefab;
        private Animator? _animator;
        private AnimatorDeathController? _deathController;

        private bool _active;

        [UsedImplicitly]
        private PrefabManager(SiraLog log, ListingManager listingManager, CancellationTokenManager cancellationTokenManager)
        {
            _log = log;
            _cancellationTokenManager = cancellationTokenManager;
            listingManager.ListingFound += n =>
            {
                _listing = n;
                string listingTitle = new(n.Title.Select(j =>
                {
                    if (char.IsLetter(j) || char.IsNumber(j))
                    {
                        return j;
                    }

                    return '_';
                }).ToArray());
                _filePath = Path.Combine(_folder, listingTitle);
            };
        }

        internal void Show()
        {
            if (_active)
            {
                return;
            }

            _active = true;

            if (_prefab == null)
            {
                return;
            }

            if (_deathController != null && _deathController.enabled)
            {
                _prefab.SetActive(false);
                _deathController.enabled = false;
            }

            _prefab.SetActive(true);
        }

        internal void Hide()
        {
            if (!_active)
            {
                return;
            }

            _active = false;

            if (_prefab == null)
            {
                return;
            }

            if (_animator == null)
            {
                _prefab.SetActive(false);
            }
            else
            {
                _animator.SetTrigger(_death);
                _deathController!.ContinueAfterDecay(10, () => _prefab.SetActive(false));
            }
        }

        internal async Task Download()
        {
            CancellationToken token = _cancellationTokenManager.Reset();

            try
            {
                if (File.Exists(_filePath))
                {
                    await LoadBundle();
                    return;
                }

                string? url = _listing?.LobbyBundle;
                if (string.IsNullOrWhiteSpace(url))
                {
                    _log.Error("No bundle listed");
                    return;
                }

                UnityWebRequest www = UnityWebRequest.Get(url);
                await www.SendAndVerify(token);
                Directory.CreateDirectory(_folder);
                File.WriteAllBytes(_filePath, www.downloadHandler.data);
                await LoadBundle();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _log.Error($"Exception while loading lobby bundle: {e}");
            }
        }

        private async Task LoadBundle()
        {
            if (_listing == null)
            {
                throw new InvalidOperationException("No listing loaded");
            }

            uint crc = _listing.BundleCrc;
            AssetBundle bundle = await AsyncExtensions.LoadFromFileAsync(_filePath, crc);
            _prefab = Object.Instantiate(bundle.LoadAllAssets<GameObject>().First());
            if (!_active)
            {
                _prefab.SetActive(false);
            }

            _animator = _prefab.GetComponent<Animator>();
            bundle.Unload(false);
            if (_animator == null)
            {
                _log.Error("No animator on prefab");
            }
            else
            {
                _deathController = _prefab.AddComponent<AnimatorDeathController>();
            }
        }
    }
}
