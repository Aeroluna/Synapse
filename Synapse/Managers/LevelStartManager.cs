using System;
using System.Reflection;
using HarmonyLib;
using IPA.Loader;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.HarmonyPatches;
using Synapse.Models;
using Synapse.Networking.Models;
using Zenject;

namespace Synapse.Managers;

internal class LevelStartManager : IDisposable
{
    private static readonly Action<string>? _nextLevelIsIsolated = GetNextLevelIsIsolated();

    private readonly Action? _disableCustomPlatform;
#if !PRE_V1_37_1
    private readonly EnvironmentsListModel _environmentsListModel;
#endif

    private readonly Config _config;
    private readonly GameplaySetupViewController _gameplaySetupViewController;
    private readonly HeckIntegrationManager? _heckIntegrationManager;
    private readonly MenuTransitionsHelper _menuTransitionsHelper;
    private readonly NetworkManager _networkManager;
    private readonly NoEnergyModifier _noEnergyModifier;

    private Ruleset? _ruleset;

    [UsedImplicitly]
    private LevelStartManager(
        SiraLog log,
        Config config,
        DiContainer container,
        GameplaySetupViewController gameplaySetupViewController,
        MenuTransitionsHelper menuTransitionsHelper,
#if !PRE_V1_37_1
        EnvironmentsListModel environmentsListModel,
#endif
        NetworkManager networkManager,
        NoEnergyModifier noEnergyModifier,
        [InjectOptional] HeckIntegrationManager? heckIntegrationManager)
    {
        _config = config;
        _gameplaySetupViewController = gameplaySetupViewController;
        _menuTransitionsHelper = menuTransitionsHelper;
#if !PRE_V1_37_1
        _environmentsListModel = environmentsListModel;
#endif
        _networkManager = networkManager;
        _noEnergyModifier = noEnergyModifier;
        _heckIntegrationManager = heckIntegrationManager;
        networkManager.MapUpdated += OnMapUpdated;

        // to disable custom platforms
        PluginMetadata? customPlatforms = PluginManager.GetPlugin("Custom Platforms");

        // ReSharper disable once InvertIf
        if (customPlatforms != null)
        {
            // cannot use ConnectionManager as it is not bound
            Type? platformManagerType = customPlatforms.Assembly.GetType("CustomFloorPlugin.PlatformManager");
            if (platformManagerType != null)
            {
                object? platformsConnectionManager = container.TryResolve(platformManagerType);
                if (platformsConnectionManager != null)
                {
                    MethodInfo? setPlatform = platformManagerType
                        .GetProperty(
                            "APIRequestedPlatform",
                            AccessTools.all)
                        ?.GetSetMethod(true);
                    MethodInfo? getDefault = platformManagerType
                        .GetProperty(
                            "DefaultPlatform",
                            AccessTools.all)
                        ?.GetGetMethod();
                    if (setPlatform == null)
                    {
                        log.Error("Could not find [CustomFloorPlugin.PlatformManager.APIRequestedPlatform] setter");
                    }
                    else if (getDefault == null)
                    {
                        log.Error("Could not find [CustomFloorPlugin.PlatformManager.DefaultPlatform] getter");
                    }
                    else
                    {
                        object defaultPlatform = getDefault.Invoke(platformsConnectionManager, null);
                        _disableCustomPlatform = () => setPlatform.Invoke(
                            platformsConnectionManager,
                            [defaultPlatform]);
                    }
                }
                else
                {
                    log.Error("Could not resolve [CustomFloorPlugin.PlatformManager] instance");
                }
            }
            else
            {
                log.Error("Could not find [CustomFloorPlugin.ConnectionManager] type");
            }
        }
    }

#pragma warning disable SA1300
    // ReSharper disable InconsistentNaming
    private enum GameplayModifier
    {
        noFailOn0Energy,
        instaFail,
        failOnSaberClash,
        noBombs,
        fastNotes,
        strictAngles,
        disappearingArrows,
        noArrows,
        ghostNotes,
        proMode,
        zenMode,
        smallCubes,
        noEnergy // custom modifier
    }
#pragma warning restore SA1300

    public void Dispose()
    {
        _networkManager.MapUpdated -= OnMapUpdated;
    }

