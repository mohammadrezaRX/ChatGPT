using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;

namespace MultiplayerCampaign
{
    internal static class MpcWorldTransferGuardState
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<HostClientConnection> Sent = new HashSet<HostClientConnection>();
        private static readonly HashSet<HostClientConnection> ResyncRequested = new HashSet<HostClientConnection>();

        public static bool AllowInitialOrResync(HostClientConnection connection)
        {
            if (connection == null)
                return false;

            lock (Sync)
            {
                if (ResyncRequested.Remove(connection))
                    return true;

                if (Sent.Contains(connection))
                    return false;

                Sent.Add(connection);
                return true;
            }
        }

        public static void MarkResync(HostClientConnection connection)
        {
            if (connection == null)
                return;

            lock (Sync)
            {
                ResyncRequested.Add(connection);
            }
        }

        public static void Remove(HostClientConnection connection)
        {
            if (connection == null)
                return;

            lock (Sync)
            {
                Sent.Remove(connection);
                ResyncRequested.Remove(connection);
            }
        }
    }

    [HarmonyPatch(typeof(HostClientConnection), "SendWorldSafelyAsync")]
    internal static class MpcWorldTransferDuplicatePatch
    {
        private static bool Prefix(HostClientConnection __instance, ref Task __result)
        {
            if (MpcWorldTransferGuardState.AllowInitialOrResync(__instance))
                return true;

            __result = Task.CompletedTask;
            return false;
        }
    }

    [HarmonyPatch(typeof(HostClientConnection), "HandleResyncRequest")]
    internal static class MpcWorldTransferResyncPatch
    {
        private static void Prefix(HostClientConnection __instance)
        {
            MpcWorldTransferGuardState.MarkResync(__instance);
        }
    }

    [HarmonyPatch(typeof(HostClientConnection), "Close")]
    internal static class MpcWorldTransferConnectionCleanupPatch
    {
        private static void Postfix(HostClientConnection __instance)
        {
            MpcWorldTransferGuardState.Remove(__instance);
        }
    }
}
