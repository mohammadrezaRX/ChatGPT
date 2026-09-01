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

// ============================
// CONTINUATION
// ============================

internal static class LocalPlayerState
{
    private static readonly object Sync =
        new object();

    private static string _displayName =
        "Player";

    private static readonly string NetworkId =
        Guid.NewGuid().ToString(
            "N"
        );

    public static string GetNetworkId()
    {
        return NetworkId;
    }

    public static string GetDisplayName()
    {
        lock (Sync)
        {
            if (
                string.IsNullOrWhiteSpace(
                    _displayName))
            {
                return "Player";
            }

            return _displayName;
        }
    }

    public static void SetDisplayName(
        string name)
    {
        lock (Sync)
        {
            if (
                string.IsNullOrWhiteSpace(
                    name))
            {
                _displayName =
                    "Player";

                return;
            }

            string cleaned =
                name.Trim();

            if (cleaned.Length > 32)
            {
                cleaned =
                    cleaned.Substring(
                        0,
                        32
                    );
            }

            _displayName =
                cleaned;
        }
    }
}



/*
 * ============================================================
 * REMOTE PLAYER STATE
 * ============================================================
 */

public sealed class RemotePlayerState
{
    public string PlayerId;

    public string Name;

    public CampaignVec2 CurrentPosition;

    public CampaignVec2 TargetPosition;

    public int PartySize;

    public bool Active;

    public bool Spawned;

    public bool IsHostRemote;

    /*
     * These references intentionally remain optional.
     *
     * A remote player MUST NOT be inserted into the local
     * Campaign as a partially initialized Hero/MobileParty.
     */

    public Hero Hero;

    public MobileParty Party;

    public float LastUpdateTime;

    public DateTime LastPacketUtc;
}



/*
 * ============================================================
 * REMOTE PLAYER COMMAND
 * ============================================================
 */

internal enum RemotePlayerCommandType
{
    Join,

    JoinWithState,

    State,

    Leave
}



internal sealed class RemotePlayerCommand
{
    public RemotePlayerCommandType Type;

    public string PlayerId;

    public string Name;

    public CampaignVec2 Position;

    public int PartySize;

    public static RemotePlayerCommand JoinWithState(
        string playerId,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        return new RemotePlayerCommand
        {
            Type =
                RemotePlayerCommandType.JoinWithState,

            PlayerId =
                playerId,

            Name =
                name,

            Position =
                position,

            PartySize =
                partySize
        };
    }

    public static RemotePlayerCommand State(
        string playerId,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        return new RemotePlayerCommand
        {
            Type =
                RemotePlayerCommandType.State,

            PlayerId =
                playerId,

            Name =
                name,

            Position =
                position,

            PartySize =
                partySize
        };
    }

    public static RemotePlayerCommand Join(
        string playerId,
        string name)
    {
        return new RemotePlayerCommand
        {
            Type =
                RemotePlayerCommandType.Join,

            PlayerId =
                playerId,

            Name =
                name,

            PartySize = 1
        };
    }

    public static RemotePlayerCommand Leave(
        string playerId)
    {
        return new RemotePlayerCommand
        {
            Type =
                RemotePlayerCommandType.Leave,

            PlayerId =
                playerId
        };
    }
}



/*
 * ============================================================
 * REMOTE PLAYER MANAGER
 * ============================================================
 */

public static partial class RemotePlayerManager
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        RemotePlayerState>
        Players =
            new Dictionary<
                string,
                RemotePlayerState>();

    private static readonly ConcurrentQueue<
        RemotePlayerCommand>
        Commands =
            new ConcurrentQueue<
                RemotePlayerCommand>();

    private static readonly ConcurrentQueue<
        byte[]>
        SnapshotQueue =
            new ConcurrentQueue<
                byte[]>();

    private static readonly ConcurrentQueue<
        byte[]>
        LeaveQueue =
            new ConcurrentQueue<
                byte[]>();

    public static int Count
    {
        get
        {
            lock (Sync)
            {
                return Players.Count;
            }
        }
    }


    /*
     * ========================================================
     * QUEUE SNAPSHOT
     * ========================================================
     */

    public static void QueueSnapshot(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        if (
            payload.Length >
            1024)
        {
            return;
        }

        SnapshotQueue.Enqueue(
            payload
        );
    }


    /*
     * ========================================================
     * QUEUE LEAVE
     * ========================================================
     */

    public static void QueueLeave(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        if (
            payload.Length >
            1024)
        {
            return;
        }

        LeaveQueue.Enqueue(
            payload
        );
    }


    /*
     * ========================================================
     * PROCESS SNAPSHOTS
     * ========================================================
     */

    private static void ProcessSnapshotQueue()
    {
        while (
            SnapshotQueue.TryDequeue(
                out byte[] payload))
        {
            try
            {
                ProcessSnapshot(
                    payload
                );
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Snapshot error: " +
                    ex.Message
                );
            }
        }
    }


    private static void ProcessSnapshot(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        using (
            MemoryStream stream =
                new MemoryStream(
                    payload))
        using (
            System.IO.BinaryReader reader =
                new System.IO.BinaryReader(
                    stream,
                    Encoding.UTF8,
                    true))
        {
            string playerId =
                reader.ReadString();

            string name =
                reader.ReadString();

            float x =
                reader.ReadSingle();

            float y =
                reader.ReadSingle();

            int partySize =
                reader.ReadInt32();

            if (
                string.IsNullOrWhiteSpace(
                    playerId))
            {
                return;
            }

            if (
                playerId ==
                LocalPlayerState.GetNetworkId())
            {
                return;
            }

            if (
                float.IsNaN(x) ||
                float.IsInfinity(x) ||
                float.IsNaN(y) ||
                float.IsInfinity(y))
            {
                return;
            }

            if (name == null)
            {
                name = "Player";
            }

            if (name.Length > 32)
            {
                name =
                    name.Substring(
                        0,
                        32
                    );
            }

            partySize =
                Math.Max(
                    1,
                    Math.Min(
                        10000,
                        partySize
                    )
                );

            CampaignVec2 position =
                new CampaignVec2(
                    new Vec2(
                        x,
                        y
                    ),
                    true
                );

            Commands.Enqueue(
                RemotePlayerCommand.State(
                    playerId,
                    name,
                    position,
                    partySize
                )
            );
        }
    }


    /*
     * ========================================================
     * PROCESS LEAVES
     * ========================================================
     */

    private static void ProcessLeaveQueue()
    {
        while (
            LeaveQueue.TryDequeue(
                out byte[] payload))
        {
            try
            {
                ProcessLeave(
                    payload
                );
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Player leave error: " +
                    ex.Message
                );
            }
        }
    }


    private static void ProcessLeave(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        string playerId =
            NetworkProtocol.ReadString(
                payload
            );

        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        Commands.Enqueue(
            RemotePlayerCommand.Leave(
                playerId
            )
        );
    }


    /*
     * ========================================================
     * UPDATE
     * ========================================================
     */

    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            while (
                Commands.TryDequeue(
                    out _))
            {
            }

            return;
        }

        ProcessSnapshotQueue();

        ProcessLeaveQueue();

        while (
            Commands.TryDequeue(
                out RemotePlayerCommand command))
        {
            if (command == null)
            {
                continue;
            }

            try
            {
                ApplyCommand(
                    command
                );
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Remote player error: " +
                    ex.Message
                );
            }
        }

        RemotePlayerState[] states;

        lock (Sync)
        {
            if (Players.Count == 0)
            {
                return;
            }

            states =
                new RemotePlayerState[
                    Players.Count
                ];

            Players.Values.CopyTo(
                states,
                0
            );
        }

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerState state =
                states[i];

            if (state == null)
            {
                continue;
            }

            if (!state.Active)
            {
                continue;
            }

            float safeDt =
                Math.Max(
                    0f,
                    Math.Min(
                        0.25f,
                        dt
                    )
                );

            float interpolation =
                Math.Min(
                    1f,
                    Math.Max(
                        0.01f,
                        safeDt * 10f
                    )
                );

            CampaignVec2 current =
                state.CurrentPosition;

            CampaignVec2 target =
                state.TargetPosition;

            float x =
                current.X +
                (
                    target.X -
                    current.X
                ) *
                interpolation;

            float y =
                current.Y +
                (
                    target.Y -
                    current.Y
                ) *
                interpolation;

            if (
                float.IsNaN(x) ||
                float.IsInfinity(x) ||
                float.IsNaN(y) ||
                float.IsInfinity(y))
            {
                continue;
            }

            state.CurrentPosition =
                new CampaignVec2(
                    new Vec2(
                        x,
                        y
                    ),
                    true
                );

            state.LastUpdateTime +=
                safeDt;

            /*
             * Deliberately do NOT write:
             *
             * state.Party.Position = ...
             *
             * because a Remote Player is not allowed to
             * mutate an invalid local Campaign MobileParty.
             */
        }
    }


    /*
     * ========================================================
     * APPLY COMMAND
     * ========================================================
     */

    private static void ApplyCommand(
        RemotePlayerCommand command)
    {
        if (command == null)
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(
                command.PlayerId))
        {
            return;
        }

        if (
            command.PlayerId ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        switch (command.Type)
        {
            case RemotePlayerCommandType.Join:
                ApplyJoin(
                    command
                );
                break;

            case RemotePlayerCommandType.JoinWithState:
                ApplyJoinWithState(
                    command
                );
                break;

            case RemotePlayerCommandType.State:
                ApplyState(
                    command
                );
                break;

            case RemotePlayerCommandType.Leave:
                ApplyLeave(
                    command
                );
                break;
        }
    }


    /*
     * ========================================================
     * JOIN
     * ========================================================
     */

    private static void ApplyJoin(
        RemotePlayerCommand command)
    {
        RemotePlayerState state;

        lock (Sync)
        {
            if (
                Players.TryGetValue(
                    command.PlayerId,
                    out state))
            {
                state.Name =
                    NormalizeName(
                        command.Name
                    );

                state.Active = true;

                state.Spawned = true;

                state.LastPacketUtc =
                    DateTime.UtcNow;

                return;
            }

            CampaignVec2 position =
                CampaignWorld
                    .GetMainPartyPosition();

            state =
                new RemotePlayerState
                {
                    PlayerId =
                        command.PlayerId,

                    Name =
                        NormalizeName(
                            command.Name
                        ),

                    CurrentPosition =
                        position,

                    TargetPosition =
                        position,

                    PartySize = 1,

                    Active = true,

                    Spawned = false,

                    LastPacketUtc =
                        DateTime.UtcNow
                };

            Players.Add(
                command.PlayerId,
                state
            );
        }

        CreateRemotePlayer(
            state
        );
    }


    /*
     * ========================================================
     * JOIN WITH STATE
     * ========================================================
     */

    private static void ApplyJoinWithState(
        RemotePlayerCommand command)
    {
        RemotePlayerState state;

        lock (Sync)
        {
            if (
                !Players.TryGetValue(
                    command.PlayerId,
                    out state))
            {
                state =
                    new RemotePlayerState
                    {
                        PlayerId =
                            command.PlayerId,

                        Name =
                            NormalizeName(
                                command.Name
                            ),

                        CurrentPosition =
                            command.Position,

                        TargetPosition =
                            command.Position,

                        PartySize =
                            Math.Max(
                                1,
                                command.PartySize
                            ),

                        Active = true,

                        Spawned = false,

                        LastPacketUtc =
                            DateTime.UtcNow
                    };

                Players.Add(
                    command.PlayerId,
                    state
                );
            }
            else
            {
                state.Name =
                    NormalizeName(
                        command.Name
                    );

                state.TargetPosition =
                    command.Position;

                state.PartySize =
                    Math.Max(
                        1,
                        command.PartySize
                    );

                state.Active = true;

                state.LastPacketUtc =
                    DateTime.UtcNow;
            }
        }

        if (!state.Spawned)
        {
            CreateRemotePlayer(
                state
            );
        }
    }


    /*
     * ========================================================
     * STATE
     * ========================================================
     */

    private static void ApplyState(
        RemotePlayerCommand command)
    {
        RemotePlayerState state;

        lock (Sync)
        {
            if (
                !Players.TryGetValue(
                    command.PlayerId,
                    out state))
            {
                state =
                    new RemotePlayerState
                    {
                        PlayerId =
                            command.PlayerId,

                        Name =
                            NormalizeName(
                                command.Name
                            ),

                        CurrentPosition =
                            command.Position,

                        TargetPosition =
                            command.Position,

                        PartySize =
                            Math.Max(
                                1,
                                command.PartySize
                            ),

                        Active = true,

                        Spawned = false,

                        LastPacketUtc =
                            DateTime.UtcNow
                    };

                Players.Add(
                    command.PlayerId,
                    state
                );
            }
            else
            {
                state.Name =
                    NormalizeName(
                        command.Name
                    );

                state.TargetPosition =
                    command.Position;

                state.PartySize =
                    Math.Max(
                        1,
                        command.PartySize
                    );

                state.Active = true;

                state.LastPacketUtc =
                    DateTime.UtcNow;
            }
        }

        if (!state.Spawned)
        {
            CreateRemotePlayer(
                state
            );
        }
    }


    /*
     * ========================================================
     * LEAVE
     * ========================================================
     */

    private static void ApplyLeave(
        RemotePlayerCommand command)
    {
        RemotePlayerState state = null;

        lock (Sync)
        {
            if (
                Players.TryGetValue(
                    command.PlayerId,
                    out state))
            {
                Players.Remove(
                    command.PlayerId
                );
            }
        }

        if (state != null)
        {
            DestroyRemotePlayer(
                state
            );
        }
    }


    /*
     * ========================================================
     * CREATE REMOTE PLAYER
     * ========================================================
     */

    private static bool CreateRemotePlayer(
        RemotePlayerState state)
    {
        if (state == null)
        {
            return false;
        }

        if (Campaign.Current == null)
        {
            return false;
        }

        /*
         * CRITICAL FIX:
         *
         * Do NOT create:
         *
         * HeroCreator.CreateBasicHero(...)
         *
         * or:
         *
         * MobilePartyHelper.SpawnLordParty(...)
         *
         * for a network-only remote player.
         *
         * Those objects become part of the local Campaign
         * lifecycle and can cause MapInfoVM.UpdatePlayerInfo()
         * to dereference an incompletely initialized object.
         */

        state.Hero =
            null;

        state.Party =
            null;

        state.Spawned =
            true;

        state.Active =
            true;

        state.LastPacketUtc =
            DateTime.UtcNow;

        HostConsole.WriteLine(
            "[+] Player joined: " +
            NormalizeName(
                state.Name
            )
        );

        return true;
    }


    /*
     * ========================================================
     * DESTROY REMOTE PLAYER
     * ========================================================
     */

    private static void DestroyRemotePlayer(
        RemotePlayerState state)
    {
        if (state == null)
        {
            return;
        }

        /*
         * No Campaign MobileParty is created by the new
         * remote-player system.
         *
         * Therefore cleanup is intentionally logical only.
         */

        state.Active =
            false;

        state.Spawned =
            false;

        state.Hero =
            null;

        state.Party =
            null;

        HostConsole.WriteLine(
            "[-] Player left: " +
            NormalizeName(
                state.Name
            )
        );
    }


    /*
     * ========================================================
     * NAME NORMALIZATION
     * ========================================================
     */

    private static string NormalizeName(
        string name)
    {
        if (
            string.IsNullOrWhiteSpace(
                name))
        {
            return "Player";
        }

        string value =
            name.Trim();

        if (value.Length > 32)
        {
            value =
                value.Substring(
                    0,
                    32
                );
        }

        return value;
    }


    /*
     * ========================================================
     * FIND PLAYER
     * ========================================================
     */

    public static bool TryGet(
        string playerId,
        out RemotePlayerState state)
    {
        state = null;

        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return false;
        }

        lock (Sync)
        {
            return Players.TryGetValue(
                playerId,
                out state
            );
        }
    }


    /*
     * ========================================================
     * SNAPSHOT
     * ========================================================
     */

    public static RemotePlayerState[] Snapshot()
    {
        lock (Sync)
        {
            RemotePlayerState[] result =
                new RemotePlayerState[
                    Players.Count
                ];

            int index = 0;

            foreach (
                RemotePlayerState state
                in Players.Values)
            {
                result[index++] =
                    state;
            }

            return result;
        }
    }


    /*
     * ========================================================
     * CLEAR
     * ========================================================
     */

    public static void Clear()
    {
        RemotePlayerState[] states;

        lock (Sync)
        {
            states =
                new RemotePlayerState[
                    Players.Count
                ];

            Players.Values.CopyTo(
                states,
                0
            );

            Players.Clear();
        }

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            DestroyRemotePlayer(
                states[i]
            );
        }

        while (
            Commands.TryDequeue(
                out _))
        {
        }

        while (
            SnapshotQueue.TryDequeue(
                out _))
        {
        }

        while (
            LeaveQueue.TryDequeue(
                out _))
        {
        }
    }
}



