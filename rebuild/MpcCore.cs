// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
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









/*
 * ============================================================
 * HOST CONSOLE
 * ============================================================
 */

internal static class HostConsole
{
    private static readonly object Sync =
        new object();

    private static bool _initialized;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
        }
    }

    public static void WriteLine(
        string message)
    {
        if (
            string.IsNullOrWhiteSpace(
                message))
        {
            return;
        }

        lock (Sync)
        {
            try
            {
                InformationManager.DisplayMessage(
                    new InformationMessage(
                        message
                    )
                );
            }
            catch
            {
            }

            try
            {
                Debug.Print(
                    "[MultiplayerCampaign] " +
                    message
                );
            }
            catch
            {
            }
        }
    }
}









// ============================================================
// HOST STATE UPDATE SERVICE
// ============================================================

internal static class HostStateUpdateService
{
    private static float _timer;

    public static void Update(
        float dt,
        MultiplayerCampaignHost host)
    {
        if (host == null)
        {
            return;
        }

        if (
            Campaign.Current == null ||
            MobileParty.MainParty == null)
        {
            return;
        }

        _timer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_timer < 0.10f)
        {
            return;
        }

        _timer =
            0f;

        HostClientConnection[] clients =
            host.GetClientsSnapshot();

        if (clients == null)
        {
            return;
        }

        CampaignVec2 hostPosition =
            MobileParty.MainParty.Position;

        int hostPartySize =
            CampaignWorld.GetMainPartySize();

        byte[] hostPayload =
            HostSnapshotBuilder.Build(
                LocalPlayerState.GetNetworkId(),
                LocalPlayerState.GetDisplayName(),
                hostPosition.X,
                hostPosition.Y,
                hostPartySize
            );

        if (hostPayload == null)
        {
            return;
        }

        NetworkMessageData hostMessage =
            new NetworkMessageData(
                NetworkPacketType.PlayerSnapshot,
                hostPayload
            );

        for (
            int i = 0;
            i < clients.Length;
            i++)
        {
            HostClientConnection client =
                clients[i];

            if (
                client == null ||
                !client.Ready)
            {
                continue;
            }

            try
            {
                client.Send(
                    hostMessage
                );
            }
            catch
            {
            }
        }

        /*
         * Send each client's most recent state to the
         * other clients.
         *
         * This is important for actual two-way synchronization.
         */

        for (
            int i = 0;
            i < clients.Length;
            i++)
        {
            HostClientConnection sender =
                clients[i];

            if (
                sender == null ||
                !sender.Ready)
            {
                continue;
            }

            byte[] clientPayload =
                HostSnapshotBuilder.Build(
                    sender.PlayerId,
                    sender.PlayerName,
                    sender.LastX,
                    sender.LastY,
                    sender.LastPartySize
                );

            if (clientPayload == null)
            {
                continue;
            }

            NetworkMessageData clientMessage =
                new NetworkMessageData(
                    NetworkPacketType.PlayerSnapshot,
                    clientPayload
                );

            for (
                int j = 0;
                j < clients.Length;
                j++)
            {
                HostClientConnection receiver =
                    clients[j];

                if (
                    receiver == null ||
                    receiver == sender ||
                    !receiver.Ready)
                {
                    continue;
                }

                try
                {
                    receiver.Send(
                        clientMessage
                    );
                }
                catch
                {
                }
            }
        }
    }
}









// ============================================================
// MASTER CLEANUP REGISTRY
// ============================================================

internal static class MasterCleanupRegistry
{
    public static void Clear()
    {
        try
        {
            RemotePlayerFinalCleanup
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerMapRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerWorldViewRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSessionRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerSnapshotCache
                .Clear();
        }
        catch
        {
        }

        try
        {
            WorldTransferService
                .Reset();
        }
        catch
        {
        }

        try
        {
            WorldSynchronizationController
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerWorldSyncState
                .Reset();
        }
        catch
        {
        }

        try
        {
            HandshakeState
                .Reset();
        }
        catch
        {
        }

        try
        {
            NetworkIdentityService
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionId
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerCampaignGameState
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerSnapshotState
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerSnapshotSendTimer
                .Reset();
        }
        catch
        {
        }

        try
        {
            RemoteSnapshotDispatcher
                .Clear();
        }
        catch
        {
        }

        try
        {
            CampaignThreadDispatcher
                .Clear();
        }
        catch
        {
        }
    }
}









// ============================================================
// FINAL STATE CLEANUP
// ============================================================

internal static class FinalStateCleanup
{
    public static void Execute()
    {
        try
        {
            FinalSessionReset
                .ResetAll();
        }
        catch
        {
        }

        try
        {
            MultiplayerUIStateManager
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerWorldSyncState
                .Reset();
        }
        catch
        {
        }

        try
        {
            WorldSynchronizationController
                .Reset();
        }
        catch
        {
        }

        try
        {
            HostWorldStateController
                .Reset();
        }
        catch
        {
        }

        try
        {
            HostPlayerSnapshotCache
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostClientStateRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSessionRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerFinalCleanup
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerUpdatePipeline
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSnapshotDispatcher
                .Clear();
        }
        catch
        {
        }

        try
        {
            CampaignThreadDispatcher
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerMapRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerWorldViewRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerNameRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerPartyRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerPositionRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            MultiplayerConnectionStatus
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerCampaignGameState
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignSessionCoordinator
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignInitializationGuard
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerSnapshotState
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerSnapshotSendTimer
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerIdentity
                .Reset();
        }
        catch
        {
        }

        try
        {
            NetworkIdentityService
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionId
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignReadinessMonitor
                .Update(
                    0f
                );
        }
        catch
        {
        }
    }
}









// ============================================================
// SAFE PLAYER POSITION APPLICATION
// ============================================================

internal static class SafeRemotePositionApplication
{
    public static void Apply(
        RemotePlayerState state,
        float dt)
    {
        if (
            state == null ||
            !state.Active)
        {
            return;
        }

        if (
            !MultiplayerSessionDiagnostics
                .ValidateRemote(
                    state))
        {
            return;
        }

        CampaignVec2 current =
            state.CurrentPosition;

        CampaignVec2 target =
            state.TargetPosition;

        CampaignVec2 next =
            RemotePlayerInterpolator
                .Interpolate(
                    current,
                    target,
                    dt
                );

        if (
            !NetworkUtilities
                .IsValidPosition(
                    next.X,
                    next.Y))
        {
            return;
        }

        state.CurrentPosition =
            next;

        RemotePlayerPositionRegistry
            .Set(
                state.PlayerId,
                next
            );
    }
}









// ============================================================
// FINAL ERROR GUARD
// ============================================================

internal static class FinalErrorGuard
{
    public static void Execute(
        Action action)
    {
        if (action == null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (NullReferenceException ex)
        {
            HostConsole.WriteLine(
                "[!] Multiplayer Campaign null state: " +
                ex.Message
            );
        }
        catch (InvalidOperationException ex)
        {
            HostConsole.WriteLine(
                "[!] Multiplayer Campaign operation error: " +
                ex.Message
            );
        }
        catch (Exception ex)
        {
            HostConsole.WriteLine(
                "[!] Multiplayer Campaign error: " +
                ex.Message
            );
        }
    }
}









// ============================================================
// FINAL SUBMODULE STATE
// ============================================================

internal static class FinalSubModuleState
{
    private static bool _loaded;

