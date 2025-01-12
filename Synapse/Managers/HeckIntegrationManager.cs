﻿using System;
using System.Reflection;
using HarmonyLib;
using IPA.Loader;
using JetBrains.Annotations;
using Zenject;

namespace Synapse.Managers;

internal class HeckIntegrationManager
{
    private readonly object _playViewManager;

    private readonly ConstructorInfo _parametersConstructor;
    private readonly MethodInfo _forceStart;

    [UsedImplicitly]
    private HeckIntegrationManager(DiContainer container)
    {
        Assembly assembly = PluginManager.GetPlugin("Heck").Assembly;
        Type? playViewManagerType = assembly.GetType("Heck.PlayView.PlayViewManager");
        if (playViewManagerType == null)
        {
            throw new InvalidOperationException("Failed to get Heck.PlayView.PlayViewManager type");
        }

        _playViewManager = container.Resolve(playViewManagerType);
        _forceStart = AccessTools.Method(playViewManagerType, "ForceStart");
        Type? startStandardLevelParametersType = assembly.GetType("Heck.PlayView.StartStandardLevelParameters");
        if (startStandardLevelParametersType == null)
        {
            throw new InvalidOperationException("Failed to get Heck.PlayView.StartStandardLevelParameters type");
        }

        _parametersConstructor = AccessTools.FirstConstructor(
            startStandardLevelParametersType,
            n => n.GetParameters().Length > 1);
    }

    internal void StartPlayViewInterruptedLevel(
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
#if LATEST
        bool playerOverrideLightshowColors,
#endif
#if !V1_29_1
        ColorScheme? beatmapOverrideColorScheme,
#endif
        GameplayModifiers gameplayModifiers,
        PlayerSpecificSettings playerSpecificSettings,
        PracticeSettings? practiceSettings,
#if !PRE_V1_37_1
        EnvironmentsListModel environmentsListModel,
#endif
        string backButtonText,
        bool useTestNoteCutSoundEffects,
        bool startPaused,
        Action? beforeSceneSwitchCallback,
#if !PRE_V1_37_1
        Action<DiContainer>? afterSceneSwitchCallback,
#endif
        Action<StandardLevelScenesTransitionSetupDataSO, LevelCompletionResults>? levelFinishedCallback,
#if !V1_29_1
        Action<LevelScenesTransitionSetupDataSO, LevelCompletionResults>? levelRestartedCallback,
        RecordingToolManager.SetupData? recordingToolData)
#else
        Action<LevelScenesTransitionSetupDataSO, LevelCompletionResults>? levelRestartedCallback)
#endif
    {
        object parameters = _parametersConstructor.Invoke([
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
#if LATEST
            playerOverrideLightshowColors,
#endif
#if !V1_29_1
            beatmapOverrideColorScheme,
#endif
            gameplayModifiers,
            playerSpecificSettings,
            practiceSettings,
#if !PRE_V1_37_1
            environmentsListModel,
#endif
            backButtonText,
            useTestNoteCutSoundEffects,
            startPaused,
            beforeSceneSwitchCallback,
#if !PRE_V1_37_1
            afterSceneSwitchCallback,
#endif
            levelFinishedCallback,
#if !V1_29_1
            levelRestartedCallback,
            recordingToolData
#else
            levelRestartedCallback
#endif
        ]);
        _forceStart.Invoke(_playViewManager, [parameters]);
    }
}
