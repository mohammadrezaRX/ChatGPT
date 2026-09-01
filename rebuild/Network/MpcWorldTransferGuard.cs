using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;

namespace MultiplayerCampaign
{
    internal static class MpcWorldTransferGuardState
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<HostClientConnection> Sent = new HashSet<HostClientConnection>();

        public static bool AllowInitial(HostClientConnection connection)
        {
            if (connection == null)
                return false;

            lock (Sync)
            {
                if (Sent.Contains(connection))
                    return false;

                Sent.Add(connection);
                return true;
            }
        }

        public static void Remove(HostClientConnection connection)
        {
            if (connection == null)
                return;

            lock (Sync)
            {
                Sent.Remove(connection);
            }
        }
    }

    [HarmonyPatch(typeof(HostClientConnection), "SendWorldSafelyAsync")]
    internal static class MpcWorldTransferDuplicatePatch
    {
        private static bool Prefix(HostClientConnection __instance, ref Task __result)
        {
            if (MpcWorldTransferGuardState.AllowInitial(__instance))
                return true;

            __result = Task.CompletedTask;
            return false;
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
