// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;

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