    private static bool _campaignStarted;

    private static readonly object Sync =
        new object();

    public static void OnLoad()
    {
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            _loaded =
                true;
        }

        HostConsole.WriteLine(
            "[MultiplayerCampaign] " +
            "Multiplayer Campaign initialized."
        );
    }

    public static void OnCampaignStart()
    {
        lock (Sync)
        {
            _campaignStarted =
                true;
        }

        CampaignSafeStartup
            .Initialize();
    }

    public static void OnCampaignEnd()
    {
        lock (Sync)
        {
            _campaignStarted =
                false;
        }

        FinalCampaignBehaviorBridge
            .Clear();
    }

    public static void OnUnload()
    {
        lock (Sync)
        {
            _loaded =
                false;

            _campaignStarted =
                false;
        }

        FinalCampaignBehaviorBridge
            .Clear();
    }
}









// ============================================================
// FINAL HOST UPDATE
// ============================================================

internal static class FinalHostUpdate
{
    public static void Update(
        float dt)
    {
        MultiplayerCampaignHost host =
            MultiplayerCampaignSubModule
                .GetHost();

        if (host == null)
        {
            return;
        }

        HostPlayerSnapshotService
            .Update(
                dt,
                host
            );

        HostCampaignSnapshotLoop
            .Update(
                dt,
                host
            );

        host.Update();
    }
}









// ============================================================
// FINAL MASTER UPDATE V2
// ============================================================

internal static class FinalMasterUpdateV2
{
    public static void Tick(
        float dt)
    {
        CampaignSafeStartup
            .Initialize();

        CampaignSafeStartup
            .Update();

        if (
            Campaign.Current == null)
        {
            return;
        }

        FinalHostUpdate
            .Update(
                dt
            );

        FinalCampaignBehaviorBridge
            .Tick(
                dt
            );

        CampaignRemotePlayerMarkerRegistry
            .Update();
    }
}









// ============================================================
// FINAL RESET V2
// ============================================================

internal static class FinalResetV2
{
    public static void Execute()
    {
        FinalErrorGuard.Execute(
            () =>
            {
                FinalPlayerSyncService
                    .Reset();

                _ResetInternal();
            }
        );
    }

    private static void _ResetInternal()
    {
        CampaignRemotePlayerMarkerCleanup
            .Clear();

        RemotePlayerWorldViewRegistry
            .Clear();

        RemotePlayerRegistry
            .Clear();

        RemoteSessionRegistry
            .Clear();

        HostPlayerRegistry
            .Clear();

        HostClientStateRegistry
            .Clear();

        HostPlayerSnapshotCache
            .Clear();

        HostWorldStateController
            .Reset();

        WorldTransferService
            .Reset();

        MultiplayerWorldTransfer
            .Clear();

        MultiplayerWorldSyncState
            .Reset();

        WorldSynchronizationController
            .Reset();

        CampaignStateSynchronization
            .Reset();

        MultiplayerCampaignGameState
            .Reset();

        MultiplayerSessionState
            .Reset();

        MultiplayerConnectionStatus
            .Reset();

        NetworkIdentityService
            .Reset();

        HandshakeState
            .Reset();

        MultiplayerSessionId
            .Reset();

        PlayerSnapshotState
            .Reset();

        CampaignSessionCoordinator
            .Reset();

        CampaignInitializationGuard
            .Reset();

        CampaignSafeStartup
            .Reset();
    }
}









// ============================================================
// FINAL SAFE MAP UPDATE
// ============================================================

internal static class FinalSafeMapUpdate
{
    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        /*
         * These operations only touch our own logical
         * remote-player structures.
         *
         * No Hero or MobileParty is created here.
         */

        RemotePlayerMapRefreshV2
            .Update(
                dt
            );

        RemotePlayerTimeoutService
            .Update(
                dt
            );
    }
}









// ============================================================
// FINAL RESET
// ============================================================

internal static class FinalGlobalReset
{
    public static void Execute()
    {
        try
        {
            MultiplayerCampaignFinalService
                .Stop();
        }
        catch
        {
        }

        try
        {
            RemotePlayerFinalCleanup
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerManager
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerMapRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            CampaignRemotePlayerMarkerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerWorldViewRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSessionRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostClientStateRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerSnapshotCache
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerSyncStatistics
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignStateSynchronization
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerSnapshotState
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerSnapshotSendTimer
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerWorldSyncState
                .Reset();
        }
        catch
        {
        }

        try
        {
            WorldSynchronizationController
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerCampaignGameState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerConnectionStatus
                .Reset();
        }
        catch
        {
        }

        try
        {
            NetworkIdentityService
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerIdentity
                .Reset();
        }
        catch
        {
        }

        try
        {
            HandshakeState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionId
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignSessionCoordinator
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignInitializationGuard
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignSafeStartup
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignThreadDispatcher
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerSnapshotLoop
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSnapshotDispatcher
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerCommandQueue
                .Clear();
        }
        catch
        {
        }

        try
        {
            WorldTransferService
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerWorldTransfer
                .Clear();
        }
        catch
        {
        }

        try
        {
            ExistingWorldTransferProvider
                .Clear();
        }
        catch
        {
        }
    }
}









// ============================================================
// FINAL ERROR REPORTER
// ============================================================

internal static class FinalErrorReporter
{
    public static void Report(
        string source,
        Exception exception)
    {
        if (exception == null)
        {
            return;
        }

        string prefix =
            string.IsNullOrWhiteSpace(
                source)
                ? "MultiplayerCampaign"
                : source;

        HostConsole.WriteLine(
            "[!] " +
            prefix +
            ": " +
            exception.Message
        );
    }
}









// ============================================================
// FINAL SAFE EXECUTION
// ============================================================

internal static class FinalSafeExecution
{
    public static void Run(
        string source,
        Action action)
    {
        if (action == null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (NullReferenceException ex)
        {
            FinalErrorReporter
                .Report(
                    source,
                    ex
                );
        }
        catch (InvalidOperationException ex)
        {
            FinalErrorReporter
                .Report(
                    source,
                    ex
                );
        }
        catch (Exception ex)
        {
            FinalErrorReporter
                .Report(
                    source,
                    ex
                );
        }
    }
}









// ============================================================
// FINAL REMOTE PLAYER MAP STATE
// ============================================================

internal static class FinalRemoteMapState
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        FinalRemotePlayerSnapshot>
        Players =
            new Dictionary<
                string,
                FinalRemotePlayerSnapshot>();

