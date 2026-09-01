// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using TaleWorlds.CampaignSystem;
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;
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














// ============================================================
// REMOTE SNAPSHOT DISPATCHER
// ============================================================

internal static class RemoteSnapshotDispatcher
{
    private static readonly ConcurrentQueue<
        NetworkPlayerSnapshot>
        Queue =
            new ConcurrentQueue<
                NetworkPlayerSnapshot>();

    public static void Enqueue(
        NetworkPlayerSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        Queue.Enqueue(
            snapshot
        );
    }

    public static void Process()
    {
        int count =
            0;

        while (
            count < 128 &&
            Queue.TryDequeue(
                out NetworkPlayerSnapshot snapshot))
        {
            count++;

            if (snapshot == null)
            {
                continue;
            }

            if (
                snapshot.PlayerId ==
                LocalPlayerState
                    .GetNetworkId())
            {
                continue;
            }

            RemotePlayerRegistry
                .AddOrUpdate(
                    snapshot
                );

            RemotePlayerBridge
                .Queue(
                    snapshot
                );
        }
    }

    public static void Clear()
    {
        while (
            Queue.TryDequeue(
                out _))
        {
        }
    }
}














// ============================================================
// CAMPAIGN THREAD DISPATCHER
// ============================================================

internal static class CampaignThreadDispatcher
{
    private static readonly ConcurrentQueue<
        Action>
        Actions =
            new ConcurrentQueue<
                Action>();

    public static void Enqueue(
        Action action)
    {
        if (action == null)
        {
            return;
        }

        Actions.Enqueue(
            action
        );
    }

    public static void Process()
    {
        int processed = 0;

        while (
            processed < 128 &&
            Actions.TryDequeue(
                out Action action))
        {
            processed++;

            if (action == null)
            {
                continue;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Campaign task error: " +
                    ex.Message
                );
            }
        }
    }

    public static void Clear()
    {
        while (
            Actions.TryDequeue(
                out _))
        {
        }
    }
}














// ============================================================
// CAMPAIGN TICK DISPATCHER
// ============================================================

internal static class CampaignTickDispatcher
{
    private static readonly MultiplayerCampaignTickAdapter
        Adapter =
            new MultiplayerCampaignTickAdapter();

    public static void Tick(
        float dt)
    {
        CampaignSessionCoordinator
            .Update();

        Adapter.Tick(
            dt
        );
    }

    public static void Reset()
    {
        Adapter.Reset();
    }
}














// ============================================================
// FINAL NETWORK CLIENT PATCH
// ============================================================

internal static class FinalNetworkClientPatch
{
    public static void Process(
        NetworkMessage message)
    {
        FinalNetworkMessageRouter
            .Route(
                message
            );
    }
}














// ============================================================
// CAMPAIGN BEHAVIOR SAFE PATCH
// ============================================================

internal static class CampaignBehaviorSafePatch
{
    private static bool _started;

    private static readonly object Sync =
        new object();

    public static void Start()
    {
        lock (Sync)
        {
            if (_started)
            {
                return;
            }

            _started =
                true;
        }

        FinalCampaignBehaviorBridge
            .Tick(
                0f
            );
    }

    public static void Tick(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        FinalErrorGuard.Execute(
            () =>
            {
                FinalCampaignNetworkController
                    .Update(
                        dt
                    );
            }
        );
    }

    public static void Stop()
    {
        lock (Sync)
        {
            _started =
                false;
        }

        FinalCampaignNetworkController
            .Shutdown();
    }
}


namespace MultiplayerCampaign
{

    [HarmonyLib.HarmonyPatch(typeof(MultiplayerCampaignRebuildLayer.MpcNetworkRuntime), "SendLocalState")]
    internal static class MpcFinalBlockHostPartyBroadcastV2
    {
        private static bool Prefix()
        {
            try
            {
                if (!MultiplayerSessionState.IsClient) return true;
                TaleWorlds.CampaignSystem.Party.MobileParty party = MultiplayerCampaignRebuildLayer.MpcClientParty.ActiveParty;
                return MpcFinalOwnershipGuardV2.IsSafeClientParty(party);
            } catch { return false; }
        }
    }













    [HarmonyLib.HarmonyPatch(typeof(MultiplayerCampaignRebuildLayer.MpcClientParty), "Ensure")]
    internal static class MpcFinalBlockSharedPartyV2
    {
        private static void Postfix(ref TaleWorlds.CampaignSystem.Party.MobileParty __result)
        {
            try
            {
                if (MultiplayerSessionState.IsClient && __result == TaleWorlds.CampaignSystem.Party.MobileParty.MainParty) __result = null;
            } catch { __result = null; }
        }
    }













    [HarmonyLib.HarmonyPatch(typeof(MultiplayerNetworkClient), "SendHello")]
    internal static class MpcFinalCharacterGateV2
    {
        private static bool Prefix()
        {
            try
            {
                MpcFinalCharacterSystemV2.EnsureLoaded();
                if (MpcFinalCharacterSystemV2.HasSelection) return true;
                try
                {
                    if (MultiplayerCampaignRebuildLayer.MpcSession.HasSlot) return true;
                } catch { }
                CampaignMessageFeed.Show("Select or create a Client character before joining.");
                return false;
            } catch { return true; }
        }
    }













