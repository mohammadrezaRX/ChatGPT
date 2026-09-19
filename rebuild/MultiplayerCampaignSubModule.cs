// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// The thematic source remains generated/maintained by the repository build process.
using System;
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using TaleWorlds.CampaignSystem;
using BinaryReader = System.IO.BinaryReader;
using HarmonyLib;
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
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
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

        private static bool _clientRequested;

        private static string _clientAddress;

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
            _clientRequested = false;
            _clientAddress = null;
        }

        public static bool IsHostRequested()
        {
            return _hostRequested;
        }

        public static void RequestClient(
            string ip)
        {
            _clientRequested =
                !string.IsNullOrWhiteSpace(ip);

            _clientAddress =
                _clientRequested
                    ? ip.Trim()
                    : null;

            _hostRequested = false;
        }

        public static bool LoadClientCampaign()
        {
            if (Campaign.Current != null)
                return true;

            try
            {
                HostConsole.WriteLine(
                    "[*] Loading local MCC for Client..."
                );

                if (!MBSaveLoad.IsSaveGameFileExists(
                        HostSaveName))
                {
                    HostConsole.WriteLine(
                        "[!] Local MCC save was not found on Client."
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
                        "[!] Local MCC save could not be loaded on Client."
                    );
                    return false;
                }

                MBGameManager.StartNewGame(
                    new SandBoxGameManager(
                        result
                    )
                );

                return true;
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Client MCC load error: " +
                    ex.Message
                );

                return false;
            }
        }

        public static void StartClientIfReady()
        {
            if (
                !_clientRequested ||
                string.IsNullOrWhiteSpace(
                    _clientAddress) ||
                Campaign.Current == null)
            {
                return;
            }

            string ip =
                _clientAddress;

            _clientAddress = null;
            _clientRequested = false;

            HostConsole.WriteLine(
                "[*] Local MCC ready. Joining world session..."
            );

            MultiplayerNetworkClient
                .Instance
                .Connect(ip);
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
            if (Campaign.Current != null)
            {
                HostConsole.WriteLine(
                    "[*] MCC already loaded. Reusing current Campaign."
                );
                return true;
            }

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

            MultiplayerSessionId
                .CreateWorldId(
                    HostSaveName
                );

            MultiplayerSessionState
                .StartHost();

            _host =
                new MultiplayerCampaignHost(
                    LocalPlayerState.GetDisplayName()
                );

            _host.Start();

            HostConsole.WriteLine(
                "[*] World session: " +
                MultiplayerSessionId.Get()
            );
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

            _clientRequested = false;
            _clientAddress = null;

            base.OnSubModuleUnloaded();
        }
    
    [HarmonyPatch(
        typeof(SandBoxGameManager),
        "OnLoadFinished"
    )]
    internal static class MpcLocalCampaignLoadPatch
    {
        private static void Postfix()
        {
            try
            {
                MultiplayerCampaignSubModule
                    .StartClientIfReady();
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Client session start failed: " +
                    ex.Message
                );
            }
        }
    }
    }

}