    public static void Update()
    {
        FinalRemotePlayerSnapshot[] players =
            FinalRemotePlayerCollection.Get();

        lock (Sync)
        {
            HashSet<string> current =
                new HashSet<string>();

            if (players != null)
            {
                for (
                    int i = 0;
                    i < players.Length;
                    i++)
                {
                    FinalRemotePlayerSnapshot player =
                        players[i];

                    if (player == null)
                    {
                        continue;
                    }

                    if (
                        string.IsNullOrWhiteSpace(
                            player.Id))
                    {
                        continue;
                    }

                    current.Add(
                        player.Id
                    );

                    Players[
                        player.Id] =
                        player;
                }
            }

            List<string> remove =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    FinalRemotePlayerSnapshot>
                pair in Players)
            {
                if (
                    !current.Contains(
                        pair.Key))
                {
                    remove.Add(
                        pair.Key
                    );
                }
            }

            for (
                int i = 0;
                i < remove.Count;
                i++)
            {
                Players.Remove(
                    remove[i]
                );
            }
        }
    }

    public static FinalRemotePlayerSnapshot[]
        Snapshot()
    {
        lock (Sync)
        {
            FinalRemotePlayerSnapshot[] result =
                new FinalRemotePlayerSnapshot[
                    Players.Count
                ];

            int index = 0;

            foreach (
                FinalRemotePlayerSnapshot player
                in Players.Values)
            {
                result[index++] =
                    player;
            }

            return result;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Players.Clear();
        }
    }
}









// ============================================================
// FINAL MOD LIFECYCLE
// ============================================================

internal static class FinalModLifecycle
{
    private static bool _loaded;

    private static bool _campaignActive;

    public static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded =
            true;

        HostConsole.WriteLine(
            "[MultiplayerCampaign] " +
            "Loaded."
        );
    }

    public static void CampaignStart()
    {
        if (!_loaded)
        {
            Load();
        }

        _campaignActive =
            true;

        CampaignSafeStartup
            .Initialize();

        CampaignSessionCoordinator
            .Initialize();

        MultiplayerCampaignGameState
            .SetCampaignReady(
                false
            );
    }

    public static void Tick(
        float dt)
    {
        if (!_campaignActive)
        {
            return;
        }

        if (
            Campaign.Current == null)
        {
            return;
        }

        CampaignReadinessMonitor
            .Update(
                dt
            );

        if (
            Campaign.Current == null)
        {
            return;
        }

        FinalTwoPlayerController
            .Update(
                dt
            );
    }

    public static void CampaignEnd()
    {
        if (!_campaignActive)
        {
            return;
        }

        _campaignActive =
            false;

        FinalTwoPlayerController
            .Stop();

        FinalGlobalReset
            .Execute();
    }

    public static void Unload()
    {
        CampaignEnd();

        _loaded =
            false;

        MultiplayerCleanup
            .Execute();
    }
}









// ============================================================
// FINAL MASTER SERVICE
// ============================================================

public static class FinalMasterService
{
    private static bool _running;

    public static void Start()
    {
        if (_running)
        {
            return;
        }

        _running =
            true;

        FinalModLifecycle
            .Load();

        FinalStartupFactory
            .InitializeCampaign();
    }

    public static void Tick(
        float dt)
    {
        if (!_running)
        {
            Start();
        }

        FinalModLifecycle
            .Tick(
                dt
            );

        FinalNetworkHealthCheck
            .Update(
                dt
            );
    }

    public static void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running =
            false;

        FinalNetworkHealthCheck
            .Reset();

        FinalModLifecycle
            .CampaignEnd();

        FinalGlobalReset
            .Execute();
    }
}









// ============================================================
// FINAL CLEANUP
// ============================================================

internal static class UltimateCleanup
{
    public static void Execute()
    {
        try
        {
            FinalMasterService
                .Stop();
        }
        catch
        {
        }

        try
        {
            MultiplayerNetworkClient
                .Instance
                .Disconnect();
        }
        catch
        {
        }

        try
        {
            MultiplayerCampaignSubModule
                .StopHost();
        }
        catch
        {
        }

        try
        {
            FinalPlayerSyncService
                .Reset();
        }
        catch
        {
        }

        try
        {
            FinalTwoPlayerController
                .Stop();
        }
        catch
        {
        }

        try
        {
            FinalPlayerSyncCoordinator
                .Reset();
        }
        catch
        {
        }

        try
        {
            FinalRemoteMapState
                .Clear();
        }
        catch
        {
        }

        try
        {
            CampaignRemotePlayerMarkerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerWorldViewRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerMapRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerManager
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSessionRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostClientStateRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerSnapshotCache
                .Clear();
        }
        catch
        {
        }

        try
        {
            WorldTransferService
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerWorldTransfer
                .Clear();
        }
        catch
        {
        }

        try
        {
            MultiplayerWorldSyncState
                .Reset();
        }
        catch
        {
        }

        try
        {
            WorldSynchronizationController
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerCampaignGameState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerConnectionStatus
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignStateSynchronization
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerSnapshotState
                .Reset();
        }
        catch
        {
        }

        try
        {
            NetworkIdentityService
                .Reset();
        }
        catch
        {
        }

        try
        {
            PlayerIdentity
                .Reset();
        }
        catch
        {
        }

        try
        {
            HandshakeState
                .Reset();
        }
        catch
        {
        }

        try
        {
            MultiplayerSessionId
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignSessionCoordinator
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignInitializationGuard
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignSafeStartup
                .Reset();
        }
        catch
        {
        }

        try
        {
            CampaignThreadDispatcher
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSnapshotDispatcher
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerSnapshotLoop
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerCommandQueue
                .Clear();
        }
        catch
        {
        }

        try
        {
            MultiplayerUIStateManager
                .Reset();
        }
        catch
        {
        }

        MultiplayerConnectionStatus
            .Reset();
    }
}









// ============================================================
// FINAL HOST STATE EXPORT
// ============================================================

internal static class HostStateExporter
{
    public static byte[] CreatePlayerPacket()
    {
        if (
            Campaign.Current == null ||
            MobileParty.MainParty == null)
        {
            return null;
        }

        CampaignVec2 position;

        try
        {
            position =
                MobileParty
                    .MainParty
                    .Position;
        }
        catch
        {
            return null;
        }

        int partySize =
            CampaignWorld
                .GetMainPartySize();

        return
            PlayerSnapshotCodec
                .Encode(
                    LocalPlayerState
                        .GetNetworkId(),
                    LocalPlayerState
                        .GetDisplayName(),
                    position,
                    partySize
                );
    }

    public static CampaignPlayerSnapshot
        CreateSnapshot()
    {
        CampaignPlayerSnapshot result =
            new CampaignPlayerSnapshot();

        result.PlayerId =
            LocalPlayerState
                .GetNetworkId();

        result.PlayerName =
            LocalPlayerState
                .GetDisplayName();

        result.Connected =
            true;

        result.Ready =
            true;

        result.PartySize =
            CampaignWorld
                .GetMainPartySize();

        CampaignVec2 position;

        if (
            CampaignWorld
                .TryGetMainPartyPosition(
                    out position))
        {
            result.Position =
                position;
        }

        return result;
    }
}









