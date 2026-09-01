// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;
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
 * SAFE CAMPAIGN ACCESS
 * ============================================================
 */

public static class SafeCampaignAccess
{
    public static bool IsReady()
    {
        return
            Campaign.Current != null &&
            MobileParty.MainParty != null;
    }

    public static bool TryGetPosition(
        out CampaignVec2 position)
    {
        position =
            new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );

        try
        {
            if (
                Campaign.Current == null ||
                MobileParty.MainParty == null)
            {
                return false;
            }

            position =
                MobileParty.MainParty.Position;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int GetPartySize()
    {
        try
        {
            if (
                MobileParty.MainParty == null ||
                MobileParty.MainParty.MemberRoster == null)
            {
                return 1;
            }

            return Math.Max(
                1,
                MobileParty.MainParty
                    .MemberRoster
                    .TotalManCount
            );
        }
        catch
        {
            return 1;
        }
    }


    public static int GetMainPartySize()
    {
        return CampaignWorld.GetMainPartySize();
    }
}







// ============================================================
// REMOTE PLAYER POSITION CACHE
// ============================================================

internal static class CampaignMapRemotePositionCache
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        CampaignVec2>
        Positions =
            new Dictionary<
                string,
                CampaignVec2>();

    public static void Update()
    {
        CampaignMapRemotePlayerData[] players =
            CampaignMapRemotePlayerRegistry
                .Snapshot();

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
                    CampaignMapRemotePlayerData player =
                        players[i];

                    if (
                        player == null ||
                        !player.Visible)
                    {
                        continue;
                    }

                    if (
                        float.IsNaN(
                            player.Position.X) ||
                        float.IsInfinity(
                            player.Position.X) ||
                        float.IsNaN(
                            player.Position.Y) ||
                        float.IsInfinity(
                            player.Position.Y))
                    {
                        continue;
                    }

                    current.Add(
                        player.PlayerId
                    );

                    Positions[
                        player.PlayerId] =
                        player.Position;
                }
            }

            List<string> remove =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    CampaignVec2>
                pair in Positions)
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
                Positions.Remove(
                    remove[i]
                );
            }
        }
    }

    public static bool TryGet(
        string playerId,
        out CampaignVec2 position)
    {
        position =
            new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );

        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return false;
        }

        lock (Sync)
        {
            return Positions.TryGetValue(
                playerId,
                out position
            );
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
            Positions.Remove(
                playerId
            );
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Positions.Clear();
        }
    }
}







// ============================================================
// REMOTE PLAYER NAME CACHE
// ============================================================

internal static class CampaignMapRemoteNameCache
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        string>
        Names =
            new Dictionary<
                string,
                string>();

    public static void Update()
    {
        CampaignMapRemotePlayerData[] players =
            CampaignMapRemotePlayerRegistry
                .Snapshot();

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
                    CampaignMapRemotePlayerData player =
                        players[i];

                    if (
                        player == null)
                    {
                        continue;
                    }

                    if (
                        string.IsNullOrWhiteSpace(
                            player.PlayerId))
                    {
                        continue;
                    }

                    current.Add(
                        player.PlayerId
                    );

                    Names[
                        player.PlayerId] =
                        string.IsNullOrWhiteSpace(
                            player.Name)
                            ? "Player"
                            : player.Name;
                }
            }

            List<string> remove =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    string>
                pair in Names)
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
                Names.Remove(
                    remove[i]
                );
            }
        }
    }

    public static string Get(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return "Player";
        }

        lock (Sync)
        {
            string name;

            if (
                Names.TryGetValue(
                    playerId,
                    out name))
            {
                return
                    string.IsNullOrWhiteSpace(
                        name)
                        ? "Player"
                        : name;
            }
        }

        return "Player";
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Names.Clear();
        }
    }
}







// ============================================================
// CAMPAIGN MAP SAFE STATE
// ============================================================

internal static class CampaignMapSafeState
{
    public static bool CanUpdate()
    {
        try
        {
            if (
                Campaign.Current == null)
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
        catch
        {
            return false;
        }
    }

    public static CampaignVec2
        GetLocalPosition()
    {
        if (
            !CanUpdate())
        {
            return
                new CampaignVec2(
                    new Vec2(
                        0f,
                        0f
                    ),
                    true
                );
        }

        try
        {
            return
                MobileParty.MainParty
                    .Position;
        }
        catch
        {
            return
                new CampaignVec2(
                    new Vec2(
                        0f,
                        0f
                    ),
                    true
                );
        }
    }
}







// ============================================================
// CAMPAIGN READINESS MONITOR
// ============================================================

internal static class CampaignReadinessMonitor
{
    private static float _timer;

    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            MultiplayerCampaignGameState
                .SetCampaignReady(false);

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

        if (_timer < 0.25f)
        {
            return;
        }

        _timer =
            0f;