    [HarmonyLib.HarmonyPatch(typeof(MultiplayerCampaignBehavior), "OnCampaignTick")]
    internal static class MpcFinalCampaignThreadPatchV2
    {
        private static void Postfix(float dt)
        {
            try
            {
                if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;
                MpcFinalCharacterSystemV2.EnsureLoaded();
                // Existing MpcNetworkRuntime already owns network timing; this layer does not run it twice.
            } catch { }
        }
    }













    [HarmonyLib.HarmonyPatch(typeof(MultiplayerCampaignSubModule), "OnGameEnd")]
    internal static class MpcFinalGameEndPatchV2
    {
        private static void Postfix()
        {
            try { MpcFinalCharacterSystemV2.ResetSelection(); } catch { }
        }
    }

}

namespace MultiplayerCampaignRebuildLayer
{

    internal static class MpcRebuildPatches
    {
        [HarmonyPatch(typeof(MultiplayerNetworkClient), "SendHello")]
        private static class ClientHelloPatch
        {
            private static bool Prefix(MultiplayerNetworkClient __instance)
            {
                try
                {
                    MpcSession.SelectOrCreateIdentityFromCurrentPlayer();
                    if (!MpcSession.HasSlot)
                        return true;

                    byte[] payload = NetworkProtocol.CreatePayload(
                        writer =>
                        {
                            writer.Write("MPC2HELLO");
                            writer.Write(MpcSession.Name ?? "Player");
                            writer.Write(MpcSession.Slot);
                            writer.Write(MpcSession.Id ?? "");
                        });

                    __instance.Send(NetworkPacketType.Hello, payload);
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(MultiplayerNetworkClient), "ProcessMessage")]
        private static class ClientMessagePatch
        {
            private static bool Prefix(NetworkMessage message)
            {
                if (message == null || message.Type != NetworkPacketType.WorldPartySnapshot)
                    return true;

                try
                {
                    if (MpcRebuildPatches.ProcessPayload(message.Payload, true))
                        return false;
                }
                catch
                {
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(HostClientConnection), "ProcessMessage")]
        private static class HostMessagePatch
        {
            private static bool Prefix(
                HostClientConnection __instance,
                NetworkPacketType type,
                byte[] payload)
            {
                try
                {
                    if (type == NetworkPacketType.Hello && IsMpcHello(payload))
                    {
                        ApplyHello(__instance, payload);
                        return false;
                    }

                    if (type == NetworkPacketType.WorldPartySnapshot &&
                        MpcRebuildPatches.ProcessPayload(payload, false))
                    {
                        return false;
                    }
                }
                catch
                {
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(MultiplayerCampaignBehavior), "OnCampaignTick")]
        private static class CampaignTickPatch
        {
            private static void Postfix(float dt)
            {
                MpcNetworkRuntime.Tick(dt);
            }
        }

        [HarmonyPatch(typeof(MultiplayerCampaignSubModule), "OnGameEnd")]
        private static class GameEndPatch
        {
            private static void Postfix()
            {
                MpcNetworkRuntime.Clear();
            }
        }

        private static bool ProcessPayload(byte[] payload, bool fromHost)
        {
            try
            {
                return MpcNetworkRuntime.ProcessNetworkPayload(payload, fromHost);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMpcHello(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > 1024)
                return false;

            try
            {
                using (MemoryStream stream = new MemoryStream(payload))
                using (System.IO.BinaryReader reader = new System.IO.BinaryReader(stream, Encoding.UTF8, true))
                    return reader.ReadString() == "MPC2HELLO";
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyHello(
            HostClientConnection connection,
            byte[] payload)
        {
            using (MemoryStream stream = new MemoryStream(payload))
            using (System.IO.BinaryReader reader = new System.IO.BinaryReader(stream, Encoding.UTF8, true))
            {
                string magic = reader.ReadString();
                string name = reader.ReadString();
                int slot = reader.ReadInt32();
                string characterId = reader.ReadString();

                if (magic != "MPC2HELLO" || slot < 0 || slot >= 3 ||
                    string.IsNullOrWhiteSpace(characterId))
                {
                    connection.SendError("Character slot is required.");
                    return;
                }

                PropertyInfo property = typeof(HostClientConnection)
                    .GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                    property.SetValue(connection, characterId, null);

                connection.PlayerName = Sanitize(name);

                connection.Send(new NetworkMessageData(
                    NetworkPacketType.Welcome,
                    NetworkProtocol.CreatePayload(
                        writer =>
                        {
                            writer.Write("Connected as " + connection.PlayerName);
                            writer.Write(characterId);
                        })));

                MultiplayerCampaignHost host = MultiplayerCampaignSubModule.GetHost();
                if (host != null)
                    host.SendWorldToClientAsync(connection);
            }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Player";

            value = value.Trim();
            if (value.Length > 32)
                value = value.Substring(0, 32);
            return value;
        }
    }

}

