// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using TaleWorlds.CampaignSystem;
using BinaryReader = System.IO.BinaryReader;
using Helpers;
using MultiplayerCampaign;
using SandBox;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.ViewModelCollection.InitialMenu;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem.Load;
using TaleWorlds.SaveSystem;
using TaleWorlds.ScreenSystem;

namespace MultiplayerCampaign
{


    /*
     * ============================================================
     * MAIN SUBMODULE
     * ============================================================
     */

    public sealed class MultiplayerCampaignSubModule
        : MBSubModuleBase
    {
        internal const string HostSaveName =
            "MCC";

        private static MultiplayerCampaignHost _host;

        private static bool _hostRequested;

        private static bool _loadingTransferredWorld;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            HostConsole.Initialize();

            HostConsole.WriteLine(
                "[*] Multiplayer Campaign loaded."
            );

            new Harmony(
                "MultiplayerCampaign"
            ).PatchAll();
        }

        protected override void OnGameStart(
            Game game,
            IGameStarter gameStarter)
        {
            base.OnGameStart(
                game,
                gameStarter
            );

            if (
                game.GameType is Campaign &&
                gameStarter is CampaignGameStarter starter)
            {
                starter.AddBehavior(
                    new MultiplayerCampaignBehavior()
                );
            }
        }

        public static void RequestHost()
        {
            _hostRequested = true;
        }

        public static bool IsHostRequested()
        {
            return _hostRequested;
        }

        public static void BeginTransferredWorldLoad()
        {
            _loadingTransferredWorld = true;
        }

        public static void EndTransferredWorldLoad()
        {
            _loadingTransferredWorld = false;
        }

        public static bool IsLoadingTransferredWorld()
        {
            return _loadingTransferredWorld;
        }

        internal static MultiplayerCampaignHost GetHost()
        {
            return _host;
        }

        public static void StopHost()
        {
            MultiplayerCampaignHost host =
                _host;

            _host = null;
            _hostRequested = false;

            if (host != null)
            {
                host.Stop();
            }
        }

        /*
         * ========================================================
         * LOAD MCC HOST
         * ========================================================
         */

        public static bool LoadHostCampaign()
        {
            try
            {
                HostConsole.WriteLine(
                    "[*] Loading MCC..."
                );

                if (
                    !MBSaveLoad.IsSaveGameFileExists(
                        HostSaveName))
                {
                    HostConsole.WriteLine(
                        "[!] MCC save was not found."
                    );

                    return false;
                }

                LoadResult result =
                    MBSaveLoad.LoadSaveGameData(
                        HostSaveName
                    );

                if (
                    result == null ||
                    !result.Successful)
                {
                    HostConsole.WriteLine(
                        "[!] MCC save could not be loaded."
                    );

                    return false;
                }

                HostConsole.WriteLine(
                    "[*] MCC load requested."
                );

                SandBoxGameManager manager =
                    new SandBoxGameManager(
                        result
                    );

                MBGameManager.StartNewGame(
                    manager
                );

                return true;
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] MCC load error: " +
                    ex.Message
                );

                return false;
            }
        }

        /*
         * ========================================================
         * START HOST
         * ========================================================
         */

        public static void StartHostIfReady()
        {
            if (!_hostRequested)
            {
                return;
            }

            if (_host != null)
            {
                return;
            }

            if (Campaign.Current == null)
            {
                return;
            }

            _host =
                new MultiplayerCampaignHost(
                    LocalPlayerState.GetDisplayName()
                );

            _host.Start();
        }

        /*
         * ========================================================
         * GAME END
         * ========================================================
         */

        public override void OnGameEnd(
            Game game)
        {
            /*
             * When Client's old Campaign is destroyed while
             * the transferred MCC Campaign is being loaded,
             * TCP must remain alive.
             */

            if (_loadingTransferredWorld)
            {
                base.OnGameEnd(game);
                return;
            }

            RemotePlayerManager.Clear();

            StopHost();

            MultiplayerNetworkClient
                .Instance
                .Disconnect();

            base.OnGameEnd(game);
        }

        protected override void OnSubModuleUnloaded()
        {
            RemotePlayerManager.Clear();

            StopHost();

            MultiplayerNetworkClient
                .Instance
                .Disconnect();

            base.OnSubModuleUnloaded();
        }
    }

}