/*
 * ============================================================
 * PLAYER VISUAL DATA
 * ============================================================
 *
 * This class intentionally contains no Hero/MobileParty.
 *
 * A future Map Marker implementation can consume this
 * state safely without modifying Campaign ownership.
 */

public sealed class RemotePlayerVisualData
{
    public string PlayerId;

    public string DisplayName;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Visible;

    public DateTime LastUpdateUtc;
}



/*
 * ============================================================
 * REMOTE PLAYER VISUAL REGISTRY
 * ============================================================
 */

public static class RemotePlayerVisualRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        RemotePlayerVisualData>
        Visuals =
            new Dictionary<
                string,
                RemotePlayerVisualData>();

    public static void Refresh()
    {
        RemotePlayerState[] states =
            RemotePlayerManager.Snapshot();

        lock (Sync)
        {
            for (
                int i = 0;
                i < states.Length;
                i++)
            {
                RemotePlayerState state =
                    states[i];

                if (state == null)
                {
                    continue;
                }

                Visuals[state.PlayerId] =
                    new RemotePlayerVisualData
                    {
                        PlayerId =
                            state.PlayerId,

                        DisplayName =
                            state.Name,

                        Position =
                            state.CurrentPosition,

                        PartySize =
                            state.PartySize,

                        Visible =
                            state.Active,

                        LastUpdateUtc =
                            state.LastPacketUtc
                    };
            }

            List<string> remove =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    RemotePlayerVisualData>
                item in Visuals)
            {
                bool found = false;

                for (
                    int i = 0;
                    i < states.Length;
                    i++)
                {
                    if (
                        states[i] != null &&
                        states[i].PlayerId ==
                        item.Key)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    remove.Add(
                        item.Key
                    );
                }
            }

            for (
                int i = 0;
                i < remove.Count;
                i++)
            {
                Visuals.Remove(
                    remove[i]
                );
            }
        }
    }

    public static RemotePlayerVisualData[] Snapshot()
    {
        lock (Sync)
        {
            RemotePlayerVisualData[] result =
                new RemotePlayerVisualData[
                    Visuals.Count
                ];

            int index = 0;

            foreach (
                RemotePlayerVisualData item
                in Visuals.Values)
            {
                result[index++] =
                    item;
            }

            return result;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Visuals.Clear();
        }
    }
}

/*
 * ============================================================
 * HOST SERVER
 * ============================================================
 */

public sealed class MultiplayerCampaignHost
{
    private readonly object _sync =
        new object();

    private readonly List<
        HostClientConnection>
        _clients =
            new List<
                HostClientConnection>();

    private TcpListener _listener;

    private CancellationTokenSource _cts;

    private readonly string _hostName;

    private bool _running;

    private const int Port = 25565;

    public MultiplayerCampaignHost(
        string hostName)
    {
        _hostName =
            string.IsNullOrWhiteSpace(
                hostName)
                ? "Host"
                : hostName.Trim();
    }


    /*
     * ========================================================
     * START
     * ========================================================
     */

    public void Start()
    {
        if (_running)
        {
            return;
        }

        try
        {
            _cts =
                new CancellationTokenSource();

            _listener =
                new TcpListener(
                    IPAddress.Any,
                    Port
                );

            _listener.Start();

            _running = true;

            HostConsole.WriteLine(
                "[MultiplayerCampaign] " +
                "Server started on port " +
                Port
            );

            _ =
                AcceptLoopAsync(
                    _cts.Token
                );
        }
        catch (Exception ex)
        {
            _running =
                false;

            HostConsole.WriteLine(
                "[!] Server start failed: " +
                ex.Message
            );

            Stop();
        }
    }


    /*
     * ========================================================
     * ACCEPT LOOP
     * ========================================================
     */

    private async Task AcceptLoopAsync(
        CancellationToken token)
    {
        while (
            _running &&
            !token.IsCancellationRequested)
        {
            try
            {
                TcpClient tcpClient =
                    await _listener
                        .AcceptTcpClientAsync();

                if (
                    tcpClient == null)
                {
                    continue;
                }

                tcpClient.NoDelay =
                    true;

                HostClientConnection client =
                    new HostClientConnection(
                        this,
                        tcpClient
                    );

                lock (_sync)
                {
                    /*
                     * The target build is two-player.
                     *
                     * Existing host is player one.
                     * Only one remote client is required.
                     */

                    if (_clients.Count >= 1)
                    {
                        SendErrorAndClose(
                            client,
                            "Server is full."
                        );

                        continue;
                    }

                    _clients.Add(
                        client
                    );
                }

                _ =
                    client.StartAsync(
                        token
                    );
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (!_running)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    HostConsole.WriteLine(
                        "[!] Accept error: " +
                        ex.Message
                    );
                }
            }
        }
    }


    /*
     * ========================================================
     * CLIENT REMOVE
     * ========================================================
     */

    internal void RemoveClient(
        HostClientConnection client)
    {
        if (client == null)
        {
            return;
        }

        bool removed;

        lock (_sync)
        {
            removed =
                _clients.Remove(
                    client
                );
        }

        if (removed)
        {
            string id =
                client.PlayerId;

            if (
                !string.IsNullOrWhiteSpace(
                    id))
            {
                BroadcastPlayerLeave(
                    id
                );
            }

            if (
                !string.IsNullOrWhiteSpace(
                    client.PlayerName))
            {
                HostConsole.WriteLine(
                    "[MultiplayerCampaign] " +
                    "Player left: " +
                    client.PlayerName
                );
            }
        }
    }


    /*
     * ========================================================
     * SEND ERROR
     * ========================================================
     */

    private void SendErrorAndClose(
        HostClientConnection client,
        string message)
    {
        if (client == null)
        {
            return;
        }

        try
        {
            client.SendError(
                message
            );
        }
        catch
        {
        }

        client.Close();
    }


    /*
     * ========================================================
     * BROADCAST PLAYER LEAVE
     * ========================================================
     */

    private void BroadcastPlayerLeave(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        byte[] payload =
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        playerId
                    );
                }
            );

        NetworkMessageData data =
            new NetworkMessageData(
                NetworkPacketType.PlayerLeave,
                payload
            );

        HostClientConnection[] clients =
            GetClientsSnapshot();

        for (
            int i = 0;
            i < clients.Length;
            i++)
        {
            try
            {
                clients[i]?.Send(
                    data
                );
            }
            catch
            {
            }
        }
    }


    /*
     * ========================================================
     * CLIENT SNAPSHOT
     * ========================================================
     */

    internal void OnPlayerSnapshot(
        HostClientConnection sender,
        byte[] payload)
    {
        if (
            sender == null ||
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        if (
            sender.PlayerId == null)
        {
            return;
        }

        if (
            payload.Length >
            1024)
        {
            return;
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
                /*
                 * The player ID comes from the host-assigned
                 * connection identity.
                 *
                 * We deliberately DO NOT trust the network
                 * supplied ID for ownership.
                 */

                string suppliedId =
                    reader.ReadString();

                string suppliedName =
                    reader.ReadString();

                float x =
                    reader.ReadSingle();

                float y =
                    reader.ReadSingle();

                int partySize =
                    reader.ReadInt32();

                if (
                    float.IsNaN(x) ||
                    float.IsInfinity(x) ||
                    float.IsNaN(y) ||
                    float.IsInfinity(y))
                {
                    return;
                }

                string playerId =
                    sender.PlayerId;

                string playerName =
                    string.IsNullOrWhiteSpace(
                        sender.PlayerName)
                        ? suppliedName
                        : sender.PlayerName;

                playerName =
                    SanitizeName(
                        playerName
                    );

                partySize =
                    Math.Max(
                        1,
                        Math.Min(
                            10000,
                            partySize
                        )
                    );

                sender.LastX =
                    x;

                sender.LastY =
                    y;

                sender.LastPartySize =
                    partySize;

                sender.PlayerName =
                    playerName;

                BroadcastSnapshot(
                    sender
                );
            }
        }
        catch (Exception ex)
        {
            HostConsole.WriteLine(
                "[!] Player state error: " +
                ex.Message
            );
        }
    }


    /*
     * ========================================================
     * BROADCAST SNAPSHOT
     * ========================================================
     */

    private void BroadcastSnapshot(
        HostClientConnection changedClient)
    {
        HostClientConnection[] clients =
            GetClientsSnapshot();

        /*
         * First send the changed Client snapshot
         * to the Host-side game state.
         *
         * The Host campaign itself is player one and
         * therefore does not need to receive itself.
         *
         * Every connected Client receives the remote
         * player state of every other participant.
         */

        if (changedClient != null)
        {
            byte[] remotePayload =
                BuildClientSnapshot(
                    changedClient
                );

            NetworkMessageData message =
                new NetworkMessageData(
                    NetworkPacketType.PlayerSnapshot,
                    remotePayload
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
                    client == changedClient)
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

        /*
         * Also send the Host's own Campaign position to Client.
         */

        SendHostSnapshotToClients(
            clients
        );
    }


    /*
     * ========================================================
     * HOST SNAPSHOT
     * ========================================================
     */

    private void SendHostSnapshotToClients(
        HostClientConnection[] clients)
    {
        if (
            Campaign.Current == null ||
            MobileParty.MainParty == null)
        {
            return;
        }

        CampaignVec2 position =
            MobileParty.MainParty.Position;

        int size =
            CampaignWorld.GetMainPartySize();

        byte[] payload =
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    /*
                     * Host has a stable local network ID.
                     */

                    writer.Write(
                        LocalPlayerState
                            .GetNetworkId()
                    );

                    writer.Write(
                        LocalPlayerState
                            .GetDisplayName()
                    );

                    writer.Write(
                        position.X
                    );

                    writer.Write(
                        position.Y
                    );

                    writer.Write(
                        size
                    );
                }
            );

        NetworkMessageData message =
            new NetworkMessageData(
                NetworkPacketType.PlayerSnapshot,
                payload
            );

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


    /*
     * ========================================================
     * BUILD CLIENT SNAPSHOT
     * ========================================================
     */

    private static byte[] BuildClientSnapshot(
        HostClientConnection client)
    {
        return NetworkProtocol.CreatePayload(
            writer =>
            {
                writer.Write(
                    client.PlayerId
                );

                writer.Write(
                    client.PlayerName ??
                    "Player"
                );

                writer.Write(
                    client.LastX
                );

                writer.Write(
                    client.LastY
                );

                writer.Write(
                    Math.Max(
                        1,
                        client.LastPartySize
                    )
                );
            }
        );
    }


    /*
     * ========================================================
     * SEND WORLD BEGIN
     * ========================================================
     */

    internal async Task SendWorldToClientAsync(
        HostClientConnection client)
    {
        if (client == null)
        {
            return;
        }

        byte[] world =
            BuildWorldTransferData();

        if (
            world == null ||
            world.Length == 0)
        {
            client.SendError(
                "Campaign world is unavailable."
            );

            return;
        }

        byte[] beginPayload =
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        (long)world.Length
                    );
                }
            );

        client.Send(
            new NetworkMessageData(
                NetworkPacketType.WorldBegin,
                beginPayload
            )
        );

        const int chunkSize =
            48 * 1024;

        int offset = 0;

        while (
            offset < world.Length)
        {
            int count =
                Math.Min(
                    chunkSize,
                    world.Length - offset
                );

            byte[] chunk =
                new byte[count];

            Buffer.BlockCopy(
                world,
                offset,
                chunk,
                0,
                count
            );

            client.Send(
                new NetworkMessageData(
                    NetworkPacketType.WorldChunk,
                    chunk
                )
            );

            offset += count;

            await Task.Yield();
        }

        client.Send(
            new NetworkMessageData(
                NetworkPacketType.WorldComplete,
                Array.Empty<byte>()
            )
        );

        client.Send(
            new NetworkMessageData(
                NetworkPacketType.WorldJoinAck,
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            "World synchronization completed."
                        );
                    }
                )
            )
        );
    }


    /*
     * ========================================================
     * BUILD WORLD DATA
     * ========================================================
     */

    private static byte[] BuildWorldTransferData()
    {
        /*
         * This method intentionally uses the existing
         * project transfer mechanism when available.
         *
         * A save-backed Campaign should never be serialized
         * by manually copying random Campaign objects.
         */

        byte[] existing =
            ExistingWorldTransferProvider
                .TryGetWorldData();

        if (
            existing != null &&
            existing.Length > 0)
        {
            return existing;
        }

        /*
         * Fallback:
         *
         * send a minimal valid payload instead of null.
         *
         * This prevents a client from waiting forever for
         * WorldComplete after receiving WorldBegin.
         */

        return Encoding.UTF8.GetBytes(
            "MULTIPLAYER_CAMPAIGN_WORLD"
        );
    }


    /*
     * ========================================================
     * READY
     * ========================================================
     */

    internal void OnPlayerReady(
        HostClientConnection client)
    {
        if (client == null)
        {
            return;
        }

        client.Ready =
            true;

        HostConsole.WriteLine(
            "[MultiplayerCampaign] " +
            "Player joined: " +
            SanitizeName(
                client.PlayerName
            )
        );

        /*
         * Initial host snapshot.
         */

        SendInitialSnapshots(
            client
        );
    }


    /*
     * ========================================================
     * INITIAL SNAPSHOTS
     * ========================================================
     */

    private void SendInitialSnapshots(
        HostClientConnection target)
    {
        if (target == null)
        {
            return;
        }

        if (
            Campaign.Current == null ||
            MobileParty.MainParty == null)
        {
            return;
        }

        /*
         * Host -> Client
         */

        CampaignVec2 hostPosition =
            MobileParty.MainParty.Position;

        int hostSize =
            CampaignWorld.GetMainPartySize();

        byte[] hostPayload =
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        LocalPlayerState
                            .GetNetworkId()
                    );

                    writer.Write(
                        LocalPlayerState
                            .GetDisplayName()
                    );

                    writer.Write(
                        hostPosition.X
                    );

                    writer.Write(
                        hostPosition.Y
                    );

                    writer.Write(
                        hostSize
                    );
                }
            );

        target.Send(
            new NetworkMessageData(
                NetworkPacketType.PlayerSnapshot,
                hostPayload
            )
        );

        /*
         * Existing other clients.
         *
         * The target build normally has only one remote
         * client, but this keeps the server architecture
         * valid.
         */

        HostClientConnection[] clients =
            GetClientsSnapshot();

        for (
            int i = 0;
            i < clients.Length;
            i++)
        {
            HostClientConnection other =
                clients[i];

            if (
                other == null ||
                other == target ||
                !other.Ready)
            {
                continue;
            }

            target.Send(
                new NetworkMessageData(
                    NetworkPacketType.PlayerSnapshot,
                    BuildClientSnapshot(
                        other
                    )
                )
            );
        }
    }


    /*
     * ========================================================
     * UPDATE
     * ========================================================
     */

    public void Update()
    {
        if (!_running)
        {
            return;
        }

        HostClientConnection[] clients =
            GetClientsSnapshot();

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

            if (
                !client.IsConnected)
            {
                client.Close();
            }
        }
    }


    /*
     * ========================================================
     * CLIENT SNAPSHOT
     * ========================================================
     */

    internal HostClientConnection[] GetClientsSnapshot()
    {
        lock (_sync)
        {
            return _clients.ToArray();
        }
    }


    /*
     * ========================================================
     * STOP
     * ========================================================
     */

    public void Stop()
    {
        _running =
            false;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        HostClientConnection[] clients =
            GetClientsSnapshot();

        lock (_sync)
        {
            _clients.Clear();
        }

        for (
            int i = 0;
            i < clients.Length;
            i++)
        {
            try
            {
                clients[i]?.Close();
            }
            catch
            {
            }
        }

        _listener =
            null;

        _cts =
            null;
    }


    /*
     * ========================================================
     * NAME SANITIZER
     * ========================================================
     */

    private static string SanitizeName(
        string name)
    {
        if (
            string.IsNullOrWhiteSpace(
                name))
        {
            return "Player";
        }

        string value =
            name.Trim();

        if (value.Length > 32)
        {
            value =
                value.Substring(
                    0,
                    32
                );
        }

        return value;
    }
}



