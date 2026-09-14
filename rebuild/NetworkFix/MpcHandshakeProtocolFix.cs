using System;
using System.Reflection;
using HarmonyLib;

namespace MultiplayerCampaign
{
    internal static class MpcHandshakeProtocolFix
    {
        [HarmonyPatch(typeof(HostClientConnection), "HandleHello")]
        private static class HostHelloPatch
        {
            private static bool Prefix(
                HostClientConnection __instance,
                byte[] payload)
            {
                if (__instance == null)
                    return false;

                string playerId;
                string playerName;

                if (!SessionHandshake.ReadHello(
                    payload,
                    out playerId,
                    out playerName))
                {
                    __instance.SendError("Invalid handshake packet.");
                    return false;
                }

                __instance.PlayerId = Guid.NewGuid().ToString("N");
                __instance.PlayerName = NetworkUtilities.SafeName(playerName);
                __instance.Ready = false;

                __instance.Send(
                    new NetworkMessageData(
                        NetworkPacketType.Welcome,
                        SessionHandshake.BuildWelcome(
                            __instance.PlayerId,
                            "Connected as " + __instance.PlayerName
                        )
                    )
                );

                try
                {
                    HostConnectionEvents.Connected(__instance);
                }
                catch
                {
                }

                HostConsole.WriteLine(
                    "[MultiplayerCampaign] Player connected: " +
                    __instance.PlayerName
                );

                try
                {
                    MethodInfo method = AccessTools.Method(
                        typeof(HostClientConnection),
                        "SendWorldSafelyAsync");

                    method?.Invoke(
                        __instance,
                        null
                    );
                }
                catch (Exception ex)
                {
                    __instance.SendError(
                        "World synchronization failed: " +
                        ex.Message
                    );
                }

                return false;
            }
        }
    }
}
