using System;
using HarmonyLib;
using IPA;
using IPA.Config.Stores;
using JetBrains.Annotations;
using SiraUtil.Zenject;
using Synapse.Installers;
using UnityEngine;
using Logger = IPA.Logging.Logger;

namespace Synapse;

[Plugin(RuntimeOptions.DynamicInit)]
internal class Plugin
{
    private const string LAUNCH_ARGUMENT_LISTING = "--listing";
    private const string LAUNCH_ARGUMENT_HASH = "--skip-hash";

    private readonly Harmony _harmonyInstance = new("dev.aeroluna.Synapse");

    [UsedImplicitly]
    [Init]
    public Plugin(Logger pluginLogger, IPA.Config.Config conf, Zenjector zenjector)
    {
        Log = pluginLogger;

        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].Equals(LAUNCH_ARGUMENT_LISTING, StringComparison.CurrentCultureIgnoreCase) && i + 1 < arguments.Length)
            {
                pluginLogger.Debug($"[{LAUNCH_ARGUMENT_LISTING}] launch argument used, overriding listing");
                ListingOverride = arguments[i + 1];
            }

            // ReSharper disable once InvertIf
            if (arguments[i].Equals(LAUNCH_ARGUMENT_HASH, StringComparison.CurrentCultureIgnoreCase))
            {
                pluginLogger.Debug($"[{LAUNCH_ARGUMENT_HASH}] launch argument used, hash checks will be skipped");
                SkipHash = true;
            }
        }

        zenjector.Install<SynapseAppInstaller>(Location.App, conf.Generated<Config>());
        zenjector.Install<SynapseMenuInstaller>(Location.Menu);
        zenjector.Install<SynapsePlayerInstaller>(Location.Player);
        zenjector.UseLogger(pluginLogger);

        string ver = Application.version;
        GameVersion = ver.Remove(ver.IndexOf("_", StringComparison.Ordinal));
    }

    internal static string GameVersion { get; private set; } = string.Empty;

    internal static Logger Log { get; private set; } = null!;

    internal static string? ListingOverride { get; private set; }

    internal static bool SkipHash { get; private set; }

#pragma warning disable CA1822
    [UsedImplicitly]
    [OnEnable]
    public void OnEnable()
    {
        _harmonyInstance.PatchAll(typeof(Plugin).Assembly);
    }

    [UsedImplicitly]
    [OnDisable]
    public void OnDisable()
    {
        _harmonyInstance.UnpatchSelf();
    }
#pragma warning restore CA1822
}