// ============================================================
// FINAL CLIENT STATE IMPORT
// ============================================================

internal static class ClientStateImporter
{
    public static void Import(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        NetworkPlayerSnapshot snapshot;

        if (
            !PlayerSnapshotCodec
                .Decode(
                    payload,
                    out snapshot))
        {
            return;
        }

        if (snapshot == null)
        {
            return;
        }

        if (
            snapshot.PlayerId ==
            LocalPlayerState
                .GetNetworkId())
        {
            return;
        }

        RemotePlayerSyncApplierV2
            .Apply(
                snapshot
            );

        RemotePlayerSnapshotLoop
            .Process();

        RemotePlayerManager
            .Update(
                0.10f
            );

        CampaignMapIntegration
            .Update(
                0.10f
            );
    }
}









// ============================================================
// FINAL REMOTE PLAYER VALIDATOR
// ============================================================

internal static class FinalRemoteValidator
{
    public static bool Validate(
        string id,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return false;
        }

        if (
            id ==
            LocalPlayerState
                .GetNetworkId())
        {
            return false;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    position.X,
                    position.Y))
        {
            return false;
        }

        if (
            partySize < 1)
        {
            return false;
        }

        if (
            partySize > 10000)
        {
            return false;
        }

        return true;
    }
}









// ============================================================
// FINAL SUBMODULE CALLBACK
// ============================================================

internal static class FinalSubmoduleCallback
{
    public static void OnLoad()
    {
        FinalSubModuleState
            .OnLoad();

        CampaignSafeStartup
            .Initialize();
    }

    public static void OnCampaignStart()
    {
        CampaignSafeStartup
            .Initialize();

        CampaignSessionCoordinator
            .Initialize();

        MultiplayerCampaignGameState
            .SetCampaignReady(
                Campaign.Current != null &&
                Hero.MainHero != null &&
                MobileParty.MainParty != null
            );
    }

    public static void OnTick(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        FinalSessionTick
            .Update(
                dt
            );
    }

    public static void OnCampaignEnd()
    {
        FinalSessionTick
            .Reset();

        FinalGlobalReset
            .Execute();
    }

    public static void OnUnload()
    {
        FinalSessionTick
            .Reset();

        FinalGlobalReset
            .Execute();

        FinalSubModuleState
            .OnUnload();
    }
}









// ============================================================
// FINAL SAFETY CHECK
// ============================================================

internal static class FinalSafetyCheck
{
    public static bool IsSafe()
    {
        try
        {
            return
                Campaign.Current != null &&
                Hero.MainHero != null &&
                MobileParty.MainParty != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRemoteSafe(
        RemotePlayerState state)
    {
        if (state == null)
        {
            return false;
        }

        if (!state.Active)
        {
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(
                state.PlayerId))
        {
            return false;
        }

        if (
            state.PlayerId ==
            LocalPlayerState
                .GetNetworkId())
        {
            return false;
        }

        return
            NetworkUtilities
                .IsValidPosition(
                    state.CurrentPosition.X,
                    state.CurrentPosition.Y);
    }
}









// ============================================================
// FINAL SHUTDOWN
// ============================================================

internal static class FinalShutdown
{
    public static void Execute()
    {
        try
        {
            MultiplayerNetworkClient
                .Instance
                .Disconnect();
        }
        catch
        {
        }

        try
        {
            MultiplayerCampaignSubModule
                .StopHost();
        }
        catch
        {
        }

        try
        {
            FinalSessionTick
                .Reset();
        }
        catch
        {
        }

        try
        {
            RemotePlayerFinalCleanup
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostPlayerRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            HostClientStateRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemoteSessionRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            CampaignRemotePlayerMarkerRegistry
                .Clear();
        }
        catch
        {
        }

        MultiplayerConnectionStatus
            .Reset();

        MultiplayerSessionState
            .Reset();

        MultiplayerCampaignGameState
            .Reset();

        CampaignStateSynchronization
            .Reset();

        NetworkIdentityService
            .Reset();

        HandshakeState
            .Reset();

        MultiplayerSessionId
            .Reset();

        PlayerSnapshotState
            .Reset();
    }
}









/*
 * ============================================================
 * FINAL CLEANUP
 * ============================================================
 */

internal static class MultiplayerCleanup
{
    public static void Execute()
    {
        try
        {
            RemotePlayerVisualRegistry
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerManager
                .Clear();
        }
        catch
        {
        }

        try
        {
            WorldPartySynchronizer
                .Clear();
        }
        catch
        {
        }

        try
        {
            MultiplayerWorldTransfer
                .Clear();
        }
        catch
        {
        }

        try
        {
            ExistingWorldTransferProvider
                .Clear();
        }
        catch
        {
        }

        MultiplayerSessionState
            .Reset();
    }
}









// ============================================================
// CLIENT SNAPSHOT RECEIVE
// ============================================================

internal static class RemoteSnapshotProcessor
{
    public static void Process(
        byte[] payload)
    {
        NetworkPlayerSnapshot snapshot;

        if (
            !PlayerSnapshotCodec.Decode(
                payload,
                out snapshot))
        {
            return;
        }

        if (
            snapshot.PlayerId ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        RemotePlayerBridge.Queue(
            snapshot
        );
    }
}









// ============================================================
// REMOTE SNAPSHOT DECODER
// ============================================================

internal static class RemoteSnapshotDecoder
{
    public static bool TryDecode(
        byte[] payload,
        out NetworkPlayerSnapshot snapshot)
    {
        snapshot =
            null;

        if (
            payload == null ||
            payload.Length == 0 ||
            payload.Length > 1024)
        {
            return false;
        }

        try
        {
            using (
                MemoryStream stream =
                    new MemoryStream(
                        payload))
            using (
                BinaryReader reader =
                    new BinaryReader(
                        stream,
                        Encoding.UTF8,
                        true))
            {
                string playerId =
                    reader.ReadString();

                string playerName =
                    reader.ReadString();

                if (
                    stream.Length -
                    stream.Position <
                    sizeof(float) * 2 +
                    sizeof(int))
                {
                    return false;
                }

                float x =
                    reader.ReadSingle();

                float y =
                    reader.ReadSingle();

                int partySize =
                    reader.ReadInt32();

                if (
                    !NetworkStateValidator
                        .IsValidPlayerId(
                            playerId))
                {
                    return false;
                }

                if (
                    playerId ==
                    LocalPlayerState
                        .GetNetworkId())
                {
                    return false;
                }

                if (
                    !NetworkUtilities
                        .IsValidPosition(
                            x,
                            y))
                {
                    return false;
                }

                if (
                    !NetworkStateValidator
                        .IsValidPartySize(
                            partySize))
                {
                    partySize =
                        1;
                }

                snapshot =
                    new NetworkPlayerSnapshot
                    {
                        PlayerId =
                            playerId,

                        PlayerName =
                            NetworkUtilities
                                .SafeName(
                                    playerName
                                ),

                        X =
                            x,

                        Y =
                            y,

                        PartySize =
                            partySize,

                        Timestamp =
                            DateTime.UtcNow
                                .Ticks
                    };

                return true;
            }
        }
        catch
        {
            return false;
        }
    }
}









// ============================================================
// HOST PLAYER STATE
// ============================================================

public sealed class HostPlayerState
{
    public string PlayerId;

    public string Name;

    public float X;

    public float Y;

    public int PartySize;

    public bool Ready;

    public bool Connected;

    public DateTime LastUpdateUtc;

    public long Sequence;

    public HostPlayerState()
    {
        PlayerId =
            "";

        Name =
            "Player";

        X =
            0f;

        Y =
            0f;

        PartySize =
            1;

        Ready =
            false;

        Connected =
            false;

        LastUpdateUtc =
            DateTime.UtcNow;

        Sequence =
            0;
    }

    public CampaignVec2 Position
    {
        get
        {
            return
                new CampaignVec2(
                    new Vec2(
                        X,
                        Y
                    ),
                    true
                );
        }

        set
        {
            X =
                value.X;

            Y =
                value.Y;
        }
    }
}









// ============================================================
// HOST PLAYER REGISTRY
// ============================================================

public static class HostPlayerRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        HostPlayerState>
        Players =
            new Dictionary<
                string,
                HostPlayerState>();

    public static HostPlayerState GetOrCreate(
        string id)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return null;
        }

        lock (Sync)
        {
            HostPlayerState state;

            if (
                !Players.TryGetValue(
                    id,
                    out state))
            {
                state =
                    new HostPlayerState
                    {
                        PlayerId =
                            id,

                        Connected =
                            true
                    };

                Players.Add(
                    id,
                    state
                );
            }

            return state;
        }
    }

