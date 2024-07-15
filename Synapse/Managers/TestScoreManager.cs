﻿#if DEBUG
using JetBrains.Annotations;
using Newtonsoft.Json;
using SiraUtil.Logging;
using Synapse.Extras;
using Synapse.Models;
using UnityEngine;
using Zenject;
using Random = System.Random;

namespace Synapse.Managers;

internal class TestScoreManager : ITickable
{
    private readonly SiraLog _log;
    private readonly NetworkManager _networkManager;
    private readonly Random _random = new();

    [UsedImplicitly]
    private TestScoreManager(SiraLog log, NetworkManager networkManager)
    {
        _log = log;
        _networkManager = networkManager;
    }

    public void Tick()
    {
        if (!Input.GetKeyDown(KeyCode.KeypadPlus))
        {
            return;
        }

        ScoreSubmission scoreSubmission = new()
        {
            Index = _networkManager.Status.Index,
            Score = _random.Next(999999),
            Percentage = (float)_random.NextDouble()
        };
        string scoreJson = JsonConvert.SerializeObject(scoreSubmission, JsonSettings.Settings);
        _log.Info(scoreJson);
        _ = _networkManager.SendString(scoreJson, ServerOpcode.ScoreSubmission);
    }
}
#endif