        bool ready =
            false;

        try
        {
            ready =
                Campaign.Current != null &&
                Hero.MainHero != null &&
                MobileParty.MainParty != null;
        }
        catch
        {
            ready =
                false;
        }

        MultiplayerCampaignGameState
            .SetCampaignReady(
                ready
            );
    }
}







// ============================================================
// CAMPAIGN INITIALIZATION GUARD
// ============================================================

internal static class CampaignInitializationGuard
{
    private static bool _behaviorRegistered;

    private static readonly object Sync =
        new object();

    public static bool IsRegistered
    {
        get
        {
            lock (Sync)
            {
                return _behaviorRegistered;
            }
        }
    }

    public static void MarkRegistered()
    {
        lock (Sync)
        {
            _behaviorRegistered =
                true;
        }
    }

    public static bool CanInitializeNetwork()
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

    public static void Reset()
    {
        lock (Sync)
        {
            _behaviorRegistered =
                false;
        }
    }
}







// ============================================================
// SAFE CAMPAIGN TICK
// ============================================================

internal static class SafeCampaignTick
{
    private static float _timer;

    public static void Update(
        float dt)
    {
        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
        {
            return;
        }

        _timer +=
            dt;

        if (_timer < 0.05f)
        {
            return;
        }

        _timer =
            0f;

        if (
            !CampaignInitializationGuard
                .CanInitializeNetwork())
        {
            return;
        }

        CampaignTickDispatcher
            .Tick(
                dt
            );
    }
}







// ============================================================
// CAMPAIGN SAFE STARTUP
// ============================================================

internal static class CampaignSafeStartup
{
    private static bool _started;

    private static readonly object Sync =
        new object();

    public static void Initialize()
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

        MultiplayerCampaignGameState
            .SetCampaignReady(
                false
            );

        CampaignSessionCoordinator
            .Initialize();

        CampaignInitializationGuard
            .MarkRegistered();

        MultiplayerConnectionStatus
            .Reset();

        CampaignStateSynchronization
            .Reset();
    }

    public static void Update()
    {
        bool ready =
            false;

        try
        {
            ready =
                Campaign.Current != null &&
                Hero.MainHero != null &&
                MobileParty.MainParty != null;
        }
        catch
        {
            ready =
                false;
        }

        MultiplayerCampaignGameState
            .SetCampaignReady(
                ready
            );

        CampaignSessionCoordinator
            .Update();
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _started =
                false;
        }
    }
}







// ============================================================
// FINAL CAMPAIGN BEHAVIOR BRIDGE
// ============================================================

internal static class FinalCampaignBehaviorBridge
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

        FinalErrorGuard
            .Execute(
                () =>
                {
                    FullMultiplayerTick
                        .Update(
                            dt
                        );
                }
            );
    }

    public static void Clear()
    {
        FinalErrorGuard
            .Execute(
                () =>
                {
                    FinalPlayerSyncService
                        .Reset();

                    RemotePlayerFinalCleanup
                        .Clear();

                    CampaignSafeStartup
                        .Reset();
                }
            );
    }
}







// ============================================================
// END OF SECTION
// ============================================================
// ============================================================
// FINAL CAMPAIGN MAP INTEGRATION
// ============================================================

internal static class CampaignMapIntegration
{
    private static float _timer;

    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
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

        CampaignRemotePlayerMarkerRegistry
            .Update();

        RemotePlayerMapRegistry
            .Update();

        RemotePlayerWorldViewRegistry
            .Update();
    }

    public static CampaignRemotePlayerMarker[]
        GetPlayers()
    {
        return
            CampaignRemotePlayerMarkerRegistry
                .Snapshot();
    }

    public static void Clear()
    {
        _timer =
            0f;

        CampaignRemotePlayerMarkerRegistry
            .Clear();

        RemotePlayerMapRegistry
            .Clear();

        RemotePlayerWorldViewRegistry
            .Clear();
    }
}







// ============================================================
// FINAL CAMPAIGN LOOP
// ============================================================

internal static class FinalCampaignLoop
{
    private static float _timer;

    public static void Tick(
        float dt)
    {
        if (
            !FinalInitializationGuard
                .Ensure())
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

        /*
         * Run network and remote-player logic only after
         * Bannerlord has a valid Campaign and MainParty.
         */

        if (
            Campaign.Current == null ||
            Hero.MainHero == null ||
            MobileParty.MainParty == null)
        {
            return;
        }

        FinalGlobalTickEntry
            .Tick(
                dt
            );

        if (_timer >= 0.10f)
        {
            _timer =
                0f;

            CampaignRemotePlayerMarkerRegistry
                .Update();

            RemotePlayerWorldViewRegistry
                .Update();
        }
    }

    public static void Reset()
    {
        _timer =
            0f;

        FinalInitializationGuard
            .Reset();
    }
}







// ============================================================
// FINAL MASTER TICK
// ============================================================

