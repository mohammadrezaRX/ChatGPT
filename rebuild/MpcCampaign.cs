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

}

