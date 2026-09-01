using System.Collections.Generic;
using HarmonyLib;

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
                HostConsole.WriteLine("[*] Duplicate world transfer suppressed for this client.");
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
}
