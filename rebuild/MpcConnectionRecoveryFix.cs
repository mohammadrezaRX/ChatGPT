using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using SandBox;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem;

namespace MultiplayerCampaign
{
    internal static class MpcRecoveryRuntime
    {
        private const string TransferSaveName = "MCC_Transfer";
        private const int LoadTimeoutSeconds = 90;
        private static readonly object Sync = new object();
        private static bool _loading;
        private static bool _loadStarted;
        private static DateTime _loadDeadlineUtc;

        public static bool Loading { get { lock (Sync) return _loading; } }

        public static void BeginLoad()
        {
            lock (Sync)
            {
                _loading = true;
                _loadStarted = false;
                _loadDeadlineUtc = DateTime.UtcNow.AddSeconds(LoadTimeoutSeconds);
            }

            ResetTransferReceivers();
            MultiplayerCampaignSubModule.BeginTransferredWorldLoad();
        }

        public static void MarkLoadStarted()
        {
            lock (Sync)
            {
                if (!_loading)
                    return;
                _loadStarted = true;
            }
        }

        public static void SetClientWorldReady(bool value)
        {
            MultiplayerCampaignSubModule.SetClientWorldReady(value);
        }

        public static void SetClientWorldLoaded(bool value)
        {
            MultiplayerCampaignSubModule.SetClientWorldLoaded(value);
        }

        public static void AbortLoad(string reason)
        {
            lock (Sync)
            {
                _loading = false;
                _loadStarted = false;
            }

            MultiplayerCampaignSubModule.AbortTransferredWorldLoad(reason ?? "Unknown transfer/load failure.");
        }

        public static void Tick()
        {
            bool timeout;
            lock (Sync)
            {
                timeout = _loading && DateTime.UtcNow >= _loadDeadlineUtc;
            }

            if (timeout)
                AbortLoad("Transferred world load timed out.");
        }

        public static string WriteTransferSave(byte[] world)
        {
            return MultiplayerCampaignSubModule.WriteTransferredWorldSave(world, TransferSaveName);
        }

        private static void ResetTransferReceivers()
        {
            MultiplayerCampaignSubModule.ResetTransferReceivers();
        }
    }

    [HarmonyPatch]
    internal static class MpcConnectionRecoveryFix
    {
        [HarmonyPatch(typeof(MultiplayerWorldTransfer), "FinishClientLoad")]
        private static class ClientWorldPatch
        {
            private static bool Prefix()
            {
                try
                {
                    byte[] world = MultiplayerWorldTransfer.GetReceivedWorld();
                    if (world == null || world.Length == 0)
                    {
                        MpcRecoveryRuntime.AbortLoad("No transferred world bytes were received.");
                        return false;
                    }

                    string path = MpcRecoveryRuntime.WriteTransferSave(world);
                    if (path == null || !File.Exists(path))
                    {
                        MpcRecoveryRuntime.AbortLoad("Transferred save could not be written.");
                        return false;
                    }

                    MpcRecoveryRuntime.BeginLoad();
                    LoadResult result = MBSaveLoad.LoadSaveGameData("MCC_Transfer");
                    if (result == null || !result.Successful)
                    {
                        MpcRecoveryRuntime.AbortLoad("MCC transfer save could not be loaded.");
                        return false;
                    }

                    MpcRecoveryRuntime.MarkLoadStarted();
                    MpcRecoveryRuntime.SetClientWorldReady(false);
                    MpcRecoveryRuntime.SetClientWorldLoaded(false);
                    MBGameManager.StartNewGame(new SandBoxGameManager(result));
                    return false;
                }
                catch (Exception ex)
                {
                    MpcRecoveryRuntime.AbortLoad(ex.Message);
                    return false;
                }
            }
        }
    }
}
