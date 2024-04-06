﻿using JetBrains.Annotations;
using Synapse.HarmonyPatches;
using Synapse.Managers;
using Synapse.Views;
using Zenject;

namespace Synapse.Installers
{
    [UsedImplicitly]
    internal class SynapseMenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            if (IPA.Loader.PluginManager.GetPlugin("Heck") != null)
            {
                Container.Bind<HeckIntegrationManager>().AsSingle();
            }

            Container.BindInterfacesTo<AddEventFlowCoordinator>().AsSingle();
            Container.BindInterfacesTo<AddMainMenuEventButton>().AsSingle();

            Container.Bind<EventFlowCoordinator>().FromFactory<EventFlowCoordinator.EventFlowCoordinatorFactory>();
            Container.Bind<EventModsViewController>().FromNewComponentAsViewController().AsSingle();
            Container.Bind<EventModsDownloadingViewController>().FromNewComponentAsViewController().AsSingle();
            Container.Bind<EventLoadingViewController>().FromNewComponentAsViewController().AsSingle();
            Container.Bind<EventLobbyViewController>().FromNewComponentAsViewController().AsSingle();
            Container.Bind<EventLeaderboardViewController>().FromNewComponentAsViewController().AsSingle();
            ////Container.Bind<EventMapDownloadingViewController>().FromNewComponentAsViewController().AsSingle();

            Container.BindInterfacesAndSelfTo<PrefabManager>().AsSingle();

            Container.BindInterfacesAndSelfTo<MapDownloadingManager>().AsSingle();
            Container.BindInterfacesAndSelfTo<CountdownManager>().AsSingle();
            Container.Bind<LevelStartManager>().AsSingle();

            Container.BindInterfacesAndSelfTo<PromoManager>().AsSingle();
            Container.Bind<NotificationManager>().FromFactory<NotificationManager.NotificationManagerFactory>().NonLazy();
        }
    }
}