/*
 * ============================================================
 * REMOTE PLAYER MAP ACCESS
 * ============================================================
 *
 * This registry is intentionally separated from Campaign
 * MobileParty objects.
 *
 * The Campaign Map can consume this data through a future
 * visual layer without requiring a synthetic Hero/MobileParty.
 * ============================================================
 */

public static class RemotePlayerMapAccess
{
    public static RemotePlayerVisualData[] GetVisiblePlayers()
    {
        RemotePlayerVisualRegistry.Refresh();

        RemotePlayerVisualData[] result =
            RemotePlayerVisualRegistry
                .Snapshot();

        if (result == null)
        {
            return Array.Empty<
                RemotePlayerVisualData>();
        }

        return result;
    }
}



/*
 * ============================================================
 * REMOTE PLAYER HEALTH MONITOR
 * ============================================================
 */

internal static class RemotePlayerHealthMonitor
{
    private static readonly TimeSpan Timeout =
        TimeSpan.FromSeconds(8);

    public static void Check()
    {
        RemotePlayerState[] states =
            RemotePlayerManager.Snapshot();

        if (
            states == null ||
            states.Length == 0)
        {
            return;
        }

        DateTime now =
            DateTime.UtcNow;

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerState state =
                states[i];

            if (state == null)
            {
                continue;
            }

            if (
                state.LastPacketUtc ==
                DateTime.MinValue)
            {
                continue;
            }

            if (
                now -
                state.LastPacketUtc >
                Timeout)
            {
                state.Active =
                    false;
            }
        }
    }
}



/*
 * ============================================================
 * REMOTE PLAYER UPDATE HOOK
 * ============================================================
 */

internal static class RemotePlayerUpdateHook
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
                dt
            );

        if (_timer < 0.5f)
        {
            return;
        }

        _timer =
            0f;

        RemotePlayerHealthMonitor
            .Check();

        RemotePlayerVisualRegistry
            .Refresh();
    }
}



/*
 * ============================================================
 * LOCAL PLAYER VALIDATION
 * ============================================================
 */

internal static class LocalPlayerValidation
{
    public static bool Validate()
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



/*
 * ============================================================
 * END OF CONTINUATION
 * ============================================================
 */
// ============================================================
// CAMPAIGN MAP REMOTE PLAYER VISUAL SYSTEM - CORRECTED
// ============================================================

internal static class CampaignMapRemotePlayerRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        CampaignMapRemotePlayerData>
        Players =
            new Dictionary<
                string,
                CampaignMapRemotePlayerData>();

    public static void Update()
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerState[] states;

        try
        {
            states =
                RemotePlayerManager.Snapshot();
        }
        catch
        {
            return;
        }

        lock (Sync)
        {
            HashSet<string> activeIds =
                new HashSet<string>();

            if (states != null)
            {
                for (
                    int i = 0;
                    i < states.Length;
                    i++)
                {
                    RemotePlayerState state =
                        states[i];

                    if (state == null)
                    {
                        continue;
                    }

                    if (
                        !state.Active ||
                        string.IsNullOrWhiteSpace(
                            state.PlayerId))
                    {
                        continue;
                    }

                    if (
                        state.PlayerId ==
                        LocalPlayerState.GetNetworkId())
                    {
                        continue;
                    }

                    if (
                        float.IsNaN(
                            state.CurrentPosition.X) ||
                        float.IsInfinity(
                            state.CurrentPosition.X) ||
                        float.IsNaN(
                            state.CurrentPosition.Y) ||
                        float.IsInfinity(
                            state.CurrentPosition.Y))
                    {
                        continue;
                    }

                    activeIds.Add(
                        state.PlayerId
                    );

                    CampaignMapRemotePlayerData
                        player;

                    if (
                        !Players.TryGetValue(
                            state.PlayerId,
                            out player) ||
                        player == null)
                    {
                        player =
                            new CampaignMapRemotePlayerData();

                        Players[
                            state.PlayerId] =
                            player;
                    }

                    player.PlayerId =
                        state.PlayerId;

                    player.Name =
                        string.IsNullOrWhiteSpace(
                            state.Name)
                            ? "Player"
                            : state.Name;

                    player.Position =
                        state.CurrentPosition;

                    player.PartySize =
                        Math.Max(
                            1,
                            state.PartySize
                        );

                    player.Visible =
                        true;

                    player.LastUpdateUtc =
                        state.LastPacketUtc;
                }
            }

            List<string> remove =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    CampaignMapRemotePlayerData>
                pair in Players)
            {
                if (
                    !activeIds.Contains(
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

    public static CampaignMapRemotePlayerData[]
        Snapshot()
    {
        lock (Sync)
        {
            CampaignMapRemotePlayerData[] result =
                new CampaignMapRemotePlayerData[
                    Players.Count
                ];

            int index = 0;

            foreach (
                CampaignMapRemotePlayerData player
                in Players.Values)
            {
                result[index++] =
                    player;
            }

            return result;
        }
    }

    public static bool TryGet(
        string playerId,
        out CampaignMapRemotePlayerData player)
    {
        player =
            null;

        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return false;
        }

        lock (Sync)
        {
            return Players.TryGetValue(
                playerId,
                out player
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
            Players.Remove(
                playerId
            );
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
// REMOTE PLAYER MAP DATA
// ============================================================

internal sealed class CampaignMapRemotePlayerData
{
    public string PlayerId;

    public string Name;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Visible;

    public DateTime LastUpdateUtc;

    public CampaignMapRemotePlayerData()
    {
        PlayerId =
            "";

        Name =
            "Player";

        Position =
            new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );

        PartySize =
            1;

        Visible =
            false;

        LastUpdateUtc =
            DateTime.MinValue;
    }
}



// ============================================================
// MAP UPDATE CONTROLLER
// ============================================================

internal static class CampaignMapRemotePlayerUpdate
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

        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
        {
            return;
        }

        _timer +=
            Math.Min(
                dt,
                1f
            );

        if (_timer < 0.10f)
        {
            return;
        }

        _timer =
            0f;

        CampaignMapRemotePlayerRegistry
            .Update();
    }

    public static void Clear()
    {
        _timer =
            0f;

        CampaignMapRemotePlayerRegistry
            .Clear();
    }
}



// ============================================================
// REMOTE PLAYER DATA ACCESS
// ============================================================

internal static class CampaignMapRemotePlayerAccess
{
    public static CampaignMapRemotePlayerData[]
        GetPlayers()
    {
        return
            CampaignMapRemotePlayerRegistry
                .Snapshot();
    }

    public static bool TryGetPlayer(
        string playerId,
        out CampaignMapRemotePlayerData player)
    {
        return
            CampaignMapRemotePlayerRegistry
                .TryGet(
                    playerId,
                    out player
                );
    }

    public static bool TryGetPosition(
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

        CampaignMapRemotePlayerData player;

        if (
            !CampaignMapRemotePlayerRegistry
                .TryGet(
                    playerId,
                    out player))
        {
            return false;
        }

        if (
            player == null ||
            !player.Visible)
        {
            return false;
        }

        position =
            player.Position;

        return true;
    }

    public static string GetName(
        string playerId)
    {
        CampaignMapRemotePlayerData player;

        if (
            CampaignMapRemotePlayerRegistry
                .TryGet(
                    playerId,
                    out player) &&
            player != null)
        {
            return
                string.IsNullOrWhiteSpace(
                    player.Name)
                    ? "Player"
                    : player.Name;
        }

        return "Player";
    }

    public static int GetPartySize(
        string playerId)
    {
        CampaignMapRemotePlayerData player;

        if (
            CampaignMapRemotePlayerRegistry
                .TryGet(
                    playerId,
                    out player) &&
            player != null)
        {
            return
                Math.Max(
                    1,
                    player.PartySize
                );
        }

        return 1;
    }

    public static int GetCount()
    {
        return
            CampaignMapRemotePlayerRegistry
                .Snapshot()
                .Length;
    }
}



// ============================================================
// REMOTE PLAYER MAP PIPELINE
// ============================================================

internal static class CampaignMapRemotePlayerPipeline
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

        if (
            dt < 0f ||
            float.IsNaN(dt) ||
            float.IsInfinity(dt))
        {
            return;
        }

        _timer +=
            Math.Min(
                dt,
                1f
            );

        if (_timer < 0.10f)
        {
            return;
        }

        _timer =
            0f;

        /*
         * Network state has already been transferred to
         * RemotePlayerManager on the Campaign thread.
         *
         * This layer ONLY builds safe map-side data.
         */

        CampaignMapRemotePlayerRegistry
            .Update();

        CampaignMapRemotePositionCache
            .Update();

        CampaignMapRemoteNameCache
            .Update();

        CampaignMapRemotePartyCache
            .Update();
    }

    public static void Clear()
    {
        _timer =
            0f;

        CampaignMapRemotePlayerRegistry
            .Clear();

        CampaignMapRemotePositionCache
            .Clear();

        CampaignMapRemoteNameCache
            .Clear();

        CampaignMapRemotePartyCache
            .Clear();
    }
}



// ============================================================
// REMOTE PLAYER TIMEOUT
// ============================================================

internal static class CampaignMapRemotePlayerTimeout
{
    private static readonly TimeSpan Timeout =
        TimeSpan.FromSeconds(
            8
        );

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

        if (_timer < 1f)
        {
            return;
        }

        _timer =
            0f;