internal static class FinalMasterTick
{
    public static void Tick(
        float dt)
    {
        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
        {
            return;
        }

        NetworkUpdateController
            .Update(
                dt
            );
    }

    public static void Reset()
    {
        _ = 0;
    }
}







// ============================================================
// FINAL STARTUP FACTORY
// ============================================================

internal static class FinalStartupFactory
{
    public static void InitializeCampaign()
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        CampaignSafeStartup
            .Initialize();

        CampaignSessionCoordinator
            .Initialize();

        MultiplayerCampaignGameState
            .SetCampaignReady(
                false
            );

        MultiplayerCampaignGameState
            .SetNetworkReady(
                MultiplayerNetworkClient
                    .Instance
                    .IsConnected
            );
    }
}







// ============================================================
// FINAL GLOBAL TICK ENTRY
// ============================================================

internal static class FinalGlobalTickEntry
{
    public static void Tick(
        float dt)
    {
        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
        {
            return;
        }

        try
        {
            FinalSubmoduleCallback
                .OnTick(
                    dt
                );
        }
        catch (Exception ex)
        {
            FinalErrorReporter
                .Report(
                    "GlobalTick",
                    ex
                );
        }
    }
}







// ============================================================
// FINAL INITIALIZATION GUARD
// ============================================================

internal static class FinalInitializationGuard
{
    private static readonly object Sync =
        new object();

    private static bool _initialized;

    public static bool Ensure()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return true;
            }

            if (
                Campaign.Current == null)
            {
                return false;
            }

            _initialized =
                true;
        }

        CampaignSessionCoordinator
            .Initialize();

        CampaignSafeStartup
            .Initialize();

        MultiplayerCampaignGameState
            .SetCampaignReady(
                false
            );

        return true;
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _initialized =
                false;
        }
    }
}







// ============================================================
// MAIN MULTIPLAYER TICK
// ============================================================

internal static class MultiplayerMainTick
{
    private static float _healthTimer;

    public static void Update(
        float dt)
    {
        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
        {
            dt =
                0f;
        }

        MultiplayerNetworkClient
            .Instance
            .Update();

        RemotePlayerTick
            .Update(
                dt
            );

        LocalPlayerNetworkSender
            .Update(
                dt
            );

        _healthTimer +=
            dt;

        if (_healthTimer >= 1f)
        {
            _healthTimer =
                0f;

            RemotePlayerHealthMonitor
                .Check();
        }
    }
}





// ============================================================
// MULTIPLAYER CAMPAIGN GAME STATE
// ============================================================

public static class MultiplayerCampaignGameState
{
    private static readonly object Sync =
        new object();

    private static bool _initialized;

    private static bool _campaignReady;

    private static bool _networkReady;

    private static bool _isHost;

    private static bool _isClient;

    private static DateTime _startedUtc;

    public static bool Initialized
    {
        get
        {
            lock (Sync)
            {
                return _initialized;
            }
        }
    }

    public static bool CampaignReady
    {
        get
        {
            lock (Sync)
            {
                return _campaignReady;
            }
        }
    }

    public static bool NetworkReady
    {
        get
        {
            lock (Sync)
            {
                return _networkReady;
            }
        }
    }

    public static bool IsHost
    {
        get
        {
            lock (Sync)
            {
                return _isHost;
            }
        }
    }

    public static bool IsClient
    {
        get
        {
            lock (Sync)
            {
                return _isClient;
            }
        }
    }

    public static DateTime StartedUtc
    {
        get
        {
            lock (Sync)
            {
                return _startedUtc;
            }
        }
    }

    public static void InitializeHost()
    {
        lock (Sync)
        {
            _initialized =
                true;

            _campaignReady =
                false;

            _networkReady =
                false;

            _isHost =
                true;

            _isClient =
                false;

            _startedUtc =
                DateTime.UtcNow;
        }
    }

    public static void InitializeClient()
    {
        lock (Sync)
        {
            _initialized =
                true;

            _campaignReady =
                false;

            _networkReady =
                false;

            _isHost =
                false;

            _isClient =
                true;

            _startedUtc =
                DateTime.UtcNow;
        }
    }

    public static void SetCampaignReady(
        bool value)
    {
        lock (Sync)
        {
            _campaignReady =
                value;
        }
    }

    public static void SetNetworkReady(
        bool value)
    {
        lock (Sync)
        {
            _networkReady =
                value;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _initialized =
                false;

            _campaignReady =
                false;

            _networkReady =
                false;

            _isHost =
                false;

            _isClient =
                false;

            _startedUtc =
                DateTime.MinValue;
        }
    }
}







// ============================================================
// MAIN CAMPAIGN MULTIPLAYER TICK
// ============================================================