    // WARNING: ruleset has lower priority than heck map settings
    public void StartLevel(
        DownloadedMap downloadedMap,
        Action<DownloadedMap, StandardLevelScenesTransitionSetupDataSO, LevelCompletionResults>? levelFinishedCallback)
    {
        ColorScheme? overrideColorScheme;
        if (_ruleset?.AllowOverrideColors != null && !_ruleset.AllowOverrideColors.Value)
        {
            overrideColorScheme = null;
        }
        else
        {
            overrideColorScheme = _gameplaySetupViewController.colorSchemesSettings.GetOverrideColorScheme();
        }

        PlayerSpecificSettings playerSpecificSettings;
        if (_ruleset?.AllowLeftHand != null && !_ruleset.AllowLeftHand.Value)
        {
            playerSpecificSettings = _gameplaySetupViewController.playerSettings.CopyWith();
            playerSpecificSettings._leftHanded = false;
        }
        else
        {
            playerSpecificSettings = _gameplaySetupViewController.playerSettings;
        }

        GameplayModifiers modifiers = new();
        if (_ruleset is { Modifiers: not null })
        {
            foreach (string rulesetModifier in _ruleset.Modifiers)
            {
                if (!Enum.TryParse(rulesetModifier, true, out GameplayModifier modifier))
                {
                    continue;
                }

                switch (modifier)
                {
                    case GameplayModifier.noFailOn0Energy:
                        modifiers._noFailOn0Energy = true;
                        break;

                    case GameplayModifier.instaFail:
                        modifiers._instaFail = true;
                        break;

                    case GameplayModifier.failOnSaberClash:
                        modifiers._failOnSaberClash = true;
                        break;

                    case GameplayModifier.noBombs:
                        modifiers._noBombs = true;
                        break;

                    case GameplayModifier.fastNotes:
                        modifiers._fastNotes = true;
                        break;

                    case GameplayModifier.strictAngles:
                        modifiers._strictAngles = true;
                        break;

                    case GameplayModifier.disappearingArrows:
                        modifiers._disappearingArrows = true;
                        break;

                    case GameplayModifier.noArrows:
                        modifiers._noArrows = true;
                        break;

                    case GameplayModifier.ghostNotes:
                        modifiers._ghostNotes = true;
                        break;

                    case GameplayModifier.proMode:
                        modifiers._proMode = true;
                        break;

                    case GameplayModifier.zenMode:
                        modifiers._zenMode = true;
                        break;

                    case GameplayModifier.smallCubes:
                        modifiers._smallCubes = true;
                        break;

                    case GameplayModifier.noEnergy:
                        _noEnergyModifier.NoEnergyNextMap = true;
                        break;
                }
            }
        }

        Action<StandardLevelScenesTransitionSetupDataSO, LevelCompletionResults>? callback = null;
        if (levelFinishedCallback != null)
        {
            callback = (a, b) => levelFinishedCallback(downloadedMap, a, b);
        }

        int division = _config.LastEvent.Division ?? 0;

#if !PRE_V1_37_1
        BeatmapKey beatmapKey = downloadedMap.BeatmapKeys[division];
        BeatmapLevel beatmapLevel = downloadedMap.BeatmapLevel;
#if !LATEST
        ColorScheme? beatmapOverrideColorScheme =
            beatmapLevel.GetColorScheme(beatmapKey.beatmapCharacteristic, beatmapKey.difficulty);
#endif
#elif !V1_29_1
        ColorScheme? beatmapOverrideColorScheme = null;
        if (downloadedMap is { BeatmapLevel: CustomBeatmapLevel customBeatmapLevel } &&
            downloadedMap.DifficultyBeatmaps[division] is CustomDifficultyBeatmap customDifficultyBeatmap)
        {
            beatmapOverrideColorScheme =
                customBeatmapLevel.GetBeatmapLevelColorScheme(customDifficultyBeatmap.beatmapColorSchemeIdx);
        }
#endif

        _disableCustomPlatform?.Invoke();
        _nextLevelIsIsolated?.Invoke("Synapse");
        IntroSkipInstallationPatch.SkipNext = true;

        StartStandardOrHeck(
            "screw yo analytics",
#if !PRE_V1_37_1
            beatmapKey,
            beatmapLevel,
#else
            downloadedMap.DifficultyBeatmaps[division],
            downloadedMap.BeatmapLevel,
#endif
            null, // no environment override
            overrideColorScheme,
#if !PRE_V1_40_8
            _gameplaySetupViewController.colorSchemesSettings.ShouldOverrideLightshowColors(),
#endif
#if !V1_29_1 && !LATEST
            beatmapOverrideColorScheme,
#endif
            modifiers,
            playerSpecificSettings,
            null,
#if !PRE_V1_37_1
            _environmentsListModel,
#endif
#if LATEST
            new GameplayAdditionalInformation(string.Empty),
            null,
            null,
#else
            string.Empty, // doesnt matter, gets reset by animation anyways
            false,
            false,
            null,
#endif
#if !PRE_V1_37_1 && !LATEST
            null,
#endif
            callback,
#if !V1_29_1
            null,
#endif
#if LATEST
            null,
#endif
            null);
    }

    private static Action<string>? GetNextLevelIsIsolated()
    {
        // to disable introskip
        PluginMetadata? bsUtils = PluginManager.GetPlugin("BS_Utils");
        if (bsUtils == null)
        {
            return null;
        }

        Type? type = bsUtils.Assembly.GetType("BS_Utils.Gameplay.Gamemode");
        MethodInfo? method = type?.GetMethod("NextLevelIsIsolated", BindingFlags.Static | BindingFlags.Public);
        if (type == null)
        {
            Plugin.Log.Error("Could not find [BS_Utils.Gameplay.Gamemode] type");
        }
        else if (method == null)
        {
            Plugin.Log.Error("Could not find [BS_Utils.Gameplay.Gamemode.NextLevelIsIsolated] method");
        }
        else
        {
            return (Action<string>?)method.CreateDelegate(typeof(Action<string>), null);
        }

        return null;
    }