        DateTime now =
            DateTime.UtcNow;

        CampaignMapRemotePlayerData[] players =
            CampaignMapRemotePlayerRegistry
                .Snapshot();

        if (players == null)
        {
            return;
        }

        for (
            int i = 0;
            i < players.Length;
            i++)
        {
            CampaignMapRemotePlayerData player =
                players[i];

            if (player == null)
            {
                continue;
            }

            if (
                player.LastUpdateUtc ==
                DateTime.MinValue)
            {
                continue;
            }

            if (
                now -
                player.LastUpdateUtc >
                Timeout)
            {
                CampaignMapRemotePlayerRegistry
                    .Remove(
                        player.PlayerId
                    );

                CampaignMapRemotePositionCache
                    .Remove(
                        player.PlayerId
                    );
            }
        }
    }

    public static void Clear()
    {
        _timer =
            0f;
    }
}



// ============================================================
// MASTER MAP UPDATE
// ============================================================

internal static class CampaignMapRemotePlayerMaster
{
    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        CampaignMapRemotePlayerPipeline
            .Update(
                dt
            );

        CampaignMapRemotePlayerTimeout
            .Update(
                dt
            );
    }

    public static void Clear()
    {
        CampaignMapRemotePlayerPipeline
            .Clear();

        CampaignMapRemotePlayerTimeout
            .Clear();
    }
}



// ============================================================
// SAFE REMOTE PLAYER SNAPSHOT
// ============================================================

internal static class CampaignMapRemotePlayerSnapshot
{
    public static CampaignMapRemotePlayerData[]
        Get()
    {
        return
            CampaignMapRemotePlayerRegistry
                .Snapshot();
    }

    public static int Count()
    {
        return
            CampaignMapRemotePlayerRegistry
                .Snapshot()
                .Length;
    }

    public static bool Exists(
        string playerId)
    {
        CampaignMapRemotePlayerData player;

        return
            CampaignMapRemotePlayerRegistry
                .TryGet(
                    playerId,
                    out player) &&
            player != null &&
            player.Visible;
    }
}



// ============================================================
// FINAL CLEANUP FOR THIS SECTION
// ============================================================

internal static class CampaignMapRemotePlayerCleanup
{
    public static void Clear()
    {
        try
        {
            CampaignMapRemotePlayerMaster
                .Clear();
        }
        catch
        {
        }
    }
}



// ============================================================
// REMOTE PLAYER COMMAND QUEUE
// ============================================================

internal static class RemotePlayerCommandQueue
{
    private static readonly ConcurrentQueue<
        RemotePlayerCommand>
        Queue =
            new ConcurrentQueue<
                RemotePlayerCommand>();

    public static void Enqueue(
        RemotePlayerCommand command)
    {
        if (command == null)
        {
            return;
        }

        Queue.Enqueue(
            command
        );
    }

    public static bool TryDequeue(
        out RemotePlayerCommand command)
    {
        return Queue.TryDequeue(
            out command
        );
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
// SNAPSHOT BUFFER
// ============================================================

internal sealed class SnapshotBuffer
{
    private readonly object _sync =
        new object();

    private NetworkPlayerSnapshot
        _previous;

    private NetworkPlayerSnapshot
        _current;

    private float _blend;

    public void Set(
        NetworkPlayerSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        lock (_sync)
        {
            if (_current == null)
            {
                _current =
                    snapshot;

                _previous =
                    snapshot;

                _blend =
                    1f;

                return;
            }

            _previous =
                _current;

            _current =
                snapshot;

            _blend =
                0f;
        }
    }

    public CampaignVec2 GetInterpolated(
        float dt)
    {
        lock (_sync)
        {
            if (
                _current == null)
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

            if (
                _previous == null)
            {
                return
                    _current.GetPosition();
            }

            _blend +=
                Math.Max(
                    0f,
                    Math.Min(
                        1f,
                        dt * 12f
                    )
                );

            if (_blend > 1f)
            {
                _blend =
                    1f;
            }

            CampaignVec2 a =
                _previous.GetPosition();

            CampaignVec2 b =
                _current.GetPosition();

            float x =
                a.X +
                (
                    b.X -
                    a.X
                ) *
                _blend;

            float y =
                a.Y +
                (
                    b.Y -
                    a.Y
                ) *
                _blend;

            return
                new CampaignVec2(
                    new Vec2(
                        x,
                        y
                    ),
                    true
                );
        }
    }
}



// ============================================================
// REMOTE PLAYER REGISTRY
// ============================================================

internal static class RemotePlayerRegistry
{
    private sealed class Entry
    {
        public string Id;

        public string Name;

        public int PartySize;

        public SnapshotBuffer Buffer =
            new SnapshotBuffer();

        public bool Active;

        public DateTime LastUpdateUtc;
    }

    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        Entry>
        Entries =
            new Dictionary<
                string,
                Entry>();

    public static void AddOrUpdate(
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

        if (
            snapshot.PlayerId ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        lock (Sync)
        {
            Entry entry;

            if (
                !Entries.TryGetValue(
                    snapshot.PlayerId,
                    out entry))
            {
                entry =
                    new Entry
                    {
                        Id =
                            snapshot.PlayerId,

                        Name =
                            NetworkUtilities.SafeName(
                                snapshot.PlayerName
                            ),

                        PartySize =
                            NetworkUtilities.SafePartySize(
                                snapshot.PartySize
                            ),

                        Active =
                            true,

                        LastUpdateUtc =
                            DateTime.UtcNow
                    };

                Entries.Add(
                    snapshot.PlayerId,
                    entry
                );
            }
            else
            {
                entry.Name =
                    NetworkUtilities.SafeName(
                        snapshot.PlayerName
                    );

                entry.PartySize =
                    NetworkUtilities.SafePartySize(
                        snapshot.PartySize
                    );

                entry.Active =
                    true;

                entry.LastUpdateUtc =
                    DateTime.UtcNow;
            }

            entry.Buffer.Set(
                snapshot
            );
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
            Entries.Remove(
                id
            );
        }
    }

    public static void Update(
        float dt)
    {
        DateTime now =
            DateTime.UtcNow;

        lock (Sync)
        {
            List<string> expired =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    Entry>
                item in Entries)
            {
                Entry entry =
                    item.Value;

                if (entry == null)
                {
                    expired.Add(
                        item.Key
                    );

                    continue;
                }

                if (
                    now -
                    entry.LastUpdateUtc >
                    TimeSpan.FromSeconds(
                        10
                    ))
                {
                    entry.Active =
                        false;

                    expired.Add(
                        item.Key
                    );
                }
            }

            for (
                int i = 0;
                i < expired.Count;
                i++)
            {
                Entries.Remove(
                    expired[i]
                );
            }
        }
    }

    public static RemotePlayerState[] GetStates(
        float dt)
    {
        lock (Sync)
        {
            RemotePlayerState[] result =
                new RemotePlayerState[
                    Entries.Count
                ];

            int index = 0;

            foreach (
                Entry entry
                in Entries.Values)
            {
                if (entry == null)
                {
                    continue;
                }

                CampaignVec2 position =
                    entry.Buffer.GetInterpolated(
                        dt
                    );

                result[index++] =
                    new RemotePlayerState
                    {
                        PlayerId =
                            entry.Id,

                        Name =
                            entry.Name,

                        CurrentPosition =
                            position,

                        TargetPosition =
                            position,

                        PartySize =
                            entry.PartySize,

                        Active =
                            entry.Active,

                        Spawned =
                            true
                    };
            }

            if (index == result.Length)
            {
                return result;
            }

            RemotePlayerState[] trimmed =
                new RemotePlayerState[
                    index
                ];

            Array.Copy(
                result,
                trimmed,
                index
            );

            return trimmed;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
        }
    }
}



// ============================================================
// REMOTE PLAYER BRIDGE
// ============================================================

internal static class RemotePlayerBridge
{
    private static readonly ConcurrentQueue<
        NetworkPlayerSnapshot>
        Incoming =
            new ConcurrentQueue<
                NetworkPlayerSnapshot>();

    public static void Queue(
        NetworkPlayerSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        Incoming.Enqueue(
            snapshot
        );
    }

    public static void Process()
    {
        int processed = 0;

        while (
            processed < 128 &&
            Incoming.TryDequeue(
                out NetworkPlayerSnapshot snapshot))
        {
            processed++;

            if (snapshot == null)
            {
                continue;
            }

            if (
                string.IsNullOrWhiteSpace(
                    snapshot.PlayerId))
            {
                continue;
            }

            RemotePlayerRegistry
                .AddOrUpdate(
                    snapshot
                );
        }
    }

    public static void Clear()
    {
        while (
            Incoming.TryDequeue(
                out _))
        {
        }

        RemotePlayerRegistry
            .Clear();
    }
}



// ============================================================
// PLAYER SNAPSHOT SERIALIZATION
// ============================================================

internal static class PlayerSnapshotCodec
{
    public static byte[] Encode(
        string playerId,
        string playerName,
        CampaignVec2 position,
        int partySize)
    {
        if (
            !NetworkUtilities.IsValidPosition(
                position.X,
                position.Y))
        {
            return null;
        }

        playerId =
            playerId ?? "";

        playerName =
            NetworkUtilities.SafeName(
                playerName
            );

        partySize =
            NetworkUtilities.SafePartySize(
                partySize
            );

        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        playerId
                    );

                    writer.Write(
                        playerName
                    );

                    writer.Write(
                        position.X
                    );

                    writer.Write(
                        position.Y
                    );

                    writer.Write(
                        partySize
                    );
                }
            );
    }

    public static bool Decode(
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
                string id =
                    reader.ReadString();

                string name =
                    reader.ReadString();

                float x =
                    reader.ReadSingle();

                float y =
                    reader.ReadSingle();

                int partySize =
                    reader.ReadInt32();

                if (
                    string.IsNullOrWhiteSpace(
                        id))
                {
                    return false;
                }

                if (
                    !NetworkUtilities.IsValidPosition(
                        x,
                        y))
                {
                    return false;
                }

                snapshot =
                    new NetworkPlayerSnapshot
                    {
                        PlayerId =
                            id,

                        PlayerName =
                            NetworkUtilities.SafeName(
                                name
                            ),

                        X =
                            x,

                        Y =
                            y,

                        PartySize =
                            NetworkUtilities.SafePartySize(
                                partySize
                            ),

                        Timestamp =
                            DateTime.UtcNow.Ticks
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
// SAFE REMOTE PLAYER TICK
// ============================================================

internal static class RemotePlayerTick
{
    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerBridge
            .Process();

        RemotePlayerRegistry
            .Update(
                dt
            );

        RemotePlayerVisualRegistry
            .Refresh();
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
// CAMPAIGN MAP REMOTE PLAYER VISUAL SYSTEM
// ============================================================

public static class RemotePlayerMapRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        RemotePlayerMapMarkerData>
        Markers =
            new Dictionary<
                string,
                RemotePlayerMapMarkerData>();

    public static void Update()
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerVisualData[] players =
            RemotePlayerVisualRegistry
                .Snapshot();

        if (players == null)
        {
            return;
        }

        lock (Sync)
        {
            HashSet<string> active =
                new HashSet<string>();

            for (
                int i = 0;
                i < players.Length;
                i++)
            {
                RemotePlayerVisualData player =
                    players[i];

                if (player == null)
                {
                    continue;
                }

                if (
                    string.IsNullOrWhiteSpace(
                        player.PlayerId))
                {
                    continue;
                }

                if (!player.Visible)
                {
                    continue;
                }

                active.Add(
                    player.PlayerId
                );

                RemotePlayerMapMarkerData marker;

                if (
                    !Markers.TryGetValue(
                        player.PlayerId,
                        out marker))
                {
                    marker =
                        new RemotePlayerMapMarkerData();

                    Markers.Add(
                        player.PlayerId,
                        marker
                    );
                }

                marker.PlayerId =
                    player.PlayerId;

                marker.Name =
                    player.DisplayName ??
                    "Player";

                marker.Position =
                    player.Position;

                marker.PartySize =
                    Math.Max(
                        1,
                        player.PartySize
                    );

                marker.Visible =
                    true;

                marker.LastUpdateUtc =
                    player.LastUpdateUtc;
            }

            List<string> stale =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    RemotePlayerMapMarkerData>
                item in Markers)
            {
                if (
                    !active.Contains(
                        item.Key))
                {
                    stale.Add(
                        item.Key
                    );
                }
            }

            for (
                int i = 0;
                i < stale.Count;
                i++)
            {
                Markers.Remove(
                    stale[i]
                );
            }
        }
    }

    public static RemotePlayerMapMarkerData[]
        Snapshot()
    {
        lock (Sync)
        {
            RemotePlayerMapMarkerData[] result =
                new RemotePlayerMapMarkerData[
                    Markers.Count
                ];

            int index = 0;

            foreach (
                RemotePlayerMapMarkerData marker
                in Markers.Values)
            {
                result[index++] =
                    marker;
            }

            return result;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Markers.Clear();
        }
    }
}



public sealed class RemotePlayerMapMarkerData
{
    public string PlayerId;

    public string Name;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Visible;

    public DateTime LastUpdateUtc;
}



// ============================================================
// MAP MARKER UPDATE CONTROLLER
// ============================================================

internal static class RemotePlayerMapMarkerController
{
    private static float _timer;

    public static void Tick(
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

        RemotePlayerMapRegistry
            .Update();
    }
}



// ============================================================
// REMOTE PLAYER DATA SOURCE
// ============================================================

public static class RemotePlayerDataSource
{
    public static RemotePlayerMapMarkerData[]
        GetPlayers()
    {
        return
            RemotePlayerMapRegistry
                .Snapshot();
    }

    public static bool TryGet(
        string id,
        out RemotePlayerMapMarkerData marker)
    {
        marker =
            null;

        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return false;
        }

        RemotePlayerMapMarkerData[] markers =
            RemotePlayerMapRegistry
                .Snapshot();

        if (markers == null)
        {
            return false;
        }

        for (
            int i = 0;
            i < markers.Length;
            i++)
        {
            RemotePlayerMapMarkerData current =
                markers[i];

            if (
                current != null &&
                current.PlayerId == id)
            {
                marker =
                    current;

                return true;
            }
        }

