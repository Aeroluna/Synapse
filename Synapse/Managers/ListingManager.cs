using System;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json;
using SiraUtil.Logging;
using Synapse.Extras;
using Synapse.Networking.Models;
using UnityEngine;
using UnityEngine.Networking;
using Zenject;

namespace Synapse.Managers;

internal class ListingManager : IInitializable
{
    private readonly CancellationTokenManager _cancellationTokenManager;
    private readonly Config _config;
    private readonly SiraLog _log;
    private Sprite? _bannerImage;
    private string? _bannerUrl;
    private string? _lastListing;

    private Task? _initializeTask;

    [UsedImplicitly]
    private ListingManager(SiraLog log, Config config, CancellationTokenManager cancellationTokenManager)
    {
        _log = log;
        _config = config;
        _cancellationTokenManager = cancellationTokenManager;
    }

    public event Action<Sprite?>? BannerImageCreated
    {
        add
        {
            if (_bannerImage != null)
            {
                value?.Invoke(_bannerImage);
            }

            BannerImageCreatedBacking += value;
        }

        remove => BannerImageCreatedBacking -= value;
    }

    public event Action<Listing?>? ListingFound
    {
        add
        {
            if (Listing != null)
            {
                value?.Invoke(Listing);
            }

            ListingFoundBacking += value;
        }

        remove => ListingFoundBacking -= value;
    }

    private event Action<Sprite?>? BannerImageCreatedBacking;

    private event Action<Listing?>? ListingFoundBacking;

    public Listing? Listing { get; private set; }

    public void Initialize()
    {
        if ((!_initializeTask?.IsCompleted ?? false) ||
            Listing != null)
        {
            return;
        }

        _initializeTask = InitializeAsync(_cancellationTokenManager.Reset());
    }

    public void Clear()
    {
        Listing = null;
        _bannerUrl = null;
        _bannerImage = null;
        _lastListing = null;
        ListingFoundBacking?.Invoke(null);
        BannerImageCreatedBacking?.Invoke(null);
    }

    private async Task GetBannerImage(Listing listing, CancellationToken token)
    {
        if (_bannerUrl != listing.BannerImage)
        {
            _bannerUrl = listing.BannerImage;

            try
            {
                _log.Debug($"Fetching banner image from [{listing.BannerImage}]");
                Sprite bannerImage = await MediaExtensions.RequestSprite(listing.BannerImage, token);
                _bannerImage = bannerImage;
                BannerImageCreatedBacking?.Invoke(bannerImage);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _log.Error($"Exception while fetching promo banner image\n{e}");
                BannerImageCreatedBacking?.Invoke(null);
            }
        }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        try
        {
            string url = Plugin.ListingOverride ?? _config.Url;
            _log.Debug($"Checking [{url}] for active listing");
            UnityWebRequest www = UnityWebRequest.Get(url);
            await www.SendAndVerify(token);

            string json = www.downloadHandler.text;
            Listing? listing = JsonConvert.DeserializeObject<Listing>(json, JsonSettings.Settings);
            if (listing == null)
            {
                _log.Error("Error deserializing listing");
                return;
            }

            if (listing.Guid == _lastListing)
            {
                return;
            }

            _lastListing = listing.Guid;
            _log.Debug($"Found active listing for [{listing.Title}]");

            Listing = listing;
            ListingFoundBacking?.Invoke(Listing);
            if (_config.LastEvent.Title != listing.Title)
            {
                _config.LastEvent = new EventInfo
                {
                    Title = listing.Title
                };
            }

            _ = GetBannerImage(listing, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Warn($"Exception while loading listing\n{e}");
            _lastListing = null;
            ListingFoundBacking?.Invoke(null);
            BannerImageCreatedBacking?.Invoke(null);
            throw;
        }
    }
}