    public static void Update(
        string id,
        string name,
        float x,
        float y,
        int partySize)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    x,
                    y))
        {
            return;
        }

        HostPlayerState state =
            GetOrCreate(
                id
            );

        if (state == null)
        {
            return;
        }

        lock (Sync)
        {
            state.Name =
                NetworkUtilities.SafeName(
                    name
                );

            state.X =
                x;

            state.Y =
                y;

            state.PartySize =
                NetworkUtilities.SafePartySize(
                    partySize
                );

            state.Connected =
                true;

            state.LastUpdateUtc =
                DateTime.UtcNow;

            state.Sequence++;
        }
    }

    public static void SetReady(
        string id,
        bool ready)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        lock (Sync)
        {
            HostPlayerState state;

            if (
                Players.TryGetValue(
                    id,
                    out state))
            {
                state.Ready =
                    ready;
            }
        }
    }

    public static void Remove(
        string id)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        lock (Sync)
        {
            Players.Remove(
                id
            );
        }
    }

    public static HostPlayerState[]
        Snapshot()
    {
        lock (Sync)
        {
            HostPlayerState[] result =
                new HostPlayerState[
                    Players.Count
                ];

            int index = 0;

            foreach (
                HostPlayerState state
                in Players.Values)
            {
                result[index++] =
                    state;
            }

            return result;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Players.Clear();
        }
    }
}









// ============================================================
// HOST PLAYER SNAPSHOT SERVICE
// ============================================================

internal static class HostPlayerSnapshotService
{
    private static float _timer;

    public static void Update(
        float dt,
        MultiplayerCampaignHost host)
    {
        if (host == null)
        {
            return;
        }

        if (
            Campaign.Current == null ||
            MobileParty.MainParty == null)
        {
            return;
        }

        _timer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_timer < 0.10f)
        {
            return;
        }

        _timer =
            0f;

        HostPlayerState[] players =
            HostPlayerRegistry
                .Snapshot();

        if (
            players == null ||
            players.Length == 0)
        {
            return;
        }

        for (
            int i = 0;
            i < players.Length;
            i++)
        {
            HostPlayerState player =
                players[i];

            if (
                player == null ||
                !player.Connected)
            {
                continue;
            }

            host.BroadcastHostSnapshot(
                player.PlayerId
            );
        }
    }
}









// ============================================================
// HOST PLAYER SNAPSHOT CACHE
// ============================================================

internal static class HostPlayerSnapshotCache
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        NetworkPlayerSnapshot>
        Snapshots =
            new Dictionary<
                string,
                NetworkPlayerSnapshot>();

    public static void Set(
        NetworkPlayerSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(
                snapshot.PlayerId))
        {
            return;
        }

        lock (Sync)
        {
            Snapshots[
                snapshot.PlayerId] =
                snapshot;
        }
    }

    public static bool TryGet(
        string playerId,
        out NetworkPlayerSnapshot snapshot)
    {
        snapshot =
            null;

        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return false;
        }

        lock (Sync)
        {
            return Snapshots.TryGetValue(
                playerId,
                out snapshot
            );
        }
    }

    public static NetworkPlayerSnapshot[]
        Snapshot()
    {
        lock (Sync)
        {
            NetworkPlayerSnapshot[] result =
                new NetworkPlayerSnapshot[
                    Snapshots.Count
                ];

            int index = 0;

            foreach (
                NetworkPlayerSnapshot snapshot
                in Snapshots.Values)
            {
                result[index++] =
                    snapshot;
            }

            return result;
        }
    }

    public static void Remove(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        lock (Sync)
        {
            Snapshots.Remove(
                playerId
            );
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Snapshots.Clear();
        }
    }
}









// ============================================================
// HOST PLAYER SNAPSHOT PROCESSOR
// ============================================================

internal static class HostPlayerSnapshotProcessor
{
    public static bool Process(
        HostClientConnection sender,
        byte[] payload)
    {
        if (sender == null)
        {
            return false;
        }

        if (
            !PlayerSnapshotCodec.Decode(
                payload,
                out NetworkPlayerSnapshot snapshot))
        {
            return false;
        }

        /*
         * Never trust the sender-provided ID.
         */

        snapshot.PlayerId =
            sender.PlayerId;

        snapshot.PlayerName =
            NetworkUtilities.SafeName(
                sender.PlayerName
            );

        sender.LastX =
            snapshot.X;

        sender.LastY =
            snapshot.Y;

        sender.LastPartySize =
            snapshot.PartySize;

        HostPlayerSnapshotCache
            .Set(
                snapshot
            );

        HostPlayerRegistry
            .Update(
                snapshot.PlayerId,
                snapshot.PlayerName,
                snapshot.X,
                snapshot.Y,
                snapshot.PartySize
            );

        return true;
    }
}









// ============================================================
// CLIENT REMOTE SNAPSHOT PROCESSOR
// ============================================================