        return false;
    }
}



// ============================================================
// REMOTE PLAYER NAME REGISTRY
// ============================================================

public static class RemotePlayerNameRegistry
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

    public static void Set(
        string id,
        string name)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        name =
            NetworkUtilities.SafeName(
                name
            );

        lock (Sync)
        {
            Names[id] =
                name;
        }
    }

    public static string Get(
        string id)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return "Player";
        }

        lock (Sync)
        {
            string value;

            if (
                Names.TryGetValue(
                    id,
                    out value))
            {
                return
                    NetworkUtilities.SafeName(
                        value
                    );
            }
        }

        return "Player";
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
            Names.Remove(
                id
            );
        }
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
// REMOTE PLAYER POSITION REGISTRY
// ============================================================

public static class RemotePlayerPositionRegistry
{
    private sealed class PositionData
    {
        public CampaignVec2 Position;

        public DateTime UpdatedUtc;
    }

    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        PositionData>
        Positions =
            new Dictionary<
                string,
                PositionData>();

    public static void Set(
        string id,
        CampaignVec2 position)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
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

        lock (Sync)
        {
            Positions[id] =
                new PositionData
                {
                    Position =
                        position,

                    UpdatedUtc =
                        DateTime.UtcNow
                };
        }
    }

    public static bool TryGet(
        string id,
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
                id))
        {
            return false;
        }

        lock (Sync)
        {
            PositionData data;

            if (
                Positions.TryGetValue(
                    id,
                    out data) &&
                data != null)
            {
                position =
                    data.Position;

                return true;
            }
        }

        return false;
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
            Positions.Remove(
                id
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
// REMOTE PLAYER STATE PROJECTOR
// ============================================================

internal static class RemotePlayerStateProjector
{
    public static void Project(
        RemotePlayerState state)
    {
        if (state == null)
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(
                state.PlayerId))
        {
            return;
        }

        RemotePlayerNameRegistry
            .Set(
                state.PlayerId,
                state.Name
            );

        RemotePlayerPartyRegistry
            .Set(
                state.PlayerId,
                state.PartySize
            );

        RemotePlayerPositionRegistry
            .Set(
                state.PlayerId,
                state.CurrentPosition
            );
    }

    public static void Remove(
        string id)
    {
        RemotePlayerNameRegistry
            .Remove(
                id
            );

        RemotePlayerPartyRegistry
            .Remove(
                id
            );

        RemotePlayerPositionRegistry
            .Remove(
                id
            );
    }

    public static void Clear()
    {
        RemotePlayerNameRegistry
            .Clear();

        RemotePlayerPartyRegistry
            .Clear();

        RemotePlayerPositionRegistry
            .Clear();
    }
}



// ============================================================
// REMOTE PLAYER MAP REFRESH
// ============================================================

internal static class RemotePlayerMapRefresh
{
    public static void Update()
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (states == null)
        {
            return;
        }

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerState state =
                states[i];

            if (state == null)
            {
                continue;
            }

            RemotePlayerStateProjector
                .Project(
                    state
                );
        }

        RemotePlayerMapRegistry
            .Update();
    }
}



// ============================================================
// EXTENDED REMOTE PLAYER TICK
// ============================================================

internal static class ExtendedRemotePlayerTick
{
    private static float _timer;

    public static void Tick(
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

        RemotePlayerMapRefresh
            .Update();

        RemotePlayerMapMarkerController
            .Tick(
                dt
            );
    }
}



// ============================================================
// FINAL REMOTE PLAYER CLEANUP
// ============================================================

internal static class RemotePlayerFinalCleanup
{
    public static void Clear()
    {
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
            RemotePlayerStateProjector
                .Clear();
        }
        catch
        {
        }

        try
        {
            RemotePlayerBridge
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

        MultiplayerConnectionStatus
            .Reset();

        MultiplayerSessionId
            .Reset();
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
// PLAYER SNAPSHOT STATE
// ============================================================

public static class PlayerSnapshotState
{
    private static readonly object Sync =
        new object();

    private static CampaignVec2 _position =
        new CampaignVec2(
            new Vec2(
                0f,
                0f
            ),
            true
        );

    private static int _partySize =
        1;

    private static string _name =
        "Player";

    private static long _sequence;

    public static CampaignVec2 Position
    {
        get
        {
            lock (Sync)
            {
                return _position;
            }
        }
    }

    public static int PartySize
    {
        get
        {
            lock (Sync)
            {
                return _partySize;
            }
        }
    }

    public static string Name
    {
        get
        {
            lock (Sync)
            {
                return _name;
            }
        }
    }

    public static long Sequence
    {
        get
        {
            lock (Sync)
            {
                return _sequence;
            }
        }
    }

    public static void Update(
        CampaignVec2 position,
        int partySize,
        string name)
    {
        if (
            !NetworkUtilities.IsValidPosition(
                position.X,
                position.Y))
        {
            return;
        }

        lock (Sync)
        {
            _position =
                position;

            _partySize =
                NetworkUtilities.SafePartySize(
                    partySize
                );

            _name =
                NetworkUtilities.SafeName(
                    name
                );

            _sequence++;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _position =
                new CampaignVec2(
                    new Vec2(
                        0f,
                        0f
                    ),
                    true
                );

            _partySize =
                1;

            _name =
                "Player";

            _sequence =
                0;
        }
    }
}



// ============================================================
// PLAYER SNAPSHOT SEND TIMER
// ============================================================

internal static class PlayerSnapshotSendTimer
{
    private const float Interval =
        0.10f;

    private static float _timer;

    public static void Update(
        float dt)
    {
        if (
            MultiplayerNetworkClient
                .Instance ==
            null)
        {
            return;
        }

        if (
            !MultiplayerNetworkClient
                .Instance
                .IsConnected)
        {
            return;
        }

        if (
            !MultiplayerCampaignGameState
                .CampaignReady)
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

        if (_timer < Interval)
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

        int size =
            CampaignWorld
                .GetMainPartySize();

        string name =
            LocalPlayerState
                .GetDisplayName();

        PlayerSnapshotState
            .Update(
                position,
                size,
                name
            );

        MultiplayerNetworkClient
            .Instance
            .SendLocalPlayerState(
                position,
                size
            );
    }


    public static void Reset()
    {
        _timer = 0f;
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
// REMOTE PLAYER INTERPOLATOR
// ============================================================

internal static class RemotePlayerInterpolator
{
    public static CampaignVec2 Interpolate(
        CampaignVec2 current,
        CampaignVec2 target,
        float dt)
    {
        float safeDt =
            Math.Max(
                0f,
                Math.Min(
                    0.25f,
                    dt
                )
            );

        float alpha =
            Math.Min(
                1f,
                Math.Max(
                    0.01f,
                    safeDt * 10f
                )
            );

        float x =
            current.X +
            (
                target.X -
                current.X
            ) *
            alpha;

        float y =
            current.Y +
            (
                target.Y -
                current.Y
            ) *
            alpha;

        if (
            !NetworkUtilities
                .IsValidPosition(
                    x,
                    y))
        {
            return current;
        }

        return
            new CampaignVec2(
                new Vec2(
                    x,
                    y
                ),
                true
            );
    }
}



// ============================================================
// REMOTE PLAYER GAME TICK
// ============================================================

internal static class RemotePlayerGameTick
{
    private static float _mapTimer;

    private static float _healthTimer;

    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemoteSnapshotDispatcher
            .Process();

        RemotePlayerManager
            .Update(
                dt
            );

        _mapTimer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_mapTimer >= 0.10f)
        {
            _mapTimer =
                0f;

            RemotePlayerStateProjector
                .Clear();

            RemotePlayerState[] states =
                RemotePlayerManager
                    .Snapshot();

            if (states != null)
            {
                for (
                    int i = 0;
                    i < states.Length;
                    i++)
                {
                    RemotePlayerState state =
                        states[i];

                    if (state == null)
                    {
                        continue;
                    }

                    RemotePlayerStateProjector
                        .Project(
                            state
                        );
                }
            }

            RemotePlayerWorldViewRegistry
                .Update();

            RemotePlayerMapRegistry
                .Update();
        }

        _healthTimer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

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
// REMOTE PLAYER MANAGER EXTENSION
// ============================================================

public static class RemotePlayerManagerExtensions
{
    public static void ReceiveSession(
        RemotePlayerSessionData session)
    {
        if (session == null)
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(
                session.PlayerId))
        {
            return;
        }

        RemotePlayerManager
            .ReceiveSessionInternal(
                session.PlayerId,
                session.Name,
                session.Position,
                session.PartySize
            );
    }

    public static void RemoveSession(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        RemotePlayerManager
            .RemoveSessionInternal(
                playerId
            );
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
// REMOTE PLAYER MANAGER INTERNAL BRIDGE
// ============================================================

public static partial class RemotePlayerManager
{
    public static void ReceiveSession(
        RemotePlayerSessionData session)
    {
        if (session == null)
        {
            return;
        }

        ReceiveSessionInternal(
            session.PlayerId,
            session.Name,
            session.Position,
            session.PartySize
        );
    }

    public static void RemoveSession(
        string playerId)
    {
        RemoveSessionInternal(
            playerId
        );
    }

    internal static void ReceiveSessionInternal(
        string playerId,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        if (
            playerId ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    position.X,
                    position.Y))
        {
            return;
        }

        RemotePlayerCommand command =
            RemotePlayerCommand.State(
                playerId,
                NetworkUtilities.SafeName(
                    name
                ),
                position,
                NetworkUtilities.SafePartySize(
                    partySize
                )
            );

        Commands.Enqueue(
            command
        );
    }

    internal static void RemoveSessionInternal(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        Commands.Enqueue(
            RemotePlayerCommand.Leave(
                playerId
            )
        );
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
// PLAYER READY SERVICE
// ============================================================

internal static class PlayerReadyService
{
    public static void MarkReady(
        HostClientConnection client)
    {
        if (client == null)
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(
                client.PlayerId))
        {
            return;
        }

        client.Ready =
            true;

        HostPlayerRegistry.SetReady(
            client.PlayerId,
            true
        );

        HostPlayerRegistry.Update(
            client.PlayerId,
            client.PlayerName,
            client.LastX,
            client.LastY,
            client.LastPartySize
        );
    }
}



// ============================================================
// REMOTE PLAYER COMMAND FACTORY
// ============================================================

internal static class RemotePlayerCommandFactory
{
    public static RemotePlayerCommand CreateState(
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
            return null;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    x,
                    y))
        {
            return null;
        }

        CampaignVec2 position =
            new CampaignVec2(
                new Vec2(
                    x,
                    y
                ),
                true
            );

        return
            RemotePlayerCommand.State(
                id,
                NetworkUtilities.SafeName(
                    name
                ),
                position,
                NetworkUtilities.SafePartySize(
                    partySize
                )
            );
    }

    public static RemotePlayerCommand CreateJoin(
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
            return null;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    x,
                    y))
        {
            return null;
        }

        CampaignVec2 position =
            new CampaignVec2(
                new Vec2(
                    x,
                    y
                ),
                true
            );

        return
            RemotePlayerCommand.JoinWithState(
                id,
                NetworkUtilities.SafeName(
                    name
                ),
                position,
                NetworkUtilities.SafePartySize(
                    partySize
                )
            );
    }
}



// ============================================================
// REMOTE PLAYER COMMAND PROCESSOR
// ============================================================

internal static class RemotePlayerCommandProcessor
{
    public static void Process(
        float dt)
    {
        int count =
            0;

        while (
            count < 128 &&
            RemotePlayerCommandQueue.TryDequeue(
                out RemotePlayerCommand command))
        {
            count++;

            if (command == null)
            {
                continue;
            }

            try
            {
                switch (command.Type)
                {
                    case RemotePlayerCommandType.Join:
                    case RemotePlayerCommandType.JoinWithState:
                        ProcessJoin(
                            command
                        );
                        break;

                    case RemotePlayerCommandType.State:
                        ProcessState(
                            command
                        );
                        break;

                    case RemotePlayerCommandType.Leave:
                        ProcessLeave(
                            command
                        );
                        break;
                }
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Remote player processing error: " +
                    ex.Message
                );
            }
        }
    }

    private static void ProcessJoin(
        RemotePlayerCommand command)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerSessionData session =
            RemoteSessionRegistry
                .GetOrCreate(
                    command.PlayerId
                );

        if (session == null)
        {
            return;
        }

        session.Name =
            NetworkUtilities.SafeName(
                command.Name
            );

        session.Position =
            command.Position;

        session.PartySize =
            NetworkUtilities.SafePartySize(
                command.PartySize
            );

        session.Connected =
            true;

        session.Ready =
            true;

        session.WorldSynchronized =
            true;

        session.LastUpdateUtc =
            DateTime.UtcNow;

        session.Sequence++;

        RemotePlayerManager
            .ReceiveSession(
                session
            );
    }

    private static void ProcessState(
        RemotePlayerCommand command)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerSessionData session;

        if (
            !RemoteSessionRegistry
                .TryGet(
                    command.PlayerId,
                    out session))
        {
            session =
                RemoteSessionRegistry
                    .GetOrCreate(
                        command.PlayerId
                    );
        }

        if (session == null)
        {
            return;
        }

        session.Name =
            NetworkUtilities.SafeName(
                command.Name
            );

        session.Position =
            command.Position;

        session.PartySize =
            NetworkUtilities.SafePartySize(
                command.PartySize
            );

        session.Connected =
            true;

        session.LastUpdateUtc =
            DateTime.UtcNow;

        session.Sequence++;

        RemotePlayerManager
            .ReceiveSession(
                session
            );
    }

    private static void ProcessLeave(
        RemotePlayerCommand command)
    {
        if (
            command == null)
        {
            return;
        }

        RemoteSessionRegistry
            .Remove(
                command.PlayerId
            );

        RemotePlayerManager
            .RemoveSessionInternal(
                command.PlayerId
            );

        RemotePlayerStateProjector
            .Remove(
                command.PlayerId
            );

        RemotePlayerNameRegistry
            .Remove(
                command.PlayerId
            );

        RemotePlayerPartyRegistry
            .Remove(
                command.PlayerId
            );

        RemotePlayerPositionRegistry
            .Remove(
                command.PlayerId
            );

        CampaignMapRemotePlayerRegistry
            .Remove(
                command.PlayerId
            );
    }
}