    private void OnMapUpdated(int _, Map map)
    {
        _ruleset = map.Ruleset;
    }

    // i wish i could use my StartStandardLevelParameters here
    private void StartStandardOrHeck(
        string gameMode,
#if !PRE_V1_37_1
        in BeatmapKey beatmapKey,
        BeatmapLevel beatmapLevel,
#else
        IDifficultyBeatmap difficultyBeatmap,
        IPreviewBeatmapLevel previewBeatmapLevel,
#endif
        OverrideEnvironmentSettings? overrideEnvironmentSettings,
        ColorScheme? overrideColorScheme,
#if !PRE_V1_40_8
        bool playerOverrideLightshowColors,
#endif
#if !V1_29_1 && !LATEST
        ColorScheme? beatmapOverrideColorScheme,
#endif
        GameplayModifiers gameplayModifiers,
        PlayerSpecificSettings playerSpecificSettings,
        PracticeSettings? practiceSettings,
#if !PRE_V1_37_1
        EnvironmentsListModel environmentsListModel,
#endif
#if LATEST
        GameplayAdditionalInformation gameplayAdditionalInformation,
        Action? beforeSceneSwitchToGameplayCallback,
        Action<DiContainer>? afterSceneSwitchToGameplayCallback,
#else
        string backButtonText,
        bool useTestNoteCutSoundEffects,
        bool startPaused,
        Action? beforeSceneSwitchCallback,
#endif
#if !PRE_V1_37_1 && !LATEST
        Action<DiContainer>? afterSceneSwitchCallback,
#endif
        Action<StandardLevelScenesTransitionSetupDataSO, LevelCompletionResults>? levelFinishedCallback,
#if !V1_29_1
        Action<LevelScenesTransitionSetupDataSO, LevelCompletionResults>? levelRestartedCallback,
#if LATEST
        IBeatmapLevelData? beatmapLevelData,
#endif
        RecordingToolManager.SetupData? recordingToolData)
#else
        Action<LevelScenesTransitionSetupDataSO, LevelCompletionResults>? levelRestartedCallback)
#endif
    {
        if (_heckIntegrationManager != null)
        {
            _heckIntegrationManager.StartPlayViewInterruptedLevel(
                gameMode,
#if !PRE_V1_37_1
                beatmapKey,
                beatmapLevel,
#else
                difficultyBeatmap,
                previewBeatmapLevel,
#endif
                overrideEnvironmentSettings,
                overrideColorScheme,
#if !PRE_V1_40_8
                playerOverrideLightshowColors,
#endif
#if !V1_29_1 && !LATEST
                beatmapOverrideColorScheme,
#endif
                gameplayModifiers,
                playerSpecificSettings,
                practiceSettings,
#if !PRE_V1_37_1
                environmentsListModel,
#endif
#if LATEST
                gameplayAdditionalInformation,
                beforeSceneSwitchToGameplayCallback,
                afterSceneSwitchToGameplayCallback,
#else
                backButtonText,
                useTestNoteCutSoundEffects,
                startPaused,
                beforeSceneSwitchCallback,
#endif
#if !PRE_V1_37_1 && !LATEST
                afterSceneSwitchCallback,
#endif
                levelFinishedCallback,
#if !V1_29_1
                levelRestartedCallback,
#if LATEST
                beatmapLevelData,
#endif
                recordingToolData);
#else
                levelRestartedCallback);
#endif
        }
        else
        {
            _menuTransitionsHelper.StartStandardLevel(
                gameMode,
#if !PRE_V1_37_1
                beatmapKey,
                beatmapLevel,
#else
                difficultyBeatmap,
                previewBeatmapLevel,
#endif
                overrideEnvironmentSettings,
                overrideColorScheme,
#if !PRE_V1_40_8
                playerOverrideLightshowColors,
#endif
#if !V1_29_1 && !LATEST
                beatmapOverrideColorScheme,
#endif
                gameplayModifiers,
                playerSpecificSettings,
                practiceSettings,
#if !PRE_V1_37_1
                environmentsListModel,
#endif
#if LATEST
                gameplayAdditionalInformation,
                beforeSceneSwitchToGameplayCallback,
                afterSceneSwitchToGameplayCallback,
#else
                backButtonText,
                useTestNoteCutSoundEffects,
                startPaused,
                beforeSceneSwitchCallback,
#endif
#if !PRE_V1_37_1 && !LATEST
                afterSceneSwitchCallback,
#endif
                levelFinishedCallback,
#if !V1_29_1
                levelRestartedCallback,
#if LATEST
                beatmapLevelData,
#endif
                recordingToolData);
#else
                levelRestartedCallback);
#endif
        }
    }
}
