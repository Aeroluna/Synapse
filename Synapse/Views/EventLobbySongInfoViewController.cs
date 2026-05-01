using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using HMUI;
using IPA.Utilities.Async;
using JetBrains.Annotations;
using Synapse.Extras;
using Synapse.HarmonyPatches;
using Synapse.Managers;
using Synapse.Models;
using Synapse.Networking.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;
using Random = UnityEngine.Random;

namespace Synapse.Views;

[ViewDefinition("Synapse.Resources.LobbySongInfo.bsml")]
internal class EventLobbySongInfoViewController : BSMLAutomaticViewController
{
    private static readonly string[] _randomHeaders =
    [
        "Upcoming",
        "Coming soon",
        "Next up",
        "As seen on TV",
        "Bringing you",
        "Stay tuned for",
        "In the pipeline",
        "Next on the agenda",
        "Prepare for",
        "Launching soon",
        "Coming your way",
        "Next in our lineup",
        "Brace yourself for",
        "Watch out for",
        "Unveiling",
        "Arriving now"
    ];

    private static Sprite? _finishPlaceholder;

    [UIComponent("cover")]
    private readonly ImageView _coverImage = null!;

    [UIComponent("header")]
    private readonly ImageView _header = null!;

    [UIComponent("headertext")]
    private readonly TextMeshProUGUI _headerText = null!;

    [UIComponent("songtext")]
    private readonly TextMeshProUGUI _songText = null!;

    [UIComponent("artisttext")]
    private readonly TextMeshProUGUI _authorText = null!;

    [UIObject("spinny")]
    private readonly GameObject _loading = null!;

    [UIObject("loading")]
    private readonly GameObject _loadingGroup = null!;

    [UIComponent("progress")]
    private readonly TextMeshProUGUI _progress = null!;

    [UIObject("songinfo")]
    private readonly GameObject _songInfo = null!;

    [UIObject("countdownobject")]
    private readonly GameObject _countdownObject = null!;

    [UIComponent("countdown")]
    private readonly TextMeshProUGUI _countdown = null!;

    [UIObject("scoreobject")]
    private readonly GameObject _scoreObject = null!;

    [UIComponent("scoretext")]
    private readonly TextMeshProUGUI _score = null!;

    [UIComponent("percentagetext")]
    private readonly TextMeshProUGUI _percentage = null!;

    [UIObject("startobject")]
    private readonly GameObject _startObject = null!;

    [UIComponent("starttext")]
    private readonly TextMeshProUGUI _startText = null!;

    [UIComponent("startbutton")]
    private readonly TextMeshProUGUI _startButton = null!;

    [UIObject("map")]
    private readonly GameObject _mapInfo = null!;

    [UIObject("finish")]
    private readonly GameObject _finish = null!;

    [UIComponent("finishimage")]
    private readonly ImageView _finishImage = null!;

    private NetworkManager _networkManager = null!;
    private CountdownManager _countdownManager = null!;
    private MapDownloadingManager _mapDownloadingManager = null!;
    private CancellationTokenManager _cancellationTokenManager = null!;
    private InfoSpriteManager _infoSpriteManager = null!;
    private TimeSyncManager _timeSyncManager = null!;
    private RainbowString _rainbowString = null!;

    private Sprite _coverPlaceholder = null!;

    private Sprite? _introSprite;
    private Sprite? _finishSprite;