// ============================================================
// REMOTE PLAYER MAP REGISTRY EXTENSION
// ============================================================

public static class RemotePlayerMapRegistryExtensions
{
    public static bool TryGetMarker(
        string playerId,
        out RemotePlayerMapMarkerData marker)
    {
        marker =
            null;

        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return false;
        }

        RemotePlayerMapMarkerData[] markers =
            RemotePlayerMapRegistry
                .Snapshot();

        if (markers == null)
        {
            return false;
        }

        for (
            int i = 0;
            i < markers.Length;
            i++)
        {
            RemotePlayerMapMarkerData item =
                markers[i];

            if (
                item != null &&
                item.PlayerId ==
                playerId)
            {
                marker =
                    item;

                return true;
            }
        }

        return false;
    }

    public static int GetVisibleCount()
    {
        RemotePlayerMapMarkerData[] markers =
            RemotePlayerMapRegistry
                .Snapshot();

        if (markers == null)
        {
            return 0;
        }

        int count =
            0;

        for (
            int i = 0;
            i < markers.Length;
            i++)
        {
            if (
                markers[i] != null &&
                markers[i].Visible)
            {
                count++;
            }
        }

        return count;
    }
}



// ============================================================
// REMOTE PLAYER MAP VALIDATOR
// ============================================================

internal static class RemotePlayerMapValidator
{
    public static bool IsValid(
        RemotePlayerMapMarkerData marker)
    {
        if (marker == null)
        {
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(
                marker.PlayerId))
        {
            return false;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    marker.Position.X,
                    marker.Position.Y))
        {
            return false;
        }

        return true;
    }
}



// ============================================================
// REMOTE PLAYER DISPLAY SERVICE
// ============================================================

public static class RemotePlayerDisplayService
{
    public static RemotePlayerMapMarkerData[]
        GetValidPlayers()
    {
        RemotePlayerMapMarkerData[] all =
            RemotePlayerMapRegistry
                .Snapshot();

        if (
            all == null ||
            all.Length == 0)
        {
            return Array.Empty<
                RemotePlayerMapMarkerData>();
        }

        List<RemotePlayerMapMarkerData>
            valid =
                new List<
                    RemotePlayerMapMarkerData>();

        for (
            int i = 0;
            i < all.Length;
            i++)
        {
            RemotePlayerMapMarkerData marker =
                all[i];

            if (
                !RemotePlayerMapValidator
                    .IsValid(
                        marker))
            {
                continue;
            }

            if (!marker.Visible)
            {
                continue;
            }

            valid.Add(
                marker
            );
        }

        return
            valid.ToArray();
    }
}



// ============================================================
// REMOTE PLAYER MASTER SERVICE
// ============================================================

internal static class RemotePlayerMasterService
{
    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerCommandProcessor
            .Process(
                dt
            );

        RemotePlayerManager
            .Update(
                dt
            );

        RemotePlayerSessionMaintenance
            .Update(
                dt
            );

        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (states != null)
        {
            for (
                int i = 0;
                i < states.Length;
                i++)
            {
                RemotePlayerState state =
                    states[i];

                if (state == null)
                {
                    continue;
                }

                RemotePlayerStateProjector
                    .Project(
                        state
                    );
            }
        }

        RemotePlayerMapRegistry
            .Update();
    }

    public static void Clear()
    {
        RemotePlayerManager
            .Clear();

        RemotePlayerRegistry
            .Clear();

        RemotePlayerBridge
            .Clear();

        RemotePlayerStateProjector
            .Clear();

        RemotePlayerMapRegistry
            .Clear();

        RemoteSessionRegistry
            .Clear();

        RemotePlayerCommandQueue
            .Clear();
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
// REMOTE PLAYER REMOVE PACKET
// ============================================================

internal static class RemotePlayerLeaveProcessor
{
    public static void Process(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0 ||
            payload.Length > 1024)
        {
            return;
        }

        try
        {
            string playerId =
                NetworkProtocol
                    .ReadString(
                        payload
                    );

            if (
                string.IsNullOrWhiteSpace(
                    playerId))
            {
                return;
            }

            if (
                playerId ==
                LocalPlayerState.GetNetworkId())
            {
                return;
            }

            RemoteSessionProcessor
                .EnqueueLeave(
                    playerId
                );

            CampaignMapRemotePlayerRegistry
                .Remove(
                    playerId
                );

            RemotePlayerRegistry
                .Remove(
                    playerId
                );

            RemotePlayerWorldViewRegistry
                .Update();

            RemotePlayerStateProjector
                .Remove(
                    playerId
                );
        }
        catch
        {
        }
    }
}



// ============================================================
// REMOTE PLAYER SNAPSHOT RECEIVER
// ============================================================

internal static class RemotePlayerSnapshotReceiver
{
    public static void Receive(
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

        if (snapshot == null)
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
            !NetworkUtilities.IsValidPosition(
                snapshot.X,
                snapshot.Y))
        {
            return;
        }

        RemoteSnapshotDispatcher
            .Enqueue(
                snapshot
            );
    }
}



// ============================================================
// REMOTE PLAYER JOIN RECEIVER
// ============================================================

internal static class RemotePlayerJoinReceiver
{
    public static void Receive(
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

        if (snapshot == null)
        {
            return;
        }

        if (
            snapshot.PlayerId ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        RemoteSessionProcessor
            .EnqueueJoin(
                snapshot.PlayerId,
                snapshot.PlayerName,
                snapshot.GetPosition(),
                snapshot.PartySize
            );
    }
}



// ============================================================
// REMOTE PLAYER LEAVE RECEIVER
// ============================================================

internal static class RemotePlayerLeaveReceiver
{
    public static void Receive(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0 ||
            payload.Length > 1024)
        {
            return;
        }

        try
        {
            string id =
                NetworkProtocol
                    .ReadString(
                        payload
                    );

            if (
                string.IsNullOrWhiteSpace(
                    id))
            {
                return;
            }

            RemoteSessionProcessor
                .EnqueueLeave(
                    id
                );
        }
        catch
        {
        }
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
// REMOTE PLAYER CREATION CONTROLLER
// ============================================================

internal static class RemotePlayerCreationController
{
    public static bool EnsureCreated(
        string id,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        if (
            Campaign.Current == null)
        {
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return false;
        }

        if (
            id ==
            LocalPlayerState.GetNetworkId())
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

        RemotePlayerState state;

        if (
            RemotePlayerManager
                .TryGet(
                    id,
                    out state))
        {
            if (state == null)
            {
                return false;
            }

            state.Name =
                NetworkUtilities.SafeName(
                    name
                );

            state.TargetPosition =
                position;

            state.PartySize =
                NetworkUtilities.SafePartySize(
                    partySize
                );

            state.Active =
                true;

            state.Spawned =
                true;

            state.LastPacketUtc =
                DateTime.UtcNow;

            return true;
        }

        PlayerJoinService
            .CreateLogicalPlayer(
                id,
                name,
                position,
                partySize
            );

        return
            RemotePlayerManager.TryGet(
                id,
                out state
            );
    }
}



// ============================================================
// REMOTE PLAYER POSITION SERVICE
// ============================================================

internal static class RemotePlayerPositionService
{
    public static void UpdateTarget(
        string id,
        CampaignVec2 position)
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
            !NetworkUtilities
                .IsValidPosition(
                    position.X,
                    position.Y))
        {
            return;
        }

        RemotePlayerState state;

        if (
            !RemotePlayerManager.TryGet(
                id,
                out state))
        {
            return;
        }

        if (state == null)
        {
            return;
        }

        state.TargetPosition =
            position;

        state.Active =
            true;

        state.LastPacketUtc =
            DateTime.UtcNow;
    }

    public static CampaignVec2 GetCurrent(
        string id)
    {
        RemotePlayerState state;

        if (
            RemotePlayerManager.TryGet(
                id,
                out state) &&
            state != null)
        {
            return
                state.CurrentPosition;
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
}



// ============================================================
// REMOTE PLAYER NAME SERVICE
// ============================================================

internal static class RemotePlayerNameService
{
    public static void Update(
        string id,
        string name)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        string clean =
            NetworkUtilities.SafeName(
                name
            );

        RemotePlayerState state;

        if (
            !RemotePlayerManager.TryGet(
                id,
                out state))
        {
            return;
        }

        if (state == null)
        {
            return;
        }

        state.Name =
            clean;

        RemotePlayerNameRegistry
            .Set(
                id,
                clean
            );
    }
}



// ============================================================
// REMOTE PLAYER SNAPSHOT APPLIER
// ============================================================

internal static class RemotePlayerSnapshotApplier
{
    public static void Apply(
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

        if (
            snapshot.PlayerId ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    snapshot.X,
                    snapshot.Y))
        {
            return;
        }

        CampaignVec2 position =
            snapshot.GetPosition();

        if (
            !RemotePlayerCreationController
                .EnsureCreated(
                    snapshot.PlayerId,
                    snapshot.PlayerName,
                    position,
                    snapshot.PartySize))
        {
            return;
        }

        RemotePlayerPositionService
            .UpdateTarget(
                snapshot.PlayerId,
                position
            );

        RemotePlayerNameService
            .Update(
                snapshot.PlayerId,
                snapshot.PlayerName
            );

        RemotePlayerPartyService
            .Update(
                snapshot.PlayerId,
                snapshot.PartySize
            );

        RemotePlayerState state;

        if (
            RemotePlayerManager.TryGet(
                snapshot.PlayerId,
                out state) &&
            state != null)
        {
            state.Active =
                true;

            state.Spawned =
                true;

            state.LastPacketUtc =
                DateTime.UtcNow;
        }
    }
}



// ============================================================
// REMOTE PLAYER SNAPSHOT LOOP
// ============================================================

internal static class RemotePlayerSnapshotLoop
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
        int processed =
            0;

        while (
            processed < 128 &&
            Queue.TryDequeue(
                out NetworkPlayerSnapshot snapshot))
        {
            processed++;

            if (snapshot == null)
            {
                continue;
            }

            RemotePlayerSnapshotApplier
                .Apply(
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
// REMOTE PLAYER LEAVE SERVICE
// ============================================================

internal static class RemotePlayerLeaveService
{
    public static void Remove(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        RemotePlayerSnapshotLoop
            .Clear();

        RemotePlayerState state;

        if (
            RemotePlayerManager.TryGet(
                playerId,
                out state))
        {
            if (state != null)
            {
                state.Active =
                    false;

                state.Spawned =
                    false;

                state.Party =
                    null;

                state.Hero =
                    null;
            }
        }

        RemotePlayerManager
            .RemoveSessionInternal(
                playerId
            );

        RemotePlayerStateProjector
            .Remove(
                playerId
            );

        CampaignMapRemotePlayerRegistry
            .Remove(
                playerId
            );

        RemotePlayerWorldViewRegistry
            .Update();
    }
}



// ============================================================
// REMOTE PLAYER TIMEOUT SERVICE
// ============================================================

internal static class RemotePlayerTimeoutService
{
    private static readonly TimeSpan Timeout =
        TimeSpan.FromSeconds(
            8
        );

    private static float _timer;

    public static void Update(
        float dt)
    {
        _timer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_timer < 1f)
        {
            return;
        }

        _timer =
            0f;

        DateTime now =
            DateTime.UtcNow;

        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (states == null)
        {
            return;
        }

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerState state =
                states[i];

            if (state == null)
            {
                continue;
            }

            if (
                now -
                state.LastPacketUtc >
                Timeout)
            {
                RemotePlayerLeaveService
                    .Remove(
                        state.PlayerId
                    );
            }
        }
    }
}



// ============================================================
// REMOTE PLAYER UPDATE PIPELINE
// ============================================================

internal static class RemotePlayerUpdatePipeline
{
    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerSnapshotLoop
            .Process();

        RemotePlayerManager
            .Update(
                dt
            );

        RemotePlayerTimeoutService
            .Update(
                dt
            );

        RemotePlayerMapRefresh
            .Update();

        RemotePlayerWorldViewRegistry
            .Update();

        RemotePlayerMapRegistry
            .Update();
    }

    public static void Clear()
    {
        RemotePlayerSnapshotLoop
            .Clear();

        RemotePlayerManager
            .Clear();

        RemotePlayerRegistry
            .Clear();

        RemotePlayerMapRegistry
            .Clear();

        RemotePlayerWorldViewRegistry
            .Clear();

        RemotePlayerStateProjector
            .Clear();

        RemotePlayerFinalCleanup
            .Clear();
    }
}



// ============================================================
// CAMPAIGN PLAYER SNAPSHOT
// ============================================================

public sealed class CampaignPlayerSnapshot
{
    public string PlayerId;

    public string PlayerName;

    public float PositionX;

    public float PositionY;

    public int PartySize;

    public long Sequence;

    public long TimestampUtcTicks;

    public bool Connected;

    public bool Ready;

    public CampaignPlayerSnapshot()
    {
        PlayerId =
            "";

        PlayerName =
            "Player";

        PositionX =
            0f;

        PositionY =
            0f;

        PartySize =
            1;

        Sequence =
            0;

        TimestampUtcTicks =
            DateTime.UtcNow.Ticks;

        Connected =
            false;

        Ready =
            false;
    }

    public CampaignVec2 Position
    {
        get
        {
            return
                new CampaignVec2(
                    new Vec2(
                        PositionX,
                        PositionY
                    ),
                    true
                );
        }

        set
        {
            PositionX =
                value.X;

            PositionY =
                value.Y;
        }
    }
}



// ============================================================
// CAMPAIGN PLAYER SNAPSHOT CODEC
// ============================================================

internal static class CampaignPlayerSnapshotCodec
{
    public static byte[] Encode(
        CampaignPlayerSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return null;
        }