internal static class ClientRemoteSnapshotProcessor
{
    public static void Process(
        byte[] payload)
    {
        NetworkPlayerSnapshot snapshot;

        if (
            !PlayerSnapshotCodec
                .Decode(
                    payload,
                    out snapshot))
        {
            return;
        }

        if (
            snapshot.PlayerId ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        if (
            snapshot.PlayerId ==
            NetworkIdentityService
                .GetCurrentId())
        {
            return;
        }

        RemoteSnapshotDispatcher
            .Enqueue(
                snapshot
            );

        RemotePlayerBridge
            .Queue(
                snapshot
            );

        RemoteSessionProcessor
            .EnqueueState(
                snapshot.PlayerId,
                snapshot.PlayerName,
                snapshot.GetPosition(),
                snapshot.PartySize
            );
    }
}









// ============================================================
// PLAYER JOIN SERVICE
// ============================================================

internal static class PlayerJoinService
{
    public static void CreateLogicalPlayer(
        string id,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        if (
            id ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        if (
            !NetworkUtilities.IsValidPosition(
                position.X,
                position.Y))
        {
            return;
        }

        partySize =
            NetworkUtilities.SafePartySize(
                partySize
            );

        RemotePlayerManager
            .ReceiveSessionInternal(
                id,
                NetworkUtilities.SafeName(
                    name
                ),
                position,
                partySize
            );
    }

    public static void RemoveLogicalPlayer(
        string id)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        RemotePlayerManager
            .RemoveSessionInternal(
                id
            );
    }
}









// ============================================================
// PLAYER JOIN VALIDATOR
// ============================================================

internal static class PlayerJoinValidator
{
    public static bool Validate(
        string id,
        string name,
        float x,
        float y,
        int partySize)
    {
        if (
            !NetworkUtilities
                .IsValidPosition(
                    x,
                    y))
        {
            return false;
        }

        if (
            !NetworkStateValidator
                .IsValidPlayerId(
                    id))
        {
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(
                name))
        {
            return false;
        }

        if (
            name.Length > 32)
        {
            return false;
        }

        return
            NetworkStateValidator
                .IsValidPartySize(
                    partySize
                );
    }
}









// ============================================================
// CLIENT PLAYER STATE SENDER
// ============================================================

internal static class ClientPlayerStateSender
{
    private static float _timer;

    private static long _sequence;

    public static void Update(
        float dt)
    {
        MultiplayerNetworkClient client =
            MultiplayerNetworkClient
                .Instance;

        if (client == null)
        {
            return;
        }

        if (!client.IsConnected)
        {
            return;
        }

        if (!client.IsWorldLoaded)
        {
            return;
        }

        if (
            Campaign.Current == null ||
            MobileParty.MainParty == null)
        {
            return;
        }

        _timer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_timer < 0.10f)
        {
            return;
        }

        _timer =
            0f;

        CampaignVec2 position;

        if (
            !CampaignWorld
                .TryGetMainPartyPosition(
                    out position))
        {
            return;
        }

        int partySize =
            CampaignWorld
                .GetMainPartySize();

        CampaignPlayerSnapshot snapshot =
            new CampaignPlayerSnapshot();

        snapshot.PlayerId =
            NetworkIdentityService
                .GetCurrentId();

        if (
            string.IsNullOrWhiteSpace(
                snapshot.PlayerId))
        {
            snapshot.PlayerId =
                LocalPlayerState
                    .GetNetworkId();
        }

        snapshot.PlayerName =
            LocalPlayerState
                .GetDisplayName();

        snapshot.Position =
            position;

        snapshot.PartySize =
            partySize;

        snapshot.Connected =
            true;

        snapshot.Ready =
            true;

        snapshot.Sequence =
            ++_sequence;

        snapshot.TimestampUtcTicks =
            DateTime.UtcNow.Ticks;

        byte[] payload =
            CampaignPlayerSnapshotCodec
                .Encode(
                    snapshot
                );

        if (payload == null)
        {
            return;
        }

        client.Send(
            NetworkPacketType.PlayerSnapshot,
            payload
        );
    }

    public static void Reset()
    {
        _timer =
            0f;

        _sequence =
            0;
    }
}









// ============================================================
// FINAL PLAYER SYNC SERVICE
// ============================================================

public static class FinalPlayerSyncService
{
    public static void Update(
        float dt)
    {
        FullMultiplayerTick
            .Update(
                dt
            );
    }

    public static void Reset()
    {
        ClientPlayerStateSender
            .Reset();

        HostCampaignSnapshotLoop
            .Reset();

        ClientCampaignSnapshotReceiver
            .Clear();

        CampaignRemotePlayerMarkerCleanup
            .Clear();

        CampaignStateSynchronization
            .Reset();

        PlayerSnapshotState
            .Reset();
    }
}









// ============================================================
// END OF SECTION
// ============================================================
// ============================================================
// FINAL COMPATIBILITY LAYER
// ============================================================

internal static class MultiplayerCompatibilityLayer
{
    public static bool IsCampaignAvailable()
    {
        try
        {
            return Campaign.Current != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsLocalPlayerAvailable()
    {
        try
        {
            return
                Campaign.Current != null &&
                Hero.MainHero != null &&
                MobileParty.MainParty != null;
        }
        catch
        {
            return false;
        }
    }

    public static CampaignVec2 GetSafePosition()
    {
        try
        {
            if (
                MobileParty.MainParty != null)
            {
                CampaignVec2 position =
                    MobileParty.MainParty.Position;

                if (
                    NetworkUtilities.IsValidPosition(
                        position.X,
                        position.Y))
                {
                    return position;
                }
            }
        }
        catch
        {
        }

        return
            new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );
    }

    public static int GetSafePartySize()
    {
        try
        {
            if (
                MobileParty.MainParty != null &&
                MobileParty.MainParty.MemberRoster != null)
            {
                return
                    NetworkUtilities.SafePartySize(
                        MobileParty.MainParty
                            .MemberRoster
                            .TotalManCount
                    );
            }
        }
        catch
        {
        }

        return 1;
    }
}









// ============================================================
// FINAL REMOTE SNAPSHOT FACTORY
// ============================================================

internal static class FinalRemoteSnapshotFactory
{
    public static FinalRemotePlayerSnapshot Create(
        RemotePlayerState state)
    {
        if (state == null)
        {
            return null;
        }

        if (
            string.IsNullOrWhiteSpace(
                state.PlayerId))
        {
            return null;
        }

        if (
            !NetworkUtilities.IsValidPosition(
                state.CurrentPosition.X,
                state.CurrentPosition.Y))
        {
            return null;
        }

        return
            new FinalRemotePlayerSnapshot
            {
                Id =
                    state.PlayerId,

                Name =
                    NetworkUtilities.SafeName(
                        state.Name
                    ),

                X =
                    state.CurrentPosition.X,

                Y =
                    state.CurrentPosition.Y,

                PartySize =
                    NetworkUtilities.SafePartySize(
                        state.PartySize
                    ),

                Connected =
                    state.Active,

                LastUpdateUtc =
                    state.LastPacketUtc
            };
    }
}









// ============================================================
// FINAL PLAYER SYNC COORDINATOR
// ============================================================

internal static class FinalPlayerSyncCoordinator
{
    private static float _sendTimer;

    private static float _receiveTimer;

    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        _receiveTimer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_receiveTimer >= 0.05f)
        {
            _receiveTimer =
                0f;

            ClientCampaignSnapshotReceiver
                .Process();

            RemotePlayerCommandProcessor
                .Process(
                    dt
                );

            RemotePlayerManager
                .Update(
                    dt
                );

            FinalRemoteMapState
                .Update();
        }