internal static class MainMultiplayerTick
{
    public static void Update(
        float dt)
    {
        CampaignReadinessMonitor
            .Update(
                dt
            );

        MultiplayerNetworkClient
            .Instance
            .Update();

        if (
            MultiplayerCampaignGameState
                .CampaignReady)
        {
            PlayerSnapshotSendTimer
                .Update(
                    dt
                );

            RemotePlayerGameTick
                .Update(
                    dt
                );
        }
    }
}







// ============================================================
// FINAL CAMPAIGN TICK ADAPTER
// ============================================================

public sealed class MultiplayerCampaignTickAdapter
{
    private float _lastDt;

    public void Tick(
        float dt)
    {
        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
        {
            dt =
                0f;
        }

        _lastDt =
            dt;

        /*
         * EVERYTHING here runs on the Bannerlord
         * Campaign/Game thread.
         *
         * Network callbacks only enqueue data.
         */

        MainMultiplayerTick
            .Update(
                _lastDt
            );
    }

    public void Reset()
    {
        _lastDt =
            0f;
    }
}







// ============================================================
// MASTER UPDATE
// ============================================================

internal static class MultiplayerCampaignMasterUpdate
{
    public static void Tick(
        float dt)
    {
        CampaignThreadDispatcher
            .Process();

        CampaignReadinessMonitor
            .Update(
                dt
            );

        if (
            !MultiplayerSessionValidator
                .CanRunCampaign())
        {
            return;
        }

        MultiplayerNetworkClient
            .Instance
            .Update();

        MultiplayerNetworkUpdateBridge
            .Update(
                dt
            );

        ExtendedRemotePlayerTick
            .Tick(
                dt
            );
    }

    public static void Clear()
    {
        CampaignThreadDispatcher
            .Clear();

        MultiplayerNetworkUpdateBridge
            .Clear();

        RemotePlayerFinalCleanup
            .Clear();
    }
}





// ============================================================
// HOST BROADCAST EXTENSIONS
// ============================================================

public static class MultiplayerCampaignHostExtensions
{
    public static void BroadcastHostSnapshot(
        this MultiplayerCampaignHost host,
        string targetPlayerId)
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

        CampaignVec2 position =
            MobileParty.MainParty.Position;

        int partySize =
            CampaignWorld.GetMainPartySize();

        byte[] payload =
            HostSnapshotBuilder.Build(
                LocalPlayerState.GetNetworkId(),
                LocalPlayerState.GetDisplayName(),
                position.X,
                position.Y,
                partySize
            );

        if (payload == null)
        {
            return;
        }

        NetworkMessageData message =
            new NetworkMessageData(
                NetworkPacketType.PlayerSnapshot,
                payload
            );

        HostClientConnection[] clients =
            host.GetClientsSnapshot();

        if (clients == null)
        {
            return;
        }