    private float _angle;
#if !PRE_V1_37_1
    private BeatmapLevel? _beatmapLevel;
#else
    private IPreviewBeatmapLevel? _beatmapLevel;
#endif
    private float _startTime;
    private PlayerScore? _playerScore;
    private Map _map = new();
    private IStageStatus? _stage;

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);

        rectTransform.sizeDelta = new Vector2(-120, 0);

        if (firstActivation)
        {
            _coverImage.material = Resources.FindObjectsOfTypeAll<Material>().First(n => n.name == "UINoGlowRoundEdge");
            _coverPlaceholder = _coverImage.sprite;

            _header.color0 = new Color(1, 1, 1, 1);
            _header.color1 = new Color(1, 1, 1, 0);

            _songText.enableAutoSizing = true;
            _authorText.enableAutoSizing = true;
            _songText.enableWordWrapping = false;
            _authorText.enableWordWrapping = false;
            _songText.fontSizeMin = _songText.fontSize / 4;
            _songText.fontSizeMax = _songText.fontSize;
            _authorText.fontSizeMin = _authorText.fontSize / 4;
            _authorText.fontSizeMax = _authorText.fontSize;

            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_songInfo.transform);

            _startObject.SetActive(false);
            _rainbowString.SetString(_startText.text);
        }

        if (addedToHierarchy)
        {
            _infoSpriteManager.IntroImageCreated += OnIntroImageCreated;
            _infoSpriteManager.FinishImageCreated += OnFinishImageCreated;

            if (_networkManager.Status.Stage is PlayStatus playStatus)
            {
                _startTime = playStatus.StartTime;
                _playerScore = playStatus.PlayerScore;
                _map = playStatus.Map;
                RefreshMap();
            }

            OnStageUpdated(_networkManager.Status.Stage);
            _networkManager.StageUpdated += OnStageUpdated;
            _networkManager.PlayerScoreUpdated += OnPlayerScoreUpdated;
            _networkManager.StartTimeUpdated += OnStartTimeUpdated;
            _networkManager.MapUpdated += OnMapUpdated;
            _mapDownloadingManager.MapDownloaded += OnMapDownloaded;
            _mapDownloadingManager.ProgressUpdated += OnProgressUpdated;
            _countdownManager.CountdownUpdated += OnCountdownUpdated;
            _countdownManager.Refresh();
        }

        _headerText.text = _randomHeaders[Random.Range(0, _randomHeaders.Length - 1)] + "...";
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);

        // ReSharper disable once InvertIf
        if (removedFromHierarchy)
        {
            _infoSpriteManager.IntroImageCreated -= OnIntroImageCreated;
            _infoSpriteManager.FinishImageCreated -= OnFinishImageCreated;
            _networkManager.StageUpdated -= OnStageUpdated;
            _networkManager.PlayerScoreUpdated -= OnPlayerScoreUpdated;
            _networkManager.StartTimeUpdated -= OnStartTimeUpdated;
            _networkManager.MapUpdated -= OnMapUpdated;
            _mapDownloadingManager.MapDownloaded -= OnMapDownloaded;
            _mapDownloadingManager.ProgressUpdated -= OnProgressUpdated;
            _mapDownloadingManager.Cancel();
            _countdownManager.CountdownUpdated -= OnCountdownUpdated;
        }
    }

    [UsedImplicitly]
    [Inject]
    private void Construct(
        NetworkManager networkManager,
        MapDownloadingManager mapDownloadingManager,
        CancellationTokenManager cancellationTokenManager,
        CountdownManager countdownManager,
        InfoSpriteManager infoSpriteManager,
        TimeSyncManager timeSyncManager,
        RainbowString rainbowString)
    {
        _networkManager = networkManager;
        _mapDownloadingManager = mapDownloadingManager;
        _cancellationTokenManager = cancellationTokenManager;
        _countdownManager = countdownManager;
        _infoSpriteManager = infoSpriteManager;
        _timeSyncManager = timeSyncManager;
        _rainbowString = rainbowString;
    }

    private void Awake()
    {
        if (_finishPlaceholder == null)
        {
            _finishPlaceholder =
                MediaExtensions.GetEmbeddedResourceSprite("Synapse.Resources.finish_placeholder.png");
        }
    }

    private void Update()
    {
        if (_loadingGroup.activeInHierarchy)
        {
            _angle += Time.deltaTime * 200;
            _loading.transform.localEulerAngles = new Vector3(0, 0, _angle);
        }
        else if (_startObject.activeInHierarchy)
        {
            _startText.SetCharArray(_rainbowString.ToCharArray());
        }
    }

    private void RefreshSongInfo()
    {
        CancellationToken token = _cancellationTokenManager.Reset();
        string? altCoverUrl = string.IsNullOrWhiteSpace(_map.AltCoverUrl) ? null : _map.AltCoverUrl;
        if (altCoverUrl != null && _playerScore == null)
        {
            _ = SetCoverImage(MediaExtensions.RequestSprite(altCoverUrl, token));
            _songText.text = "???";
            _authorText.text = "??? [???]";
        }
        else if (_beatmapLevel != null)
        {
#if !PRE_V1_37_1
#if !PRE_V1_40_8
            Task<Sprite> spriteTask = _beatmapLevel.previewMediaData.GetCoverSpriteAsync();
#else
            Task<Sprite> spriteTask = _beatmapLevel.previewMediaData.GetCoverSpriteAsync(token);
#endif
            string authorName = string.Join(", ", _beatmapLevel.allMappers);
#else
            Task<Sprite> spriteTask = _beatmapLevel.GetCoverImageAsync(token);
            string authorName = _beatmapLevel.levelAuthorName;
#endif
            _ = SetCoverImage(spriteTask);
            _songText.text = _beatmapLevel.songName;
            _authorText.text = $"{_beatmapLevel.songAuthorName} [{authorName}]";
        }
        else
        {
            _coverImage.sprite = _coverPlaceholder;
            _songText.text = "???";
            _authorText.text = "??? [???]";
        }

        if (_playerScore != null)
        {
            if (_map.Ruleset?.AllowResubmission ?? false)
            {
                _startButton.text = "rescore";
                _startObject.SetActive(true);
                _scoreObject.SetActive(false);
            }
            else
            {
                _score.text = $"{ScoreFormatter.Format(_playerScore.Score)}";
                _percentage.text = $"{EventLeaderboardVisuals.FormatPercentage(_playerScore.Percentage)}";
                _startObject.SetActive(false);
                _scoreObject.SetActive(true);
            }

            _countdownObject.SetActive(false);
        }
        else
        {
            if (_startTime > _timeSyncManager.SyncTime)
            {
                _startObject.SetActive(false);
                _scoreObject.SetActive(false);
                _countdownObject.SetActive(true);
            }
            else
            {
                _startButton.text = "play";
                _startObject.SetActive(true);
                _scoreObject.SetActive(false);
                _countdownObject.SetActive(false);
            }
        }

        return;

        async Task SetCoverImage(Task<Sprite> spriteTask)
        {
            _coverImage.sprite = await spriteTask;
        }
    }

    private void OnMapDownloaded(DownloadedMap map)
    {
        _beatmapLevel = map.BeatmapLevel;
        _songInfo.SetActive(true);
        _loadingGroup.SetActive(false);
        RefreshSongInfo();
    }

    private void OnStageUpdated(IStageStatus stage)
    {
        UnityMainThreadTaskScheduler.Factory.StartNew(() => RefreshStage(stage));
    }

    private void OnPlayerScoreUpdated(PlayerScore? playerScore)
    {
        _playerScore = playerScore;
        UnityMainThreadTaskScheduler.Factory.StartNew(RefreshSongInfo);
    }

    private void OnMapUpdated(int _, Map map)
    {
        _map = map;
        UnityMainThreadTaskScheduler.Factory.StartNew(RefreshMap);
    }

    private void RefreshStage(IStageStatus stage)
    {
        _stage = stage;
        switch (stage)
        {
            case PlayStatus:
                _mapInfo.SetActive(true);
                _finish.SetActive(false);
                break;

            case IntroStatus:
            case FinishStatus:
                _mapInfo.SetActive(false);
                _finish.SetActive(true);
                RefreshInfoSprite();
                break;
        }
    }

    private void RefreshMap()
    {
        _progress.text = "Loading...";
        _beatmapLevel = null;
        _songInfo.SetActive(false);
        _loadingGroup.SetActive(true);
        RefreshSongInfo();
    }

    private void RefreshInfoSprite()
    {
        if (_finishPlaceholder == null ||
            (_stage is not IntroStatus &&
            _stage is not FinishStatus))
        {
            return;
        }

        Sprite? image = _stage switch
        {
            IntroStatus => _introSprite,
            FinishStatus => _finishSprite,
            _ => null
        };
        _finishImage.sprite = image != null ? image : _finishPlaceholder;
    }

    private void OnStartTimeUpdated(float startTime)
    {
        _startTime = startTime;
        UnityMainThreadTaskScheduler.Factory.StartNew(RefreshSongInfo);
    }

    private void OnCountdownUpdated(string text)
    {
        _countdown.text = text;
    }

    private void OnProgressUpdated(string message)
    {
        _progress.text = message;
    }

    private void OnIntroImageCreated(Sprite? image)
    {
        _introSprite = image;
        RefreshInfoSprite();
    }

    private void OnFinishImageCreated(Sprite? image)
    {
        _finishSprite = image;
        RefreshInfoSprite();
    }

    [UsedImplicitly]
    [UIAction("start-click")]
    private void OnStartClick()
    {
        _countdownManager.ManualStart();
    }
}