        if (
            MultiplayerNetworkClient
                .Instance
                .IsConnected &&
            MultiplayerNetworkClient
                .Instance
                .IsWorldLoaded &&
            MobileParty.MainParty != null)
        {
            _sendTimer +=
                Math.Max(
                    0f,
                    Math.Min(
                        1f,
                        dt
                    )
                );

            if (_sendTimer >= 0.10f)
            {
                _sendTimer =
                    0f;

                CampaignVec2 position =
                    MultiplayerCompatibilityLayer
                        .GetSafePosition();

                int partySize =
                    MultiplayerCompatibilityLayer
                        .GetSafePartySize();

                MultiplayerNetworkClient
                    .Instance
                    .SendLocalPlayerState(
                        position,
                        partySize
                    );
            }
        }
    }

    public static void Reset()
    {
        _sendTimer =
            0f;

        _receiveTimer =
            0f;

        FinalRemoteMapState
            .Clear();
    }
}









// ============================================================
// FINAL TWO PLAYER CONTROLLER
// ============================================================

public static class FinalTwoPlayerController
{
    private static bool _active;

    public static void Start()
    {
        if (_active)
        {
            return;
        }

        _active =
            true;

        MultiplayerSessionState
            .StartClient();

        MultiplayerCampaignGameState
            .SetNetworkReady(
                true
            );

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .Connected
            );
    }

    public static void Update(
        float dt)
    {
        if (!_active)
        {
            return;
        }

        if (
            !MultiplayerCompatibilityLayer
                .IsCampaignAvailable())
        {
            return;
        }

        FinalPlayerSyncCoordinator
            .Update(
                dt
            );
    }

    public static void Stop()
    {
        _active =
            false;

        FinalPlayerSyncCoordinator
            .Reset();

        MultiplayerSessionState
            .Reset();
    }
}









// ============================================================
// HOST SNAPSHOT BUILDER
// ============================================================

internal static class HostSnapshotBuilder
{
    public static byte[] Build(
        string id,
        string name,
        float x,
        float y,
        int partySize)
    {
        if (
            !NetworkUtilities
                .IsValidPosition(
                    x,
                    y))
        {
            return null;
        }

        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        id ??
                        ""
                    );

                    writer.Write(
                        NetworkUtilities
                            .SafeName(
                                name
                            )
                    );

                    writer.Write(
                        x
                    );

                    writer.Write(
                        y
                    );

                    writer.Write(
                        NetworkUtilities
                            .SafePartySize(
                                partySize
                            )
                    );
                }
            );
    }
}









// ============================================================
// FINAL SAFE WORLD CHECK
// ============================================================

internal static class FinalWorldCheck
{
    public static bool IsReady()
    {
        if (
            Campaign.Current == null)
        {
            return false;
        }

        if (
            Hero.MainHero == null)
        {
            return false;
        }

        if (
            MobileParty.MainParty == null)
        {
            return false;
        }

        return true;
    }
}


namespace MultiplayerCampaign
{

    internal static class MpcFinalOwnershipGuardV2
    {
        public static bool IsSafeClientParty(TaleWorlds.CampaignSystem.Party.MobileParty party)
        {
            try
            {
                if (party == null) return false;
                if (MultiplayerSessionState.IsClient && party == TaleWorlds.CampaignSystem.Party.MobileParty.MainParty) return false;
                return true;
            } catch { return false; }
        }
    }








    internal static class MpcFinalRuntimeStatusV2
    {
        public static bool CharacterSelected { get { try { return MpcFinalCharacterSystemV2.HasSelection; } catch { return false; } } }
        public static int CharacterSlot { get { try { return MpcFinalCharacterSystemV2.Selected; } catch { return -1; } } }
        public static string CharacterId { get { try { return MpcFinalCharacterSystemV2.GetSelectedCharacterId(); } catch { return null; } } }
        public static string CharacterName { get { try { return MpcFinalCharacterSystemV2.GetSelectedName(); } catch { return "Player"; } } }
    }







    internal sealed class MpcFinalCharacterSlotV2
    {
        public int Slot;
        public string CharacterId;
        public string Name;
        public string CharacterData;
        public long CreatedUtcTicks;
    }








    internal static class MpcFinalCharacterSystemV2
    {
        private const int SlotCount = 3;
        private static readonly object Sync = new object();
        private static readonly MpcFinalCharacterSlotV2[] Slots = new MpcFinalCharacterSlotV2[SlotCount];
        private static bool Loaded;
        private static int SelectedSlot = -1;

        private static string FilePath
        {
            get
            {
                string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(root, "MultiplayerCampaign", "final-characters.dat");
            }
        }

        public static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (Loaded) return;
                Loaded = true;
                LoadLocked();
            }
        }

        public static bool HasSelection
        {
            get { EnsureLoaded(); lock (Sync) return SelectedSlot >= 0 && SelectedSlot < SlotCount; }
        }

        public static int Selected
        {
            get { EnsureLoaded(); lock (Sync) return SelectedSlot; }
        }

        public static bool SelectSlot(int slot)
        {
            EnsureLoaded();
            if (slot < 0 || slot >= SlotCount) return false;
            lock (Sync)
            {
                SelectedSlot = slot;
                SaveLocked();
                return true;
            }
        }

        public static MpcFinalCharacterSlotV2 CreateSlot(int slot, string name, string data)
        {
            EnsureLoaded();
            if (slot < 0 || slot >= SlotCount) return null;
            lock (Sync)
            {
                MpcFinalCharacterSlotV2 record = new MpcFinalCharacterSlotV2
                {
                    Slot = slot,
                    CharacterId = System.Guid.NewGuid().ToString("N"),
                    Name = SafeName(name),
                    CharacterData = data ?? "",
                    CreatedUtcTicks = System.DateTime.UtcNow.Ticks
                };
                Slots[slot] = record;
                SelectedSlot = slot;
                SaveLocked();
                return record;
            }
        }

        public static MpcFinalCharacterSlotV2 GetSlot(int slot)
        {
            EnsureLoaded();
            if (slot < 0 || slot >= SlotCount) return null;
            lock (Sync) return Slots[slot];
        }

        public static bool IsEmpty(int slot) { return GetSlot(slot) == null; }
        public static string GetSelectedCharacterId()
        {
            EnsureLoaded();
            lock (Sync)
            {
                return SelectedSlot >= 0 && SelectedSlot < SlotCount && Slots[SelectedSlot] != null ? Slots[SelectedSlot].CharacterId : null;
            }
        }

        public static string GetSelectedName()
        {
            EnsureLoaded();
            lock (Sync)
            {
                return SelectedSlot >= 0 && SelectedSlot < SlotCount && Slots[SelectedSlot] != null ? Slots[SelectedSlot].Name : "Player";
            }
        }

        public static void ResetSelection()
        {
            EnsureLoaded();
            lock (Sync) { SelectedSlot = -1; SaveLocked(); }
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Player";
            name = name.Trim();
            return name.Length > 32 ? name.Substring(0, 32) : name;
        }

        private static void SaveLocked()
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(FilePath);
                if (!System.IO.Directory.Exists(directory)) System.IO.Directory.CreateDirectory(directory);
                using (var stream = new System.IO.FileStream(FilePath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(1); writer.Write(SelectedSlot);
                    for (int i = 0; i < SlotCount; i++)
                    {
                        var s = Slots[i]; writer.Write(s != null); if (s == null) continue;
                        writer.Write(s.Slot); writer.Write(s.CharacterId ?? ""); writer.Write(s.Name ?? "Player"); writer.Write(s.CharacterData ?? ""); writer.Write(s.CreatedUtcTicks);
                    }
                }
            } catch { }
        }

        private static void LoadLocked()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return;
                using (var stream = new System.IO.FileStream(FilePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                using (var reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, true))
                {
                    if (reader.ReadInt32() != 1) return;
                    SelectedSlot = reader.ReadInt32();
                    for (int i = 0; i < SlotCount; i++)
                    {
                        if (!reader.ReadBoolean()) { Slots[i] = null; continue; }
                        Slots[i] = new MpcFinalCharacterSlotV2
                        { Slot = reader.ReadInt32(), CharacterId = reader.ReadString(), Name = reader.ReadString(), CharacterData = reader.ReadString(), CreatedUtcTicks = reader.ReadInt64() };
                    }
                }
            } catch { SelectedSlot = -1; for (int i = 0; i < SlotCount; i++) Slots[i] = null; }
        }
    }

}