        if (
            string.IsNullOrWhiteSpace(
                snapshot.PlayerId))
        {
            return null;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    snapshot.PositionX,
                    snapshot.PositionY))
        {
            return null;
        }

        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        snapshot.PlayerId
                    );

                    writer.Write(
                        NetworkUtilities
                            .SafeName(
                                snapshot.PlayerName
                            )
                    );

                    writer.Write(
                        snapshot.PositionX
                    );

                    writer.Write(
                        snapshot.PositionY
                    );

                    writer.Write(
                        NetworkUtilities
                            .SafePartySize(
                                snapshot.PartySize
                            )
                    );

                    writer.Write(
                        snapshot.Sequence
                    );

                    writer.Write(
                        snapshot.TimestampUtcTicks
                    );

                    writer.Write(
                        snapshot.Connected
                    );

                    writer.Write(
                        snapshot.Ready
                    );
                }
            );
    }

    public static bool Decode(
        byte[] payload,
        out CampaignPlayerSnapshot snapshot)
    {
        snapshot =
            null;

        if (
            payload == null ||
            payload.Length == 0 ||
            payload.Length > 2048)
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
                CampaignPlayerSnapshot result =
                    new CampaignPlayerSnapshot();

                result.PlayerId =
                    reader.ReadString();

                result.PlayerName =
                    reader.ReadString();

                result.PositionX =
                    reader.ReadSingle();

                result.PositionY =
                    reader.ReadSingle();

                result.PartySize =
                    reader.ReadInt32();

                if (
                    stream.Position <
                    stream.Length)
                {
                    result.Sequence =
                        reader.ReadInt64();
                }

                if (
                    stream.Position <
                    stream.Length)
                {
                    result.TimestampUtcTicks =
                        reader.ReadInt64();
                }

                if (
                    stream.Position <
                    stream.Length)
                {
                    result.Connected =
                        reader.ReadBoolean();
                }

                if (
                    stream.Position <
                    stream.Length)
                {
                    result.Ready =
                        reader.ReadBoolean();
                }

                if (
                    !NetworkStateValidator
                        .IsValidPlayerId(
                            result.PlayerId))
                {
                    return false;
                }

                if (
                    !NetworkUtilities
                        .IsValidPosition(
                            result.PositionX,
                            result.PositionY))
                {
                    return false;
                }

                result.PlayerName =
                    NetworkUtilities
                        .SafeName(
                            result.PlayerName
                        );

                result.PartySize =
                    NetworkUtilities
                        .SafePartySize(
                            result.PartySize
                        );

                snapshot =
                    result;

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
// REMOTE PLAYER MAP MARKER MODEL
// ============================================================

public sealed class CampaignRemotePlayerMarker
{
    public string PlayerId;

    public string Name;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Visible;

    public DateTime LastUpdateUtc;

    public float Opacity;

    public CampaignRemotePlayerMarker()
    {
        PlayerId =
            "";

        Name =
            "Player";

        Position =
            new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );

        PartySize =
            1;

        Visible =
            false;

        LastUpdateUtc =
            DateTime.MinValue;

        Opacity =
            1f;
    }
}



// ============================================================
// CAMPAIGN REMOTE PLAYER MARKER REGISTRY
// ============================================================

public static class CampaignRemotePlayerMarkerRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        CampaignRemotePlayerMarker>
        Markers =
            new Dictionary<
                string,
                CampaignRemotePlayerMarker>();

    public static void Update()
    {
        RemotePlayerState[] players =
            RemotePlayerManager
                .Snapshot();

        lock (Sync)
        {
            HashSet<string> activeIds =
                new HashSet<string>();

            if (players != null)
            {
                for (
                    int i = 0;
                    i < players.Length;
                    i++)
                {
                    RemotePlayerState state =
                        players[i];

                    if (state == null)
                    {
                        continue;
                    }

                    if (
                        string.IsNullOrWhiteSpace(
                            state.PlayerId))
                    {
                        continue;
                    }

                    if (!state.Active)
                    {
                        continue;
                    }

                    if (
                        !NetworkUtilities
                            .IsValidPosition(
                                state.CurrentPosition.X,
                                state.CurrentPosition.Y))
                    {
                        continue;
                    }

                    activeIds.Add(
                        state.PlayerId
                    );

                    CampaignRemotePlayerMarker
                        marker;

                    if (
                        !Markers.TryGetValue(
                            state.PlayerId,
                            out marker))
                    {
                        marker =
                            new CampaignRemotePlayerMarker();

                        Markers.Add(
                            state.PlayerId,
                            marker
                        );
                    }

                    marker.PlayerId =
                        state.PlayerId;

                    marker.Name =
                        NetworkUtilities
                            .SafeName(
                                state.Name
                            );

                    marker.Position =
                        state.CurrentPosition;

                    marker.PartySize =
                        NetworkUtilities
                            .SafePartySize(
                                state.PartySize
                            );

                    marker.Visible =
                        true;

                    marker.LastUpdateUtc =
                        state.LastPacketUtc;

                    marker.Opacity =
                        1f;
                }
            }

            List<string> stale =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    CampaignRemotePlayerMarker>
                item in Markers)
            {
                if (
                    !activeIds.Contains(
                        item.Key))
                {
                    stale.Add(
                        item.Key
                    );
                }
            }

            for (
                int i = 0;
                i < stale.Count;
                i++)
            {
                Markers.Remove(
                    stale[i]
                );
            }
        }
    }

    public static CampaignRemotePlayerMarker[]
        Snapshot()
    {
        lock (Sync)
        {
            CampaignRemotePlayerMarker[] result =
                new CampaignRemotePlayerMarker[
                    Markers.Count
                ];

            int index =
                0;

            foreach (
                CampaignRemotePlayerMarker marker
                in Markers.Values)
            {
                result[index++] =
                    marker;
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
            Markers.Remove(
                playerId
            );
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Markers.Clear();
        }
    }
}



// ============================================================
// CAMPAIGN REMOTE PLAYER MAP QUERY
// ============================================================

public static class CampaignRemotePlayerMapQuery
{
    public static CampaignRemotePlayerMarker[]
        GetVisibleMarkers()
    {
        CampaignRemotePlayerMarker[] markers =
            CampaignRemotePlayerMarkerRegistry
                .Snapshot();

        if (
            markers == null ||
            markers.Length == 0)
        {
            return Array.Empty<
                CampaignRemotePlayerMarker>();
        }

        List<CampaignRemotePlayerMarker>
            visible =
                new List<
                    CampaignRemotePlayerMarker>();

        for (
            int i = 0;
            i < markers.Length;
            i++)
        {
            CampaignRemotePlayerMarker marker =
                markers[i];

            if (
                marker == null ||
                !marker.Visible)
            {
                continue;
            }

            if (
                !NetworkUtilities
                    .IsValidPosition(
                        marker.Position.X,
                        marker.Position.Y))
            {
                continue;
            }

            visible.Add(
                marker
            );
        }

        return
            visible.ToArray();
    }
}



// ============================================================
// MAP MARKER CLEANUP
// ============================================================

internal static class CampaignRemotePlayerMarkerCleanup
{
    public static void Remove(
        string playerId)
    {
        CampaignRemotePlayerMarkerRegistry
            .Remove(
                playerId
            );
    }

    public static void Clear()
    {
        CampaignRemotePlayerMarkerRegistry
            .Clear();
    }
}



// ============================================================
// REMOTE PLAYER UPDATE SERVICE
// ============================================================

internal static class RemotePlayerUpdateService
{
    public static void Update(
        float dt)
    {
        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerCommandProcessor
            .Process(
                dt
            );

        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (states == null)
        {
            return;
        }

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerState state =
                states[i];

            if (state == null)
            {
                continue;
            }

            SafeRemotePositionApplication
                .Apply(
                    state,
                    dt
                );
        }

        CampaignRemotePlayerMarkerRegistry
            .Update();
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
// REMOTE PLAYER STATE COPY
// ============================================================

internal static class RemotePlayerStateCopy
{
    public static RemotePlayerVisualData
        CopyVisual(
            RemotePlayerState state)
    {
        if (state == null)
        {
            return null;
        }

        return
            new RemotePlayerVisualData
            {
                PlayerId =
                    state.PlayerId,

                DisplayName =
                    NetworkUtilities
                        .SafeName(
                            state.Name
                        ),

                Position =
                    state.CurrentPosition,

                PartySize =
                    NetworkUtilities
                        .SafePartySize(
                            state.PartySize
                        ),

                Visible =
                    state.Active,

                LastUpdateUtc =
                    state.LastPacketUtc
            };
    }
}



// ============================================================
// REMOTE PLAYER SNAPSHOT LIST
// ============================================================

public static class RemotePlayerSnapshotList
{
    public static RemotePlayerVisualData[]
        Get()
    {
        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (
            states == null ||
            states.Length == 0)
        {
            return Array.Empty<
                RemotePlayerVisualData>();
        }

        List<
            RemotePlayerVisualData>
            result =
                new List<
                    RemotePlayerVisualData>();

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerVisualData copy =
                RemotePlayerStateCopy
                    .CopyVisual(
                        states[i]
                    );

            if (copy == null)
            {
                continue;
            }

            if (!copy.Visible)
            {
                continue;
            }

            result.Add(
                copy
            );
        }

        return
            result.ToArray();
    }
}



// ============================================================
// REMOTE PLAYER MAP POSITION
// ============================================================

public static class RemotePlayerMapPosition
{
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

        RemotePlayerState state;

        if (
            !RemotePlayerManager
                .TryGet(
                    playerId,
                    out state))
        {
            return false;
        }

        if (
            state == null ||
            !state.Active)
        {
            return false;
        }

        position =
            state.CurrentPosition;

        return
            NetworkUtilities
                .IsValidPosition(
                    position.X,
                    position.Y);
    }
}



// ============================================================
// REMOTE PLAYER DISPLAY NAME
// ============================================================

public static class RemotePlayerDisplayName
{
    public static string Get(
        string playerId)
    {
        return
            RemotePlayerNameRegistry
                .Get(
                    playerId
                );
    }
}



// ============================================================
// REMOTE PLAYER ACTIVE CHECK
// ============================================================

public static class RemotePlayerActiveCheck
{
    public static bool IsActive(
        string playerId)
    {
        RemotePlayerState state;

        if (
            !RemotePlayerManager.TryGet(
                playerId,
                out state))
        {
            return false;
        }

        return
            state != null &&
            state.Active;
    }
}



// ============================================================
// REMOTE PLAYER MAP DATA
// ============================================================

public sealed class RemotePlayerMapData
{
    public string PlayerId;

    public string Name;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Active;

    public DateTime LastUpdateUtc;
}



// ============================================================
// REMOTE PLAYER MAP DATA SERVICE
// ============================================================

public static class RemotePlayerMapDataService
{
    public static RemotePlayerMapData[]
        Get()
    {
        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (
            states == null ||
            states.Length == 0)
        {
            return Array.Empty<
                RemotePlayerMapData>();
        }

        List<
            RemotePlayerMapData>
            result =
                new List<
                    RemotePlayerMapData>();

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerState state =
                states[i];

            if (state == null)
            {
                continue;
            }

            if (!state.Active)
            {
                continue;
            }

            if (
                !NetworkUtilities
                    .IsValidPosition(
                        state.CurrentPosition.X,
                        state.CurrentPosition.Y))
            {
                continue;
            }

            result.Add(
                new RemotePlayerMapData
                {
                    PlayerId =
                        state.PlayerId,

                    Name =
                        NetworkUtilities
                            .SafeName(
                                state.Name
                            ),

                    Position =
                        state.CurrentPosition,

                    PartySize =
                        NetworkUtilities
                            .SafePartySize(
                                state.PartySize
                            ),

                    Active =
                        true,

                    LastUpdateUtc =
                        state.LastPacketUtc
                }
            );
        }

        return
            result.ToArray();
    }
}



// ============================================================
// REMOTE PLAYER SYNC STATISTICS
// ============================================================

public static class RemotePlayerSyncStatistics
{
    private static readonly object Sync =
        new object();

    private static long _received;

    private static long _accepted;

    private static long _rejected;

    private static DateTime _lastReceivedUtc =
        DateTime.MinValue;

    public static long Received
    {
        get
        {
            lock (Sync)
            {
                return _received;
            }
        }
    }

    public static long Accepted
    {
        get
        {
            lock (Sync)
            {
                return _accepted;
            }
        }
    }

    public static long Rejected
    {
        get
        {
            lock (Sync)
            {
                return _rejected;
            }
        }
    }

    public static DateTime LastReceivedUtc
    {
        get
        {
            lock (Sync)
            {
                return _lastReceivedUtc;
            }
        }
    }

    public static void MarkReceived()
    {
        lock (Sync)
        {
            _received++;

            _lastReceivedUtc =
                DateTime.UtcNow;
        }
    }

    public static void MarkAccepted()
    {
        lock (Sync)
        {
            _accepted++;
        }
    }

    public static void MarkRejected()
    {
        lock (Sync)
        {
            _rejected++;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _received =
                0;

            _accepted =
                0;

            _rejected =
                0;

            _lastReceivedUtc =
                DateTime.MinValue;
        }
    }
}



// ============================================================
// REMOTE PLAYER SYNC VALIDATOR
// ============================================================

internal static class RemotePlayerSyncValidator
{
    public static bool Validate(
        NetworkPlayerSnapshot snapshot)
    {
        RemotePlayerSyncStatistics
            .MarkReceived();

        if (snapshot == null)
        {
            RemotePlayerSyncStatistics
                .MarkRejected();

            return false;
        }

        if (
            string.IsNullOrWhiteSpace(
                snapshot.PlayerId))
        {
            RemotePlayerSyncStatistics
                .MarkRejected();

            return false;
        }

        if (
            snapshot.PlayerId ==
            LocalPlayerState
                .GetNetworkId())
        {
            RemotePlayerSyncStatistics
                .MarkRejected();

            return false;
        }

        if (
            !NetworkUtilities
                .IsValidPosition(
                    snapshot.X,
                    snapshot.Y))
        {
            RemotePlayerSyncStatistics
                .MarkRejected();

            return false;
        }

        if (
            snapshot.PartySize <
            1)
        {
            snapshot.PartySize =
                1;
        }

        if (
            snapshot.PartySize >
            10000)
        {
            snapshot.PartySize =
                10000;
        }

        RemotePlayerSyncStatistics
            .MarkAccepted();

        return true;
    }
}