        for (
            int i = 0;
            i < clients.Length;
            i++)
        {
            HostClientConnection client =
                clients[i];

            if (client == null)
            {
                continue;
            }

            if (!client.Ready)
            {
                continue;
            }

            if (
                !string.IsNullOrWhiteSpace(
                    targetPlayerId) &&
                client.PlayerId != targetPlayerId)
            {
                continue;
            }

            try
            {
                client.Send(
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
// HOST SNAPSHOT BROADCAST LOOP
// ============================================================

internal static class HostCampaignSnapshotLoop
{
    private static float _timer;

    private static long _sequence;

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

        CampaignPlayerSnapshot hostSnapshot =
            HostCampaignSnapshotBuilder
                .BuildHostSnapshot();

        hostSnapshot.Sequence =
            ++_sequence;

        CampaignStateSynchronization
            .MarkSynced(
                hostSnapshot.Sequence
            );

        byte[] hostPayload =
            CampaignPlayerSnapshotCodec
                .Encode(
                    hostSnapshot
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

        HostClientConnection[] clients =
            host.GetClientsSnapshot();

        if (clients == null)
        {
            return;
        }

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
         * Broadcast the latest state of the remote client.
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

            CampaignPlayerSnapshot clientSnapshot =
                new CampaignPlayerSnapshot();

            clientSnapshot.PlayerId =
                sender.PlayerId;

            clientSnapshot.PlayerName =
                sender.PlayerName;

            clientSnapshot.Position =
                new CampaignVec2(
                    new Vec2(
                        sender.LastX,
                        sender.LastY
                    ),
                    true
                );

            clientSnapshot.PartySize =
                sender.LastPartySize;

            clientSnapshot.Connected =
                true;

            clientSnapshot.Ready =
                sender.Ready;

            clientSnapshot.Sequence =
                ++_sequence;

            clientSnapshot.TimestampUtcTicks =
                DateTime.UtcNow.Ticks;

            byte[] clientPayload =
                CampaignPlayerSnapshotCodec
                    .Encode(
                        clientSnapshot
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

    public static void Reset()
    {
        _timer =
            0f;

        _sequence =
            0;
    }
}







// ============================================================
// CLIENT CAMPAIGN SNAPSHOT RECEIVER
// ============================================================

internal static class ClientCampaignSnapshotReceiver
{
    private static readonly ConcurrentQueue<
        CampaignPlayerSnapshot>
        Queue =
            new ConcurrentQueue<
                CampaignPlayerSnapshot>();

    public static void Receive(
        byte[] payload)
    {
        CampaignPlayerSnapshot snapshot;

        if (
            !CampaignPlayerSnapshotCodec
                .Decode(
                    payload,
                    out snapshot))
        {
            return;
        }

        if (
            snapshot == null)
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

        Queue.Enqueue(
            snapshot
        );
    }

    public static void Process()
    {
        int processed =
            0;

        while (
            processed < 128 &&
            Queue.TryDequeue(
                out CampaignPlayerSnapshot snapshot))
        {
            processed++;

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

            Apply(
                snapshot
            );
        }
    }

    private static void Apply(
        CampaignPlayerSnapshot snapshot)
    {
        RemotePlayerSnapshotApplier
            .Apply(
                new NetworkPlayerSnapshot
                {
                    PlayerId =
                        snapshot.PlayerId,

                    PlayerName =
                        snapshot.PlayerName,

                    X =
                        snapshot.PositionX,

                    Y =
                        snapshot.PositionY,

                    PartySize =
                        snapshot.PartySize,

                    Timestamp =
                        snapshot.TimestampUtcTicks
                }
            );

        RemotePlayerState state;

        if (
            RemotePlayerManager
                .TryGet(
                    snapshot.PlayerId,
                    out state) &&
            state != null)
        {
            state.LastPacketUtc =
                DateTime.UtcNow;

            state.Active =
                snapshot.Connected;

            state.Spawned =
                true;

            state.TargetPosition =
                snapshot.Position;
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
// FULL MULTIPLAYER TICK
// ============================================================

internal static class FullMultiplayerTick
{
    private static float _timer;

    public static void Update(
        float dt)
    {
        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
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

        MultiplayerNetworkClient
            .Instance
            .Update();

        ClientCampaignSnapshotReceiver
            .Process();

        if (
            MultiplayerCampaignGameState
                .CampaignReady)
        {
            ClientPlayerStateSender
                .Update(
                    dt
                );
        }

        RemotePlayerUpdateService
            .Update(
                dt
            );

        _timer +=
            dt;

        if (_timer >= 1f)
        {
            _timer =
                0f;

            SessionStatusUpdater
                .Update(
                    1f
                );
        }
    }
}







// ============================================================
// FINAL ENTRY POINT
// ============================================================

public static class MultiplayerCampaignEntryPoint
{
    public static void Load()
    {
        FinalSubModuleState
            .OnLoad();
    }

    public static void CampaignStarted()
    {
        FinalSubModuleState
            .OnCampaignStart();
    }

    public static void Tick(
        float dt)
    {
        FinalMasterUpdateV2
            .Tick(
                dt
            );
    }

    public static void CampaignEnded()
    {
        FinalSubModuleState
            .OnCampaignEnd();
    }

    public static void Unload()
    {
        FinalSubModuleState
            .OnUnload();
    }
}







// ============================================================
// CAMPAIGN BEHAVIOR V2
// ============================================================

public sealed class MultiplayerCampaignBehaviorV2
    : CampaignBehaviorBase
{
    private bool _initialized;

    public override void RegisterEvents()
    {
        CampaignEvents
            .TickEvent
            .AddNonSerializedListener(
                this,
                OnTick
            );

        CampaignEvents
            .OnGameLoadedEvent
            .AddNonSerializedListener(
                this,
                _ => OnGameLoadFinished()
            );
    }

    private void OnTick(
        float dt)
    {
        if (
            !_initialized)
        {
            _initialized =
                true;

            CampaignBehaviorSafePatch
                .Start();
        }

        CampaignBehaviorSafePatch
            .Tick(
                dt
            );
    }

    private void OnGameLoadFinished()
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        try
        {
            CampaignSessionCoordinator
                .Update();

            CampaignSafeStartup
                .Initialize();

            CampaignSafeStartup
                .Update();

            MultiplayerCampaignGameState
                .SetCampaignReady(
                    Campaign.Current != null &&
                    Hero.MainHero != null &&
                    MobileParty.MainParty != null
                );
        }
        catch
        {
        }
    }

    public override void SyncData(
        IDataStore dataStore)
    {
        /*
         * Network session data must not be serialized
         * into Bannerlord's Campaign save.
         *
         * The Campaign itself remains authoritative for
         * local persistent data.
         */
    }
}







// ============================================================
// FINAL MULTIPLAYER TICK V3
// ============================================================

internal static class FinalMultiplayerTickV3
{
    private static float _timer;

    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
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

        /*
         * Network receive.
         */

        MultiplayerNetworkClient
            .Instance
            .Update();

        /*
         * Campaign-thread queue.
         */

        CampaignThreadDispatcher
            .Process();

        /*
         * Remote-player synchronization.
         */

        RemotePlayerCommandProcessor
            .Process(
                dt
            );

        RemotePlayerSnapshotLoop
            .Process();

        RemotePlayerManager
            .Update(
                dt
            );

        /*
         * Local-player state.
         */

        PlayerSnapshotSendTimer
            .Update(
                Math.Min(
                    dt,
                    0.10f
                )
            );

        /*
         * Map state.
         */

        FinalSafeMapUpdate
            .Update(
                dt
            );

        /*
         * Session maintenance.
         */

        if (_timer >= 1f)
        {
            _timer =
                0f;

            SessionStatusUpdater
                .Update(
                    1f
                );

            MultiplayerConnectionMonitor
                .Update(
                    1f
                );
        }
    }

    public static void Reset()
    {
        _timer =
            0f;
    }
}







// ============================================================
// FINAL ENTRY SERVICE
// ============================================================

public static class MultiplayerCampaignFinalService
{
    private static bool _active;

    public static bool Active
    {
        get
        {
            return _active;
        }
    }

    public static void Start()
    {
        if (_active)
        {
            return;
        }

        _active =
            true;

        FinalCampaignNetworkController
            .Initialize();

        CampaignSafeStartup
            .Initialize();

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .Connected
            );
    }

    public static void Tick(
        float dt)
    {
        if (!_active)
        {
            Start();
        }

        FinalMultiplayerTickV3
            .Update(
                dt
            );
    }

    public static void Stop()
    {
        if (!_active)
        {
            return;
        }

        _active =
            false;

        FinalMultiplayerTickV3
            .Reset();

        FinalCampaignNetworkController
            .Shutdown();

        FinalStateCleanup
            .Execute();
    }
}







// ============================================================
// FINAL CAMPAIGN SERVICE ENTRY
// ============================================================

public static class MultiplayerCampaignServiceEntry
{
    public static void OnLoad()
    {
        FinalSubModuleState
            .OnLoad();

        CampaignSafeStartup
            .Reset();

        FinalGlobalReset
            .Execute();

        CampaignSafeStartup
            .Initialize();

        HostConsole.WriteLine(
            "[MultiplayerCampaign] " +
            "Service loaded."
        );
    }

    public static void OnCampaignStart()
    {
        FinalSafeExecution.Run(
            "CampaignStart",
            () =>
            {
                CampaignSafeStartup
                    .Initialize();

                MultiplayerCampaignFinalService
                    .Start();
            }
        );
    }

    public static void OnTick(
        float dt)
    {
        FinalSafeExecution.Run(
            "CampaignTick",
            () =>
            {
                if (
                    Campaign.Current == null)
                {
                    return;
                }

                MultiplayerCampaignFinalService
                    .Tick(
                        dt
                    );
            }
        );
    }

    public static void OnCampaignEnd()
    {
        FinalSafeExecution.Run(
            "CampaignEnd",
            () =>
            {
                MultiplayerCampaignFinalService
                    .Stop();

                CampaignSafeStartup
                    .Reset();
            }
        );
    }

    public static void OnUnload()
    {
        FinalSafeExecution.Run(
            "Unload",
            () =>
            {
                MultiplayerCampaignFinalService
                    .Stop();

                FinalGlobalReset
                    .Execute();

                FinalSubModuleState
                    .OnUnload();
            }
        );
    }
}







// ============================================================
// FINAL CAMPAIGN BEHAVIOR
// ============================================================

public sealed class MultiplayerCampaignFinalBehavior
    : CampaignBehaviorBase
{
    private bool _initialized;

    public override void RegisterEvents()
    {
        CampaignEvents
            .TickEvent
            .AddNonSerializedListener(
                this,
                OnTick
            );

        CampaignEvents.OnGameLoadedEvent
            .AddNonSerializedListener(
                this,
                starter => OnGameLoaded(starter));
    }

    private void OnTick(
        float dt)
    {
        if (!_initialized)
        {
            _initialized =
                true;

            FinalModLifecycle
                .CampaignStart();
        }

        FinalModLifecycle
            .Tick(
                dt
            );
    }

    private void OnGameLoaded(CampaignGameStarter starter)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        try
        {
            CampaignSafeStartup
                .Update();

            CampaignSessionCoordinator
                .Update();

            MultiplayerCampaignGameState
                .SetCampaignReady(
                    Campaign.Current != null &&
                    Hero.MainHero != null &&
                    MobileParty.MainParty != null
                );
        }
        catch
        {
        }
    }

    public override void SyncData(
        IDataStore dataStore)
    {
        /*
         * Network player state is session-only.
         *
         * Do not write it into Campaign save data.
         */
    }
}







// ============================================================
// END OF REBUILT MULTIPLAYER CAMPAIGN
// ============================================================
// ============================================================
// FINAL MODULE WRAPPER
// ============================================================

public sealed class MultiplayerCampaignFinalSubModule
    : MBSubModuleBase
{
    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();

        FinalSubmoduleCallback
            .OnLoad();
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
            game == null ||
            gameStarter == null)
        {
            return;
        }

        if (
            game.GameType is Campaign &&
            gameStarter is CampaignGameStarter starter)
        {
            starter.AddBehavior(
                new MultiplayerCampaignFinalBehavior()
            );
        }
    }

    public override void OnGameEnd(
        Game game)
    {
        FinalSubmoduleCallback
            .OnCampaignEnd();

        base.OnGameEnd(
            game
        );
    }

    protected override void OnSubModuleUnloaded()
    {
        FinalSubmoduleCallback
            .OnUnload();

        base.OnSubModuleUnloaded();
    }
}







// ============================================================
// FINAL SAFE API
// ============================================================

internal static class MultiplayerCampaignSafeApi
{
    public static void Tick(
        float dt)
    {
        try
        {
            FinalCampaignLoop
                .Tick(
                    dt
                );
        }
        catch (Exception ex)
        {
            FinalErrorReporter
                .Report(
                    "CampaignLoop",
                    ex
                );
        }
    }

    public static void Shutdown()
    {
        try
        {
            FinalCampaignLoop
                .Reset();
        }
        catch
        {
        }

        FinalShutdown
            .Execute();
    }
}







// ============================================================
// FINAL FILE TERMINATOR
// ============================================================

internal static class MultiplayerCampaignFileTerminator
{
    public static void Execute()
    {
        MultiplayerCampaignSafeApi
            .Shutdown();
    }
}







// ============================================================
// END OF REBUILT FILE
// ============================================================
// ============================================================
// FINAL CLOSE
// ============================================================

internal static class MultiplayerCampaignClose
{
    public static void Execute()
    {
        try
        {
            MultiplayerCampaignSafeApi
                .Shutdown();
        }
        catch
        {
        }

        try
        {
            MultiplayerCleanup
                .Execute();
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
            RemotePlayerManager
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
            PlayerIdentity
                .Reset();
        }
        catch
        {
        }
    }
}







// ============================================================
// FINAL END
// ============================================================
// ============================================================
// FINAL END OF MULTIPLAYER CAMPAIGN
// ============================================================

internal static class MultiplayerCampaignFinalization
{
    public static void Execute()
    {
        try
        {
            MultiplayerCampaignClose
                .Execute();
        }
        catch
        {
        }

        try
        {
            FinalInitializationGuard
                .Reset();
        }
        catch
        {
        }

        try
        {
            FinalGlobalReset
                .Execute();
        }
        catch
        {
        }
    }
}







// ============================================================
// HOST CAMPAIGN SNAPSHOT BUILDER
// ============================================================

internal static class HostCampaignSnapshotBuilder
{
    public static CampaignPlayerSnapshot
        BuildHostSnapshot()
    {
        CampaignPlayerSnapshot snapshot =
            new CampaignPlayerSnapshot();

        snapshot.PlayerId =
            LocalPlayerState
                .GetNetworkId();

        snapshot.PlayerName =
            LocalPlayerState
                .GetDisplayName();

        snapshot.Connected =
            true;

        snapshot.Ready =
            true;

        snapshot.PartySize =
            CampaignWorld
                .GetMainPartySize();

        CampaignVec2 position;

        if (
            CampaignWorld
                .TryGetMainPartyPosition(
                    out position))
        {
            snapshot.Position =
                position;
        }

        snapshot.TimestampUtcTicks =
            DateTime.UtcNow.Ticks;

        snapshot.Sequence =
            CampaignStateSynchronization
                .LastSequence +
                1;

        return snapshot;
    }
}







// ============================================================
// END OF CONTINUATION
// ============================================================
// ============================================================
// CAMPAIGN STATE SYNCHRONIZATION
// ============================================================

internal static class CampaignStateSynchronization
{
    private static readonly object Sync =
        new object();

    private static long _lastSequence;

    private static DateTime _lastSyncUtc =
        DateTime.MinValue;

    public static long LastSequence
    {
        get
        {
            lock (Sync)
            {
                return _lastSequence;
            }
        }
    }

    public static DateTime LastSyncUtc
    {
        get
        {
            lock (Sync)
            {
                return _lastSyncUtc;
            }
        }
    }

    public static void MarkSynced(
        long sequence)
    {
        lock (Sync)
        {
            if (
                sequence <
                _lastSequence)
            {
                return;
            }

            _lastSequence =
                sequence;

            _lastSyncUtc =
                DateTime.UtcNow;
        }
    }

    public static bool IsRecent()
    {
        lock (Sync)
        {
            if (
                _lastSyncUtc ==
                DateTime.MinValue)
            {
                return false;
            }

            return
                DateTime.UtcNow -
                _lastSyncUtc <
                TimeSpan.FromSeconds(
                    5
                );
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _lastSequence =
                0;

            _lastSyncUtc =
                DateTime.MinValue;
        }
    }
}


namespace MultiplayerCampaign
{


    /*
     * ============================================================
     * CAMPAIGN MESSAGE FEED
     * ============================================================
     *
     * Game/Campaign thread only.
     *
     * This is used to prove that the Client really entered
     * the Host world/session.
     *
     * Client:
     *
     *     WorldJoinTest
     *
     * Host:
     *
     *     WorldJoinAck
     *
     * ============================================================
     */

    internal static class CampaignMessageFeed
    {
        public static void Show(
            string message)
        {
            if (
                string.IsNullOrWhiteSpace(
                    message))
            {
                return;
            }

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
        }
    }






    /*
     * ============================================================
     * CAMPAIGN BEHAVIOR
     * ============================================================
     */

    public sealed class MultiplayerCampaignBehavior
        : CampaignBehaviorBase
    {
        private float _networkTimer;

        private bool _playerReadySent;

        private bool _worldInitialized;

        public override void RegisterEvents()
        {
            CampaignEvents
                .TickEvent
                .AddNonSerializedListener(
                    this,
                    OnCampaignTick
                );
        }

        private void OnCampaignTick(
            float dt)
        {
            /*
             * HOST
             */

            MultiplayerCampaignSubModule
                .StartHostIfReady();

            /*
             * PROCESS NETWORK QUEUE
             *
             * Game Thread.
             */

            MultiplayerNetworkClient
                .Instance
                .Update();

            /*
             * CLIENT WORLD
             */

            if (
                MultiplayerNetworkClient
                    .Instance
                    .IsWorldLoaded)
            {
                _worldInitialized = true;
            }

            /*
             * PLAYER READY
             */

            if (
                _worldInitialized &&
                !_playerReadySent &&
                MobileParty.MainParty != null)
            {
                _playerReadySent = true;

                HostConsole.WriteLine(
                    "[*] Client Campaign is active."
                );

                MultiplayerNetworkClient
                    .Instance
                    .SendPlayerReady();
            }

            /*
             * REMOTE PLAYERS
             *
             * All Hero/MobileParty changes happen here,
             * on the Campaign/Game thread.
             */

            RemotePlayerManager.Update(dt);

            /*
             * NPC WORLD SYNCHRONIZATION
             */

            WorldPartySynchronizer
                .ApplyPending(dt);

            /*
             * FIXED NETWORK RATE
             *
             * 10 updates/sec.
             */

            _networkTimer += dt;

            if (_networkTimer < 0.10f)
            {
                return;
            }

            _networkTimer = 0f;

            MultiplayerCampaignHost host =
                MultiplayerCampaignSubModule
                    .GetHost();

            if (host != null)
            {
                host.Update();
            }

            MultiplayerNetworkClient client =
                MultiplayerNetworkClient.Instance;

            /*
             * LOCAL PLAYER STATE
             *
             * Game Thread only.
             */

            if (
                client.IsConnected &&
                client.IsWorldLoaded &&
                MobileParty.MainParty != null)
            {
                client.SendLocalPlayerState(
                    MobileParty.MainParty.Position,
                    CampaignWorld.GetMainPartySize()
                );
            }
        }

        public override void SyncData(
            IDataStore dataStore)
        {
        }
    }




    /*
     * ============================================================
     * CAMPAIGN WORLD HELPER
     * ============================================================
     *
     * Bannerlord 1.3.4
     *
     * IMPORTANT:
     *
     * These methods may ONLY be called from the
     * Campaign/Game thread.
     *
     * Network threads must never call these methods.
     *
     * ============================================================
     */

    internal static class CampaignWorld
    {
        public static CampaignVec2 GetMainPartyPosition()
        {
            try
            {
                MobileParty mainParty =
                    MobileParty.MainParty;

                if (mainParty != null)
                {
                    return mainParty.Position;
                }
            }
            catch
            {
            }

            return new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );
        }

        public static int GetMainPartySize()
        {
            try
            {
                MobileParty mainParty =
                    MobileParty.MainParty;

                if (
                    mainParty != null &&
                    mainParty.MemberRoster != null)
                {
                    return Math.Max(
                        1,
                        mainParty
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
    

    public static bool TryGetMainPartyPosition(
        out CampaignVec2 position)
    {
        try
        {
            MobileParty mainParty = MobileParty.MainParty;
            if (mainParty != null)
            {
                position = mainParty.Position;
                return true;
            }
        }
        catch
        {
        }
        position = new CampaignVec2(new Vec2(0f, 0f), true);
        return false;
    }
}

}