namespace MultiplayerCampaignRebuildLayer
{

    internal static class MpcCodec
    {
        private const string Magic = "MPC2";
        private const int MaxPayload = 512 * 1024;

        public const byte Player = 1;
        public const byte WorldParties = 2;
        public const byte Time = 3;
        public const byte Leave = 4;
        public const byte Resync = 5;

        public static byte[] EncodePlayer(PlayerSyncState state)
        {
            return EncodeCommon(Player, state, null);
        }

        public static byte[] EncodeTime(
            double timeMs,
            float campaignDt,
            int speed,
            int mode,
            long revision)
        {
            using (MemoryStream stream = new MemoryStream())
            using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Time);
                writer.Write(revision);
                writer.Write(timeMs);
                writer.Write(campaignDt);
                writer.Write(speed);
                writer.Write(mode);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static byte[] EncodeLeave(string id, long revision)
        {
            using (MemoryStream stream = new MemoryStream())
            using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Leave);
                writer.Write(revision);
                writer.Write(id ?? "");
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static byte[] EncodeResync(long revision)
        {
            using (MemoryStream stream = new MemoryStream())
            using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Resync);
                writer.Write(revision);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] EncodeCommon(
            byte kind,
            PlayerSyncState state,
            PlayerSyncState[] parties)
        {
            using (MemoryStream stream = new MemoryStream())
            using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(kind);
                writer.Write(state != null ? state.Sequence : 0L);
                writer.Write(state != null ? state.Revision : 0L);
                writer.Write(state != null ? state.Id ?? "" : "");
                writer.Write(state != null ? state.Name ?? "Player" : "Player");
                writer.Write(state != null ? state.X : 0f);
                writer.Write(state != null ? state.Y : 0f);
                writer.Write(state != null ? state.TargetX : 0f);
                writer.Write(state != null ? state.TargetY : 0f);
                writer.Write(state != null ? state.BearingX : 0f);
                writer.Write(state != null ? state.BearingY : 0f);
                writer.Write(state != null ? state.PartySize : 1);
                writer.Write(state != null && state.Moving);
                writer.Write(state != null && state.Active);
                writer.Write(state != null ? state.ServerTimeMs : 0d);
                writer.Write(state != null ? state.TimeSpeed : 1);
                writer.Write(state != null ? state.TimeMode : 0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryDecode(
            byte[] payload,
            out byte kind,
            out PlayerSyncState state,
            out double timeMs,
            out float campaignDt,
            out int speed,
            out int mode,
            out string removedId,
            out long revision)
        {
            kind = 0;
            state = null;
            timeMs = 0d;
            campaignDt = 0f;
            speed = 1;
            mode = 0;
            removedId = "";
            revision = 0;

            if (payload == null || payload.Length == 0 || payload.Length > MaxPayload)
                return false;

            try
            {
                using (MemoryStream stream = new MemoryStream(payload))
                using (System.IO.BinaryReader reader = new System.IO.BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadString() != Magic)
                        return false;

                    kind = reader.ReadByte();
                    revision = reader.ReadInt64();

                    if (kind == Player)
                    {
                        state = new PlayerSyncState();
                        state.Sequence = revision;
                        state.Revision = reader.ReadInt64();
                        state.Id = reader.ReadString();
                        state.Name = reader.ReadString();
                        state.X = reader.ReadSingle();
                        state.Y = reader.ReadSingle();
                        state.TargetX = reader.ReadSingle();
                        state.TargetY = reader.ReadSingle();
                        state.BearingX = reader.ReadSingle();
                        state.BearingY = reader.ReadSingle();
                        state.PartySize = reader.ReadInt32();
                        state.Moving = reader.ReadBoolean();
                        state.Active = reader.ReadBoolean();
                        state.ServerTimeMs = reader.ReadDouble();
                        state.TimeSpeed = reader.ReadInt32();
                        state.TimeMode = reader.ReadInt32();
                        state.LastUpdateUtc = DateTime.UtcNow;
                        return IsValidPlayer(state);
                    }

                    if (kind == Time)
                    {
                        timeMs = reader.ReadDouble();
                        campaignDt = reader.ReadSingle();
                        speed = reader.ReadInt32();
                        mode = reader.ReadInt32();
                        return true;
                    }

                    if (kind == Leave)
                    {
                        removedId = reader.ReadString();
                        return !string.IsNullOrWhiteSpace(removedId);
                    }

                    return kind == Resync;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidPlayer(PlayerSyncState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Id))
                return false;

            if (state.Id.Length > 128 || (state.Name ?? "").Length > 64)
                return false;

            if (float.IsNaN(state.X) || float.IsInfinity(state.X) ||
                float.IsNaN(state.Y) || float.IsInfinity(state.Y) ||
                float.IsNaN(state.TargetX) || float.IsInfinity(state.TargetX) ||
                float.IsNaN(state.TargetY) || float.IsInfinity(state.TargetY))
                return false;

            if (state.PartySize < 1 || state.PartySize > 10000)
                return false;

            return true;
        }
    }








    internal sealed class PlayerSyncState
    {
        public string Id;
        public string Name;
        public float X;
        public float Y;
        public float TargetX;
        public float TargetY;
        public float BearingX;
        public float BearingY;
        public int PartySize;
        public bool Moving;
        public bool Active;
        public long Sequence;
        public long Revision;
        public double ServerTimeMs;
        public int TimeSpeed;
        public int TimeMode;
        public DateTime LastUpdateUtc;
    }

}