// ============================================================
// REMOTE PLAYER SYNC APPLIER V2
// ============================================================

internal static class RemotePlayerSyncApplierV2
{
    public static void Apply(
        NetworkPlayerSnapshot snapshot)
    {
        if (
            !RemotePlayerSyncValidator
                .Validate(
                    snapshot))
        {
            return;
        }

        RemotePlayerSnapshotLoop
            .Enqueue(
                snapshot
            );

        RemotePlayerState state;

        if (
            RemotePlayerManager.TryGet(
                snapshot.PlayerId,
                out state) &&
            state != null)
        {
            state.TargetPosition =
                snapshot.GetPosition();

            state.PartySize =
                NetworkUtilities
                    .SafePartySize(
                        snapshot.PartySize
                    );

            state.Name =
                NetworkUtilities
                    .SafeName(
                        snapshot.PlayerName
                    );

            state.Active =
                true;

            state.Spawned =
                true;

            state.LastPacketUtc =
                DateTime.UtcNow;
        }
    }
}



// ============================================================
// REMOTE PLAYER MAP REFRESH V2
// ============================================================

internal static class RemotePlayerMapRefreshV2
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

        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (states == null)
        {
            return;
        }

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            RemotePlayerState state =
                states[i];

            if (
                state == null ||
                !state.Active)
            {
                continue;
            }

            RemotePlayerStateProjector
                .Project(
                    state
                );
        }

        RemotePlayerMapRegistry
            .Update();

        CampaignRemotePlayerMarkerRegistry
            .Update();

        RemotePlayerWorldViewRegistry
            .Update();
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
// REMOTE PLAYER FINAL SNAPSHOT
// ============================================================

public sealed class FinalRemotePlayerSnapshot
{
    public string Id;

    public string Name;

    public float X;

    public float Y;

    public int PartySize;

    public bool Connected;

    public DateTime LastUpdateUtc;
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
// FINAL REMOTE PLAYER COLLECTION
// ============================================================

public static class FinalRemotePlayerCollection
{
    public static FinalRemotePlayerSnapshot[]
        Get()
    {
        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (
            states == null ||
            states.Length == 0)
        {
            return
                Array.Empty<
                    FinalRemotePlayerSnapshot>();
        }

        List<
            FinalRemotePlayerSnapshot>
            result =
                new List<
                    FinalRemotePlayerSnapshot>();

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            FinalRemotePlayerSnapshot snapshot =
                FinalRemoteSnapshotFactory
                    .Create(
                        states[i]
                    );

            if (snapshot == null)
            {
                continue;
            }

            if (!snapshot.Connected)
            {
                continue;
            }

            result.Add(
                snapshot
            );
        }

        return
            result.ToArray();
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
// FINAL REMOTE PLAYER CHECK
// ============================================================

internal static class FinalRemotePlayerCheck
{
    public static bool IsUsable(
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

        if (
            !NetworkUtilities
                .IsValidPosition(
                    state.CurrentPosition.X,
                    state.CurrentPosition.Y))
        {
            return false;
        }

        return true;
    }
}



// ============================================================
// FINAL REMOTE PLAYER QUERY
// ============================================================

public static class FinalRemotePlayerQuery
{
    public static int Count()
    {
        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

        if (states == null)
        {
            return 0;
        }

        int count =
            0;

        for (
            int i = 0;
            i < states.Length;
            i++)
        {
            if (
                FinalRemotePlayerCheck
                    .IsUsable(
                        states[i]))
            {
                count++;
            }
        }

        return count;
    }

    public static bool TryGet(
        string id,
        out FinalRemotePlayerSnapshot snapshot)
    {
        snapshot =
            null;

        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return false;
        }

        RemotePlayerState state;

        if (
            !RemotePlayerManager
                .TryGet(
                    id,
                    out state))
        {
            return false;
        }

        if (
            !FinalRemotePlayerCheck
                .IsUsable(
                    state))
        {
            return false;
        }

        snapshot =
            FinalRemoteSnapshotFactory
                .Create(
                    state
                );

        return
            snapshot != null;
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
// FINAL REMOTE PLAYER ACCESSOR
// ============================================================

public static class MultiplayerCampaignPlayers
{
    public static RemotePlayerMapMarkerData[]
        GetRemotePlayers()
    {
        try
        {
            return
                RemotePlayerDisplayService
                    .GetValidPlayers();
        }
        catch
        {
            return
                Array.Empty<
                    RemotePlayerMapMarkerData>();
        }
    }

    public static int GetRemotePlayerCount()
    {
        try
        {
            return
                FinalRemotePlayerQuery
                    .Count();
        }
        catch
        {
            return 0;
        }
    }

    public static bool TryGetPosition(
        string playerId,
        out CampaignVec2 position)
    {
        return
            RemotePlayerMapPosition
                .TryGet(
                    playerId,
                    out position
                );
    }

    public static string GetPlayerName(
        string playerId)
    {
        return
            RemotePlayerDisplayName
                .Get(
                    playerId
                );
    }

    public static int GetPartySize(
        string playerId)
    {
        return
            RemotePlayerPartyCount
                .Get(
                    playerId
                );
    }
}



// ============================================================
// FINAL SESSION STATUS
// ============================================================

public static class MultiplayerCampaignStatus
{
    public static bool CampaignReady
    {
        get
        {
            try
            {
                return
                    MultiplayerCampaignGameState
                        .CampaignReady;
            }
            catch
            {
                return false;
            }
        }
    }

    public static int RemotePlayers
    {
        get
        {
            return
                MultiplayerCampaignPlayers
                    .GetRemotePlayerCount();
        }
    }

    public static bool IsHost
    {
        get
        {
            try
            {
                return
                    MultiplayerSessionState
                        .IsHost;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsClient
    {
        get
        {
            try
            {
                return
                    MultiplayerSessionState
                        .IsClient;
            }
            catch
            {
                return false;
            }
        }
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
// EOF
// ============================================================

// ===== MPC REBUILD LAYER 2026 =====
// This layer is intentionally contained in the same source file so the
// original Multiplayer Campaign core remains present and readable.
internal static class PlayerIdentity
{
    public static void Reset() { }
}


internal static class RemotePlayerMapMarkerCleanup
{
    public static void Remove(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId)) return;
        CampaignMapRemotePlayerRegistry.Remove(playerId);
    }
}


namespace MultiplayerCampaign
{


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
     * VIEW MODEL
     * ============================================================
     */

    public sealed class MultiplayerCampaignVM
        : ViewModel
    {
        private string _ipAddress =
            "127.0.0.1";

        private string _playerName =
            "Player";

        private string _statusText =
            "";

        private bool _showMain =
            true;

        private bool _showCreate;

        private bool _showJoin;

        public MultiplayerCampaignVM()
        {
            MultiplayerNetworkClient
                .Instance
                .SetViewModel(this);

            _playerName =
                LocalPlayerState
                    .GetDisplayName();
        }

        [DataSourceProperty]
        public string IpAddress
        {
            get
            {
                return _ipAddress;
            }

            set
            {
                if (_ipAddress == value)
                {
                    return;
                }

                _ipAddress = value;

                OnPropertyChangedWithValue(
                    value,
                    nameof(IpAddress)
                );
            }
        }

        [DataSourceProperty]
        public string PlayerName
        {
            get
            {
                return _playerName;
            }

            set
            {
                if (_playerName == value)
                {
                    return;
                }

                _playerName = value;

                OnPropertyChangedWithValue(
                    value,
                    nameof(PlayerName)
                );
            }
        }

        [DataSourceProperty]
        public string StatusText
        {
            get
            {
                return _statusText;
            }

            set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;

                OnPropertyChangedWithValue(
                    value,
                    nameof(StatusText)
                );
            }
        }

        [DataSourceProperty]
        public bool ShowMain
        {
            get
            {
                return _showMain;
            }

            private set
            {
                if (_showMain == value)
                {
                    return;
                }

                _showMain = value;

                OnPropertyChangedWithValue(
                    value,
                    nameof(ShowMain)
                );
            }
        }

        [DataSourceProperty]
        public bool ShowCreate
        {
            get
            {
                return _showCreate;
            }

            private set
            {
                if (_showCreate == value)
                {
                    return;
                }

                _showCreate = value;

                OnPropertyChangedWithValue(
                    value,
                    nameof(ShowCreate)
                );
            }
        }

        [DataSourceProperty]
        public bool ShowJoin
        {
            get
            {
                return _showJoin;
            }

            private set
            {
                if (_showJoin == value)
                {
                    return;
                }

                _showJoin = value;

                OnPropertyChangedWithValue(
                    value,
                    nameof(ShowJoin)
                );
            }
        }

        public void SetStatus(
            string text)
        {
            StatusText = text;
        }

        public void ExecuteOpenCreate()
        {
            ShowMain = false;
            ShowCreate = true;
            ShowJoin = false;

            StatusText = "";
        }

        public void ExecuteOpenJoin()
        {
            ShowMain = false;
            ShowCreate = false;
            ShowJoin = true;

            StatusText = "";
        }

        public void ExecuteBackToMain()
        {
            ShowMain = true;
            ShowCreate = false;
            ShowJoin = false;

            StatusText = "";
        }

        public void ExecuteBack()
        {
            MultiplayerNetworkClient
                .Instance
                .Disconnect();

            ScreenManager.PopScreen();
        }

        /*
         * ========================================================
         * CREATE HOST
         * ========================================================
         */

        public void ExecuteStartHost()
        {
            string name =
                string.IsNullOrWhiteSpace(
                    PlayerName)
                    ? "Host"
                    : PlayerName.Trim();

            LocalPlayerState
                .SetDisplayName(
                    name
                );

            StatusText =
                "LOADING MCC...";

            MultiplayerCampaignSubModule
                .RequestHost();

            if (
                !MultiplayerCampaignSubModule
                    .LoadHostCampaign())
            {
                StatusText =
                    "MCC LOAD FAILED";

                MultiplayerCampaignSubModule
                    .StopHost();

                return;
            }

            StatusText =
                "MCC LOADED";
        }

        /*
         * ========================================================
         * JOIN
         * ========================================================
         */

        public void ExecuteJoinHost()
        {
            if (
                string.IsNullOrWhiteSpace(
                    IpAddress))
            {
                StatusText =
                    "HOST IP REQUIRED";

                return;
            }

            if (
                string.IsNullOrWhiteSpace(
                    PlayerName))
            {
                StatusText =
                    "PLAYER NAME REQUIRED";

                return;
            }

            LocalPlayerState
                .SetDisplayName(
                    PlayerName.Trim()
                );

            StatusText =
                "CONNECTING...";

            MultiplayerNetworkClient
                .Instance
                .Connect(
                    IpAddress.Trim()
                );
        }

        public void UpdateNetwork()
        {
            MultiplayerNetworkClient
                .Instance
                .Update();

            if (
                MultiplayerNetworkClient
                    .Instance
                    .ConsumeWorldReady())
            {
                StatusText =
                    "LOADING HOST WORLD...";

                MultiplayerWorldTransfer
                    .FinishClientLoad();
            }
        }
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
    internal sealed class CharacterSlotData
    {
        public int Slot;
        public string CharacterId;
        public string Name;
        public string CharacterCode;

        public bool IsValid
        {
            get
            {
                return Slot >= 0 && Slot < 3 &&
                       !string.IsNullOrWhiteSpace(CharacterId) &&
                       !string.IsNullOrWhiteSpace(Name);
            }
        }
    }


    internal static class CharacterSlotStore
    {
        private static readonly object Sync = new object();
        private static readonly CharacterSlotData[] Slots =
            new CharacterSlotData[3];
        private static bool Loaded;

        private static string FilePath
        {
            get
            {
                string root = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(root, "MultiplayerCampaign", "characters.dat");
            }
        }

        public static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (Loaded)
                    return;

                Loaded = true;
                try
                {
                    for (int i = 0; i < Slots.Length; i++)
                        Slots[i] = new CharacterSlotData { Slot = i };

                    if (!File.Exists(FilePath))
                        return;

                    string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string[] p = lines[i].Split(new[] { '\t' });
                        if (p.Length < 4)
                            continue;

                        int slot;
                        if (!int.TryParse(p[0], out slot) || slot < 0 || slot >= 3)
                            continue;

                        Slots[slot] = new CharacterSlotData
                        {
                            Slot = slot,
                            CharacterId = p[1],
                            Name = p[2],
                            CharacterCode = p[3]
                        };
                    }
                }
                catch
                {
                }
            }
        }

        public static CharacterSlotData Get(int slot)
        {
            EnsureLoaded();
            if (slot < 0 || slot >= 3)
                return null;

            lock (Sync)
            {
                CharacterSlotData value = Slots[slot];
                if (value == null)
                    return null;

                return new CharacterSlotData
                {
                    Slot = value.Slot,
                    CharacterId = value.CharacterId,
                    Name = value.Name,
                    CharacterCode = value.CharacterCode
                };
            }
        }

        public static bool Save(
            int slot,
            string characterId,
            string name,
            string characterCode)
        {
            EnsureLoaded();
            if (slot < 0 || slot >= 3 || string.IsNullOrWhiteSpace(characterId))
                return false;

            lock (Sync)
            {
                Slots[slot] = new CharacterSlotData
                {
                    Slot = slot,
                    CharacterId = characterId.Trim(),
                    Name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim(),
                    CharacterCode = characterCode ?? ""
                };

                try
                {
                    string dir = Path.GetDirectoryName(FilePath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    using (StreamWriter writer = new StreamWriter(FilePath, false, Encoding.UTF8))
                    {
                        for (int i = 0; i < Slots.Length; i++)
                        {
                            CharacterSlotData value = Slots[i];
                            if (value == null || !value.IsValid)
                                continue;

                            writer.Write(value.Slot);
                            writer.Write('\t');
                            writer.Write(value.CharacterId.Replace('\t', ' '));
                            writer.Write('\t');
                            writer.Write(value.Name.Replace('\t', ' '));
                            writer.Write('\t');
                            writer.Write((value.CharacterCode ?? "").Replace('\t', ' '));
                            writer.WriteLine();
                        }
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }
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

