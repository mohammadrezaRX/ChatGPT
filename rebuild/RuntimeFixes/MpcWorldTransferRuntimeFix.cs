using System;
using System.Collections.Generic;
using HarmonyLib;
using SandBox;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem.Load;

namespace MultiplayerCampaign
{
    internal static class MpcWorldTransferRuntimeFix
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<HostClientConnection> WorldSent =
            new HashSet<HostClientConnection>();

        public static bool ShouldSendWorld(HostClientConnection client)
        {
            if (client == null)
                return false;

            lock (Sync)
            {
                if (WorldSent.Contains(client))
                    return false;

                WorldSent.Add(client);
                return true;
            }
        }

        public static void ForgetClient(HostClientConnection client)
        {
            if (client == null)
                return;

            lock (Sync)
            {
                WorldSent.Remove(client);
            }
        }
    }

    [HarmonyPatch(typeof(WorldTransferHostService), "Send")]
    internal static class MpcWorldTransferHostOncePatch
    {
        private static bool Prefix(HostClientConnection client)
        {
            if (MpcWorldTransferRuntimeFix.ShouldSendWorld(client))
                return true;

            try
            {
                HostConsole.WriteLine("[*] World transfer already sent for this client; duplicate suppressed.");
            }
            catch
            {
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(HostClientConnection), "Close")]
    internal static class MpcWorldTransferClientClosePatch
    {
        private static void Prefix(HostClientConnection __instance)
        {
            try
            {
                MpcWorldTransferRuntimeFix.ForgetClient(__instance);
            }
            catch
            {
            }
        }
    }

    [HarmonyPatch(typeof(MultiplayerWorldTransfer), "FinishClientLoad")]
    internal static class MpcWorldTransferFinishLoadPatch
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
                if (path == null)
                {
                    MpcRecoveryRuntime.AbortLoad("Transferred save could not be written.");
                    return false;
                }

                MultiplayerWorldTransfer.Clear();
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

                HostConsole.WriteLine("[*] MCC transferred world load started.");
                return false;
            }
            catch (Exception ex)
            {
                try
                {
                    MpcRecoveryRuntime.AbortLoad(ex.ToString());
                }
                catch
                {
                }

                return false;
            }
        }
    }
}
