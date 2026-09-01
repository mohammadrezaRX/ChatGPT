// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
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




















/*
 * ============================================================
 * HOST CLIENT CONNECTION
 * ============================================================
 */

internal sealed class HostClientConnection
{
    private readonly MultiplayerCampaignHost _host;

    private readonly TcpClient _client;

    private readonly NetworkStream _stream;

    private readonly object _sendLock =
        new object();

    private bool _closed;

    public string PlayerId
    {
        get;
        private set;
    }

    public string PlayerName
    {
        get;
        set;
    }

    public bool Ready
    {
        get;
        set;
    }

    public float LastX
    {
        get;
        set;
    }

    public float LastY
    {
        get;
        set;
    }

    public int LastPartySize
    {
        get;
        set;
    }

    public bool IsConnected
    {
        get
        {
            try
            {
                return
                    !_closed &&
                    _client != null &&
                    _client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }


    public HostClientConnection(
        MultiplayerCampaignHost host,
        TcpClient client)
    {
        _host =
            host;

        _client =
            client;

        _stream =
            client.GetStream();

        PlayerId =
            "";

        PlayerName =
            "Player";

        LastPartySize =
            1;
    }


    /*
     * ========================================================
     * START
     * ========================================================
     */

    public async Task StartAsync(
        CancellationToken token)
    {
        try
        {
            while (
                !_closed &&
                !token.IsCancellationRequested)
            {
                byte[] lengthBytes =
                    await ReadExactAsync(
                        4,
                        token
                    );

                if (lengthBytes == null)
                {
                    return;
                }

                int length =
                    BitConverter.ToInt32(
                        lengthBytes,
                        0
                    );

                if (
                    length < 2 ||
                    length >
                    NetworkProtocol.MaxPacketSize)
                {
                    return;
                }

                byte[] body =
                    await ReadExactAsync(
                        length,
                        token
                    );

                if (body == null)
                {
                    return;
                }

                if (
                    body.Length < 2)
                {
                    return;
                }

                if (
                    body[0] !=
                    NetworkProtocol.Version)
                {
                    SendError(
                        "Unsupported network protocol."
                    );

                    return;
                }

                NetworkPacketType type =
                    (NetworkPacketType)
                    body[1];

                byte[] payload =
                    new byte[
                        body.Length - 2
                    ];

                if (payload.Length > 0)
                {
                    Buffer.BlockCopy(
                        body,
                        2,
                        payload,
                        0,
                        payload.Length
                    );
                }

                ProcessMessage(
                    type,
                    payload
                );
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                HostConsole.WriteLine(
                    "[!] Client connection error: " +
                    ex.Message
                );
            }
        }
        finally
        {
            Close();

            _host.RemoveClient(
                this
            );
        }
    }


    /*
     * ========================================================
     * READ
     * ========================================================
     */

    private async Task<byte[]> ReadExactAsync(
        int count,
        CancellationToken token)
    {
        byte[] buffer =
            new byte[count];

        int offset = 0;

        while (
            offset <
            count)
        {
            int read =
                await _stream.ReadAsync(
                    buffer,
                    offset,
                    count - offset,
                    token
                );

            if (read <= 0)
            {
                return null;
            }

            offset += read;
        }

        return buffer;
    }


    /*
     * ========================================================
     * PROCESS MESSAGE
     * ========================================================
     */

    private void ProcessMessage(
        NetworkPacketType type,
        byte[] payload)
    {
        switch (type)
        {
            case NetworkPacketType.Hello:
                HandleHello(
                    payload
                );
                break;

            case NetworkPacketType.PlayerReady:
                HandleReady(
                    payload
                );
                break;

            case NetworkPacketType.PlayerSnapshot:
                _host.OnPlayerSnapshot(
                    this,
                    payload
                );
                break;

            case NetworkPacketType.ResyncRequest:
                HandleResyncRequest();
                break;
        }
    }


    /*
     * ========================================================
     * HELLO
     * ========================================================
     */

    private void HandleHello(
        byte[] payload)
    {
        string requestedName =
            NetworkProtocol.ReadString(
                payload
            );

        PlayerId =
            Guid.NewGuid().ToString(
                "N"
            );

        PlayerName =
            SanitizeName(
                requestedName
            );

        Send(
            new NetworkMessageData(
                NetworkPacketType.Welcome,
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            "Connected as " +
                            PlayerName
                        );

                        writer.Write(
                            PlayerId
                        );
                    }
                )
            )
        );

        HostConsole.WriteLine(
            "[MultiplayerCampaign] " +
            "Player connected: " +
            PlayerName
        );

        /*
         * World transfer happens only after a valid
         * handshake identity exists.
         */

        _ =
            SendWorldSafelyAsync();
    }


    /*
     * ========================================================
     * WORLD SEND
     * ========================================================
     */

    private async Task SendWorldSafelyAsync()
    {
        try
        {
            await _host
                .SendWorldToClientAsync(
                    this
                );
        }
        catch (Exception ex)
        {
            SendError(
                "World synchronization failed: " +
                ex.Message
            );
        }
    }


    /*
     * ========================================================
     * READY
     * ========================================================
     */

    private void HandleReady(
        byte[] payload)
    {
        string name =
            NetworkProtocol.ReadString(
                payload
            );

        if (
            !string.IsNullOrWhiteSpace(
                name))
        {
            PlayerName =
                SanitizeName(
                    name
                );
        }

        _host.OnPlayerReady(
            this
        );
    }


    /*
     * ========================================================
     * RESYNC
     * ========================================================
     */

    private void HandleResyncRequest()
    {
        _ =
            SendWorldSafelyAsync();
    }


    /*
     * ========================================================
     * SEND
     * ========================================================
     */

    public void Send(
        NetworkMessageData message)
    {
        if (
            message == null ||
            _closed)
        {
            return;
        }

        byte[] frame =
            NetworkProtocol.BuildFrame(
                message.Type,
                message.Payload
            );

        lock (_sendLock)
        {
            if (_closed)
            {
                return;
            }

            try
            {
                _stream.Write(
                    frame,
                    0,
                    frame.Length
                );

                _stream.Flush();
            }
            catch
            {
                Close();
            }
        }
    }


    /*
     * ========================================================
     * ERROR
     * ========================================================
     */

    public void SendError(
        string message)
    {
        if (
            string.IsNullOrWhiteSpace(
                message))
        {
            message =
                "Unknown network error.";
        }

        Send(
            new NetworkMessageData(
                NetworkPacketType.Error,
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            message
                        );
                    }
                )
            )
        );
    }


    /*
     * ========================================================
     * CLOSE
     * ========================================================
     */

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed =
            true;

        try
        {
            _stream?.Close();
        }
        catch
        {
        }

        try
        {
            _client?.Close();
        }
        catch
        {
        }
    }


    /*
     * ========================================================
     * NAME
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

        name =
            name.Trim();

        if (name.Length > 32)
        {
            name =
                name.Substring(
                    0,
                    32
                );
        }

        return name;
    }
}




















/*
 * ============================================================
 * NETWORK MESSAGE DATA
 * ============================================================
 */

internal sealed class NetworkMessageData
{
    public NetworkPacketType Type;

    public byte[] Payload;

    public NetworkMessageData(
        NetworkPacketType type,
        byte[] payload)
    {
        Type =
            type;

        Payload =
            payload ??
            Array.Empty<byte>();
    }
}




















/*
 * ============================================================
 * SESSION STATE
 * ============================================================
 */

public static class MultiplayerSessionState
{
    private static readonly object Sync =
        new object();

    private static bool _active;

    private static bool _host;

    private static bool _client;

    private static bool _worldReady;

    private static string _sessionName =
        "MCC";

    public static bool Active
    {
        get
        {
            lock (Sync)
            {
                return _active;
            }
        }
    }

    public static bool IsHost
    {
        get
        {
            lock (Sync)
            {
                return _host;
            }
        }
    }

    public static bool IsClient
    {
        get
        {
            lock (Sync)
            {
                return _client;
            }
        }
    }

    public static bool WorldReady
    {
        get
        {
            lock (Sync)
            {
                return _worldReady;
            }
        }
    }

    public static string SessionName
    {
        get
        {
            lock (Sync)
            {
                return _sessionName;
            }
        }

        set
        {
            lock (Sync)
            {
                _sessionName =
                    string.IsNullOrWhiteSpace(
                        value)
                        ? "MCC"
                        : value.Trim();
            }
        }
    }

    public static void StartHost()
    {
        lock (Sync)
        {
            _active =
                true;

            _host =
                true;

            _client =
                false;

            _worldReady =
                false;
        }
    }

    public static void StartClient()
    {
        lock (Sync)
        {
            _active =
                true;

            _host =
                false;

            _client =
                true;

            _worldReady =
                false;
        }
    }

    public static void SetWorldReady(
        bool value)
    {
        lock (Sync)
        {
            _worldReady =
                value;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _active =
                false;

            _host =
                false;

            _client =
                false;

            _worldReady =
                false;

            _sessionName =
                "MCC";
        }
    }
}




















// ============================================================
// END OF CORRECTED SECTION
// ============================================================
// ادامه
// توجه: این بخش ادامه‌ی نسخه‌ای است که تا اینجا بازسازی شده، نه
// تضمیناً ادامه‌ی دقیق خط 2000 فایل اصلی GitHub.

// ============================================================
// NETWORK UTILITIES
// ============================================================

internal static class NetworkUtilities
{
    public static bool IsValidFloat(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }

    public static bool IsValidPosition(
        float x,
        float y)
    {
        return
            IsValidFloat(x) &&
            IsValidFloat(y);
    }

    public static string SafeName(
        string value)
    {
        if (
            string.IsNullOrWhiteSpace(
                value))
        {
            return "Player";
        }

        string result =
            value.Trim();

        if (result.Length > 32)
        {
            result =
                result.Substring(
                    0,
                    32
                );
        }

        return result;
    }

    public static int SafePartySize(
        int value)
    {
        return Math.Max(
            1,
            Math.Min(
                10000,
                value
            )
        );
    }
}




















// ============================================================
// REMOTE PLAYER SNAPSHOT MODEL
// ============================================================

public sealed class NetworkPlayerSnapshot
{
    public string PlayerId;

    public string PlayerName;

    public float X;

    public float Y;

    public int PartySize;

    public long Timestamp;

    public NetworkPlayerSnapshot()
    {
        PlayerId =
            "";

        PlayerName =
            "Player";

        PartySize =
            1;

        Timestamp =
            DateTime.UtcNow.Ticks;
    }

    public CampaignVec2 GetPosition()
    {
        if (
            !NetworkUtilities.IsValidPosition(
                X,
                Y))
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

        return
            new CampaignVec2(
                new Vec2(
                    X,
                    Y
                ),
                true
            );
    }
}




















// ============================================================
// CLIENT SNAPSHOT SEND
// ============================================================

internal static class LocalPlayerNetworkSender
{
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

        if (_timer < 0.10f)
        {
            return;
        }

        _timer =
            0f;

        MultiplayerNetworkClient client =
            MultiplayerNetworkClient.Instance;

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

        client.SendLocalPlayerState(
            position,
            size
        );
    }
}




















// ============================================================
// SESSION CONTROLLER
// ============================================================

public static class MultiplayerSessionController
{
    public static void StartHost()
    {
        MultiplayerSessionState
            .StartHost();

        HostConsole.WriteLine(
            "[MultiplayerCampaign] " +
            "Starting Host..."
        );

        MultiplayerCampaignSubModule
            .RequestHost();
    }

    public static void StartClient(
        string ip)
    {
        MultiplayerSessionState
            .StartClient();

        LocalPlayerState
            .SetDisplayName(
                LocalPlayerState
                    .GetDisplayName()
            );

        MultiplayerNetworkClient
            .Instance
            .Connect(
                string.IsNullOrWhiteSpace(
                    ip)
                    ? "127.0.0.1"
                    : ip.Trim()
            );
    }

    public static void Stop()
    {
        MultiplayerCampaignSubModule
            .StopHost();

        MultiplayerNetworkClient
            .Instance
            .Disconnect();

        MultiplayerCleanup
            .Execute();
    }
}




















// ============================================================
// NETWORK STATE VALIDATOR
// ============================================================

internal static class NetworkStateValidator
{
    public static bool IsValidPlayerId(
        string id)
    {
        return
            !string.IsNullOrWhiteSpace(
                id) &&
            id.Length <= 128;
    }

    public static bool IsValidPlayerName(
        string name)
    {
        return
            !string.IsNullOrWhiteSpace(
                name) &&
            name.Length <= 32;
    }

    public static bool IsValidPosition(
        CampaignVec2 position)
    {
        return
            NetworkUtilities.IsValidPosition(
                position.X,
                position.Y
            );
    }

    public static bool IsValidPartySize(
        int size)
    {
        return
            size >= 1 &&
            size <= 10000;
    }
}




















// ============================================================
// CONNECTION STATE
// ============================================================

public enum MultiplayerConnectionState
{
    Disconnected,

    Connecting,

    Connected,

    Handshaking,

    SynchronizingWorld,

    WaitingForCampaign,

    Ready,

    Disconnecting
}




















// ============================================================
// CONNECTION STATUS
// ============================================================

public static class MultiplayerConnectionStatus
{
    private static readonly object Sync =
        new object();

    private static MultiplayerConnectionState
        _state =
            MultiplayerConnectionState
                .Disconnected;

    public static MultiplayerConnectionState
        State
    {
        get
        {
            lock (Sync)
            {
                return _state;
            }
        }
    }

    public static void Set(
        MultiplayerConnectionState state)
    {
        lock (Sync)
        {
            _state =
                state;
        }
    }

    public static bool IsReady()
    {
        lock (Sync)
        {
            return
                _state ==
                MultiplayerConnectionState.Ready;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _state =
                MultiplayerConnectionState
                    .Disconnected;
        }
    }
}




















// ============================================================
// SESSION ID
// ============================================================

internal static class MultiplayerSessionId
{
    private static string _id;

    private static readonly object Sync =
        new object();

    public static string Get()
    {
        lock (Sync)
        {
            if (
                string.IsNullOrWhiteSpace(
                    _id))
            {
                _id =
                    Guid.NewGuid()
                        .ToString("N");
            }

            return _id;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _id =
                null;
        }
    }
}




















// ============================================================
// SESSION STARTUP
// ============================================================

public static class MultiplayerSessionStartup
{
    public static bool StartHost()
    {
        try
        {
            MultiplayerCampaignGameState
                .InitializeHost();

            LocalPlayerState
                .SetDisplayName(
                    LocalPlayerState
                        .GetDisplayName()
                );

            MultiplayerConnectionStatus
                .Set(
                    MultiplayerConnectionState
                        .Connecting
                );

            HostConsole.WriteLine(
                "[MultiplayerCampaign] " +
                "Host session starting."
            );

            return true;
        }
        catch (Exception ex)
        {
            HostConsole.WriteLine(
                "[!] Host startup error: " +
                ex.Message
            );

            MultiplayerCampaignGameState
                .Reset();

            return false;
        }
    }

    public static bool StartClient(
        string ip)
    {
        try
        {
            if (
                string.IsNullOrWhiteSpace(
                    ip))
            {
                return false;
            }

            MultiplayerCampaignGameState
                .InitializeClient();

            MultiplayerConnectionStatus
                .Set(
                    MultiplayerConnectionState
                        .Connecting
                );

            MultiplayerNetworkClient
                .Instance
                .Connect(
                    ip.Trim()
                );

            return true;
        }
        catch (Exception ex)
        {
            HostConsole.WriteLine(
                "[!] Client startup error: " +
                ex.Message
            );

            MultiplayerCampaignGameState
                .Reset();

            return false;
        }
    }
}




















// ============================================================
// SESSION SHUTDOWN
// ============================================================

public static class MultiplayerSessionShutdown
{
    public static void Stop()
    {
        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .Disconnecting
            );

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
            RemotePlayerFinalCleanup
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
            RemotePlayerWorldViewRegistry
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

        MultiplayerCampaignGameState
            .Reset();

        MultiplayerConnectionStatus
            .Reset();
    }
}




















// ============================================================
// SAFE NETWORK SHUTDOWN HOOK
// ============================================================

internal static class SafeNetworkShutdownHook
{
    public static void Execute()
    {
        try
        {
            MultiplayerConnectionStatus
                .Set(
                    MultiplayerConnectionState
                        .Disconnecting
                );
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
            RemotePlayerFinalCleanup
                .Clear();
        }
        catch
        {
        }
    }
}




















// ============================================================
// END
// ============================================================
// ============================================================
// ADVANCED REMOTE PLAYER SESSION DATA
// ============================================================

public sealed class RemotePlayerSessionData
{
    public string PlayerId;

    public string Name;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Connected;

    public bool Ready;

    public bool WorldSynchronized;

    public DateTime ConnectedUtc;

    public DateTime LastUpdateUtc;

    public int Sequence;

    public RemotePlayerSessionData()
    {
        PlayerId = "";
        Name = "Player";

        Position =
            new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );

        PartySize = 1;

        Connected = false;
        Ready = false;
        WorldSynchronized = false;

        ConnectedUtc =
            DateTime.UtcNow;

        LastUpdateUtc =
            DateTime.UtcNow;

        Sequence = 0;
    }
}




















// ============================================================
// REMOTE SESSION REGISTRY
// ============================================================

public static class RemoteSessionRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        RemotePlayerSessionData>
        Sessions =
            new Dictionary<
                string,
                RemotePlayerSessionData>();

    public static RemotePlayerSessionData GetOrCreate(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return null;
        }

        lock (Sync)
        {
            RemotePlayerSessionData session;

            if (
                !Sessions.TryGetValue(
                    playerId,
                    out session))
            {
                session =
                    new RemotePlayerSessionData
                    {
                        PlayerId =
                            playerId,

                        Connected =
                            true,

                        ConnectedUtc =
                            DateTime.UtcNow,

                        LastUpdateUtc =
                            DateTime.UtcNow
                    };

                Sessions.Add(
                    playerId,
                    session
                );
            }

            return session;
        }
    }

    public static bool TryGet(
        string playerId,
        out RemotePlayerSessionData session)
    {
        session = null;

        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return false;
        }

        lock (Sync)
        {
            return Sessions.TryGetValue(
                playerId,
                out session
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
            Sessions.Remove(
                playerId
            );
        }
    }

    public static RemotePlayerSessionData[]
        Snapshot()
    {
        lock (Sync)
        {
            RemotePlayerSessionData[] result =
                new RemotePlayerSessionData[
                    Sessions.Count
                ];

            int index = 0;

            foreach (
                RemotePlayerSessionData session
                in Sessions.Values)
            {
                result[index++] =
                    session;
            }

            return result;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Sessions.Clear();
        }
    }
}




















// ============================================================
// REMOTE SESSION PROCESSOR
// ============================================================

internal static class RemoteSessionProcessor
{
    private static readonly ConcurrentQueue<
        RemotePlayerCommand>
        Queue =
            new ConcurrentQueue<
                RemotePlayerCommand>();

    public static void EnqueueJoin(
        string id,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        Queue.Enqueue(
            new RemotePlayerCommand
            {
                Type =
                    RemotePlayerCommandType.JoinWithState,

                PlayerId =
                    id,

                Name =
                    name,

                Position =
                    position,

                PartySize =
                    partySize
            }
        );
    }

    public static void EnqueueState(
        string id,
        string name,
        CampaignVec2 position,
        int partySize)
    {
        Queue.Enqueue(
            new RemotePlayerCommand
            {
                Type =
                    RemotePlayerCommandType.State,

                PlayerId =
                    id,

                Name =
                    name,

                Position =
                    position,

                PartySize =
                    partySize
            }
        );
    }

    public static void EnqueueLeave(
        string id)
    {
        Queue.Enqueue(
            new RemotePlayerCommand
            {
                Type =
                    RemotePlayerCommandType.Leave,

                PlayerId =
                    id
            }
        );
    }

    public static void Process(
        float dt)
    {
        int processed = 0;

        while (
            processed < 128 &&
            Queue.TryDequeue(
                out RemotePlayerCommand command))
        {
            processed++;

            if (command == null)
            {
                continue;
            }

            if (
                string.IsNullOrWhiteSpace(
                    command.PlayerId))
            {
                continue;
            }

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
    }

    private static void ProcessJoin(
        RemotePlayerCommand command)
    {
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
        RemoteSessionRegistry
            .Remove(
                command.PlayerId
            );

        RemotePlayerManager
            .RemoveSession(
                command.PlayerId
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
// NETWORK UPDATE INTEGRATION
// ============================================================

internal static class MultiplayerNetworkUpdateBridge
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

        if (_timer < 0.05f)
        {
            return;
        }

        _timer =
            0f;

        RemoteSessionProcessor
            .Process(
                dt
            );

        RemotePlayerManager
            .Update(
                dt
            );
    }

    public static void Clear()
    {
        RemoteSessionProcessor
            .Clear();

        RemoteSessionRegistry
            .Clear();
    }
}




















// ============================================================
// SESSION VALIDATOR
// ============================================================

internal static class MultiplayerSessionValidator
{
    public static bool CanRunCampaign()
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

    public static bool CanReceiveRemotePlayers()
    {
        if (!CanRunCampaign())
        {
            return false;
        }

        if (
            !MultiplayerConnectionStatus
                .IsReady())
        {
            /*
             * Remote snapshot data can still exist before
             * Ready, but it must not modify Campaign objects.
             */
            return true;
        }

        return true;
    }
}




















// ============================================================
// HOST CLIENT SNAPSHOT HANDLER
// ============================================================

internal static class HostClientSnapshotHandler
{
    public static bool TryRead(
        byte[] payload,
        out string id,
        out string name,
        out float x,
        out float y,
        out int partySize)
    {
        id =
            null;

        name =
            "Player";

        x =
            0f;

        y =
            0f;

        partySize =
            1;

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
                id =
                    reader.ReadString();

                name =
                    reader.ReadString();

                x =
                    reader.ReadSingle();

                y =
                    reader.ReadSingle();

                partySize =
                    reader.ReadInt32();
            }

            if (
                string.IsNullOrWhiteSpace(
                    id))
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

            name =
                NetworkUtilities
                    .SafeName(
                        name
                    );

            partySize =
                NetworkUtilities
                    .SafePartySize(
                        partySize
                    );

            return true;
        }
        catch
        {
            return false;
        }
    }
}




















// ============================================================
// REMOTE PLAYER NETWORK ADAPTER
// ============================================================

internal static class RemotePlayerNetworkAdapter
{
    public static void ReceiveSnapshot(
        byte[] payload)
    {
        string id;
        string name;
        float x;
        float y;
        int partySize;

        if (
            !HostClientSnapshotHandler.TryRead(
                payload,
                out id,
                out name,
                out x,
                out y,
                out partySize))
        {
            return;
        }

        if (
            id ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        RemotePlayerCommand command =
            RemotePlayerCommandFactory
                .CreateState(
                    id,
                    name,
                    x,
                    y,
                    partySize
                );

        if (command == null)
        {
            return;
        }

        CampaignThreadDispatcher.Enqueue(
            () =>
            {
                RemotePlayerCommandQueue
                    .Enqueue(
                        command
                    );
            }
        );
    }

    public static void ReceiveJoin(
        byte[] payload)
    {
        string id;
        string name;
        float x;
        float y;
        int partySize;

        if (
            !HostClientSnapshotHandler.TryRead(
                payload,
                out id,
                out name,
                out x,
                out y,
                out partySize))
        {
            return;
        }

        if (
            id ==
            LocalPlayerState.GetNetworkId())
        {
            return;
        }

        RemotePlayerCommand command =
            RemotePlayerCommandFactory
                .CreateJoin(
                    id,
                    name,
                    x,
                    y,
                    partySize
                );

        if (command == null)
        {
            return;
        }

        CampaignThreadDispatcher.Enqueue(
            () =>
            {
                RemotePlayerCommandQueue
                    .Enqueue(
                        command
                    );
            }
        );
    }
}




















// ============================================================
// REMOTE PLAYER SESSION MAINTENANCE
// ============================================================

internal static class RemotePlayerSessionMaintenance
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

        RemotePlayerSessionData[] sessions =
            RemoteSessionRegistry
                .Snapshot();

        if (sessions == null)
        {
            return;
        }

        for (
            int i = 0;
            i < sessions.Length;
            i++)
        {
            RemotePlayerSessionData session =
                sessions[i];

            if (session == null)
            {
                continue;
            }

            if (
                now -
                session.LastUpdateUtc >
                Timeout)
            {
                session.Connected =
                    false;

                session.Ready =
                    false;

                RemoteSessionProcessor
                    .Process(
                        0f
                    );
            }
        }
    }
}




















// ============================================================
// PLAYER READY PACKET
// ============================================================

internal static class PlayerReadyPacket
{
    public static byte[] Build()
    {
        return
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
                        DateTime.UtcNow.Ticks
                    );
                }
            );
    }

    public static bool Read(
        byte[] payload,
        out string id,
        out string name)
    {
        id = null;
        name = "Player";

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
                id =
                    reader.ReadString();

                name =
                    reader.ReadString();

                if (
                    string.IsNullOrWhiteSpace(
                        id))
                {
                    return false;
                }

                name =
                    NetworkUtilities.SafeName(
                        name
                    );

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
// SESSION HANDSHAKE
// ============================================================

internal static class SessionHandshake
{
    public static byte[] BuildHello()
    {
        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        NetworkProtocol.Version
                    );

                    writer.Write(
                        LocalPlayerState
                            .GetNetworkId()
                    );

                    writer.Write(
                        LocalPlayerState
                            .GetDisplayName()
                    );
                }
            );
    }

    public static bool ReadHello(
        byte[] payload,
        out string playerId,
        out string playerName)
    {
        playerId = null;
        playerName = "Player";

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
                byte version =
                    reader.ReadByte();

                string requestedId =
                    reader.ReadString();

                string requestedName =
                    reader.ReadString();

                if (
                    version !=
                    NetworkProtocol.Version)
                {
                    return false;
                }

                if (
                    string.IsNullOrWhiteSpace(
                        requestedName))
                {
                    requestedName =
                        "Player";
                }

                /*
                 * The server owns actual player IDs.
                 */

                playerId =
                    requestedId;

                playerName =
                    NetworkUtilities.SafeName(
                        requestedName
                    );

                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public static byte[] BuildWelcome(
        string assignedId,
        string message)
    {
        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        assignedId ??
                        ""
                    );

                    writer.Write(
                        NetworkUtilities.SafeName(
                            message
                        )
                    );

                    writer.Write(
                        MultiplayerSessionId
                            .Get()
                    );
                }
            );
    }

    public static bool ReadWelcome(
        byte[] payload,
        out string assignedId,
        out string message)
    {
        assignedId = null;
        message = "";

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
                assignedId =
                    reader.ReadString();

                message =
                    reader.ReadString();

                if (
                    stream.Position <
                    stream.Length)
                {
                    reader.ReadString();
                }

                if (
                    string.IsNullOrWhiteSpace(
                        assignedId))
                {
                    return false;
                }

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
// CONNECTION HANDSHAKE STATE
// ============================================================

internal static class HandshakeState
{
    private static readonly object Sync =
        new object();

    private static bool _helloSent;

    private static bool _welcomeReceived;

    private static bool _playerReadySent;

    private static string _serverAssignedId;

    public static bool HelloSent
    {
        get
        {
            lock (Sync)
            {
                return _helloSent;
            }
        }
    }

    public static bool WelcomeReceived
    {
        get
        {
            lock (Sync)
            {
                return _welcomeReceived;
            }
        }
    }

    public static bool PlayerReadySent
    {
        get
        {
            lock (Sync)
            {
                return _playerReadySent;
            }
        }
    }

    public static string ServerAssignedId
    {
        get
        {
            lock (Sync)
            {
                return _serverAssignedId;
            }
        }
    }

    public static void SetHelloSent()
    {
        lock (Sync)
        {
            _helloSent =
                true;
        }
    }

    public static void SetWelcome(
        string id)
    {
        lock (Sync)
        {
            _welcomeReceived =
                true;

            _serverAssignedId =
                id;
        }
    }

    public static void SetPlayerReadySent()
    {
        lock (Sync)
        {
            _playerReadySent =
                true;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _helloSent =
                false;

            _welcomeReceived =
                false;

            _playerReadySent =
                false;

            _serverAssignedId =
                null;
        }
    }
}




















// ============================================================
// NETWORK IDENTITY SERVICE
// ============================================================

internal static class NetworkIdentityService
{
    private static readonly object Sync =
        new object();

    private static string _assignedId;

    public static string GetCurrentId()
    {
        lock (Sync)
        {
            if (
                !string.IsNullOrWhiteSpace(
                    _assignedId))
            {
                return _assignedId;
            }
        }

        return
            LocalPlayerState
                .GetNetworkId();
    }

    public static void SetAssignedId(
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
            _assignedId =
                id;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _assignedId =
                null;
        }
    }
}




















// ============================================================
// NETWORK PLAYER SNAPSHOT SERVICE
// ============================================================

internal static class NetworkPlayerSnapshotService
{
    private static float _sendTimer;

    private static CampaignVec2 _lastPosition;

    private static int _lastPartySize = 1;

    private static bool _hasLast;

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

        _sendTimer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_sendTimer < 0.10f)
        {
            return;
        }

        _sendTimer =
            0f;

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

        int partySize =
            CampaignWorld
                .GetMainPartySize();

        string playerName =
            LocalPlayerState
                .GetDisplayName();

        if (
            !_hasLast ||
            ShouldSend(
                position,
                partySize))
        {
            client.SendLocalPlayerState(
                position,
                partySize
            );

            _lastPosition =
                position;

            _lastPartySize =
                partySize;

            _hasLast =
                true;
        }
    }

    private static bool ShouldSend(
        CampaignVec2 position,
        int partySize)
    {
        float dx =
            position.X -
            _lastPosition.X;

        float dy =
            position.Y -
            _lastPosition.Y;

        float distanceSquared =
            dx * dx +
            dy * dy;

        if (
            distanceSquared >
            0.000001f)
        {
            return true;
        }

        if (
            partySize !=
            _lastPartySize)
        {
            return true;
        }

        return false;
    }

    public static void Reset()
    {
        _sendTimer =
            0f;

        _lastPosition =
            new CampaignVec2(
                new Vec2(
                    0f,
                    0f
                ),
                true
            );

        _lastPartySize =
            1;

        _hasLast =
            false;
    }
}




















// ============================================================
// CLIENT NETWORK MESSAGE ROUTER
// ============================================================

internal static class ClientNetworkMessageRouter
{
    public static void Route(
        NetworkMessage message)
    {
        if (message == null)
        {
            return;
        }

        switch (message.Type)
        {
            case NetworkPacketType.Welcome:

                HandleWelcome(
                    message.Payload
                );

                break;

            case NetworkPacketType.WorldBegin:

                WorldTransferService
                    .ReceiveBegin(
                        message.Payload
                    );

                break;

            case NetworkPacketType.WorldChunk:

                WorldTransferService
                    .ReceiveChunk(
                        message.Payload
                    );

                break;

            case NetworkPacketType.WorldComplete:

                WorldTransferService
                    .ReceiveComplete(
                        message.Payload
                    );

                WorldReadyHandler
                    .Handle();

                break;

            case NetworkPacketType.PlayerSnapshot:

                ClientRemoteSnapshotProcessor
                    .Process(
                        message.Payload
                    );

                break;

            case NetworkPacketType.PlayerLeave:

                RemotePlayerLeaveProcessor
                    .Process(
                        message.Payload
                    );

                break;

            case NetworkPacketType.WorldJoinAck:

                HandleWorldJoinAck(
                    message.Payload
                );

                break;

            case NetworkPacketType.Error:

                HandleError(
                    message.Payload
                );

                break;
        }
    }

    private static void HandleWelcome(
        byte[] payload)
    {
        string assignedId;
        string message;

        if (
            !SessionHandshake
                .ReadWelcome(
                    payload,
                    out assignedId,
                    out message))
        {
            return;
        }

        NetworkIdentityService
            .SetAssignedId(
                assignedId
            );

        HandshakeState
            .SetWelcome(
                assignedId
            );

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .SynchronizingWorld
            );

        MultiplayerSessionState
            .StartClient();

        CampaignMessageFeed.Show(
            string.IsNullOrWhiteSpace(
                message)
                ? "Connected to Host."
                : message
        );
    }

    private static void HandleWorldJoinAck(
        byte[] payload)
    {
        string message =
            NetworkProtocol
                .ReadString(
                    payload
                );

        if (
            string.IsNullOrWhiteSpace(
                message))
        {
            message =
                "World synchronization completed.";
        }

        CampaignMessageFeed.Show(
            message
        );

        WorldSynchronizationController
            .SetClientJoined(
                true
            );
    }

    private static void HandleError(
        byte[] payload)
    {
        string message =
            NetworkProtocol
                .ReadString(
                    payload
                );

        if (
            string.IsNullOrWhiteSpace(
                message))
        {
            message =
                "Network error.";
        }

        HostConsole.WriteLine(
            "[!] " +
            message
        );
    }
}


















// ============================================================
// CAMPAIGN SESSION COORDINATOR
// ============================================================

public static class CampaignSessionCoordinator
{
    private static readonly object Sync =
        new object();

    private static bool _initialized;

    private static bool _mapReady;

    private static bool _localPlayerReady;

    private static bool _remotePlayerSystemReady;

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

    public static bool MapReady
    {
        get
        {
            lock (Sync)
            {
                return _mapReady;
            }
        }
    }

    public static bool LocalPlayerReady
    {
        get
        {
            lock (Sync)
            {
                return _localPlayerReady;
            }
        }
    }

    public static bool RemotePlayerSystemReady
    {
        get
        {
            lock (Sync)
            {
                return _remotePlayerSystemReady;
            }
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            _initialized =
                true;

            _mapReady =
                false;

            _localPlayerReady =
                false;

            _remotePlayerSystemReady =
                true;
        }
    }

    public static void Update()
    {
        bool campaignReady =
            false;

        try
        {
            campaignReady =
                Campaign.Current != null;
        }
        catch
        {
            campaignReady =
                false;
        }

        lock (Sync)
        {
            _mapReady =
                campaignReady;

            try
            {
                _localPlayerReady =
                    campaignReady &&
                    Hero.MainHero != null &&
                    MobileParty.MainParty != null;
            }
            catch
            {
                _localPlayerReady =
                    false;
            }
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _initialized =
                false;

            _mapReady =
                false;

            _localPlayerReady =
                false;

            _remotePlayerSystemReady =
                false;
        }
    }
}




















// ============================================================
// HOST CLIENT STATE
// ============================================================

public sealed class HostClientState
{
    public string PlayerId;

    public string PlayerName;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Connected;

    public bool Ready;

    public DateTime LastUpdateUtc;

    public long Sequence;

    public HostClientState()
    {
        PlayerId =
            "";

        PlayerName =
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

        Connected =
            false;

        Ready =
            false;

        LastUpdateUtc =
            DateTime.UtcNow;

        Sequence =
            0;
    }
}




















// ============================================================
// HOST CLIENT STATE REGISTRY
// ============================================================

internal static class HostClientStateRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        HostClientState>
        States =
            new Dictionary<
                string,
                HostClientState>();

    public static HostClientState GetOrCreate(
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
            HostClientState state;

            if (
                !States.TryGetValue(
                    id,
                    out state))
            {
                state =
                    new HostClientState
                    {
                        PlayerId =
                            id
                    };

                States.Add(
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
            !NetworkUtilities.IsValidPosition(
                position.X,
                position.Y))
        {
            return;
        }

        HostClientState state =
            GetOrCreate(
                id
            );

        if (state == null)
        {
            return;
        }

        lock (Sync)
        {
            state.PlayerName =
                NetworkUtilities
                    .SafeName(
                        name
                    );

            state.Position =
                position;

            state.PartySize =
                NetworkUtilities
                    .SafePartySize(
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
            HostClientState state;

            if (
                States.TryGetValue(
                    id,
                    out state))
            {
                state.Ready =
                    ready;

                state.Connected =
                    true;
            }
        }
    }

    public static void SetDisconnected(
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
            HostClientState state;

            if (
                States.TryGetValue(
                    id,
                    out state))
            {
                state.Connected =
                    false;

                state.Ready =
                    false;

                state.LastUpdateUtc =
                    DateTime.UtcNow;
            }
        }
    }

    public static HostClientState[] Snapshot()
    {
        lock (Sync)
        {
            HostClientState[] result =
                new HostClientState[
                    States.Count
                ];

            int index =
                0;

            foreach (
                HostClientState state
                in States.Values)
            {
                result[index++] =
                    state;
            }

            return result;
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
            States.Remove(
                id
            );
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            States.Clear();
        }
    }
}




















// ============================================================
// HOST CLIENT SNAPSHOT BROADCASTER
// ============================================================

internal static class HostClientSnapshotBroadcaster
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
            CampaignWorld
                .GetMainPartySize();

        byte[] hostPayload =
            HostSnapshotBuilder.Build(
                LocalPlayerState
                    .GetNetworkId(),
                LocalPlayerState
                    .GetDisplayName(),
                hostPosition.X,
                hostPosition.Y,
                hostPartySize
            );

        if (hostPayload != null)
        {
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

                client.Send(
                    hostMessage
                );
            }
        }

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

                receiver.Send(
                    clientMessage
                );
            }
        }
    }
}




















// ============================================================
// HOST CONNECTION EVENTS
// ============================================================

internal static class HostConnectionEvents
{
    public static void Connected(
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

        HostClientStateRegistry
            .GetOrCreate(
                client.PlayerId
            );

        HostClientStateRegistry
            .SetReady(
                client.PlayerId,
                false
            );
    }

    public static void Ready(
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

        HostClientStateRegistry
            .Update(
                client.PlayerId,
                client.PlayerName,
                new CampaignVec2(
                    new Vec2(
                        client.LastX,
                        client.LastY
                    ),
                    true
                ),
                client.LastPartySize
            );

        HostClientStateRegistry
            .SetReady(
                client.PlayerId,
                true
            );
    }

    public static void Disconnected(
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

        HostClientStateRegistry
            .SetDisconnected(
                client.PlayerId
            );

        RemoteSessionProcessor
            .EnqueueLeave(
                client.PlayerId
            );
    }
}




















// ============================================================
// CONNECTION MONITOR
// ============================================================

internal static class MultiplayerConnectionMonitor
{
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

        RemotePlayerSessionMaintenance
            .Update(
                1f
            );

        if (
            MultiplayerNetworkClient
                .Instance
                .IsConnected)
        {
            MultiplayerConnectionStatus
                .Set(
                    MultiplayerConnectionState
                        .Ready
                );
        }
    }
}




















// ============================================================
// MASTER SESSION UPDATE
// ============================================================

internal static class MasterSessionUpdate
{
    public static void Update(
        float dt)
    {
        CampaignSessionCoordinator
            .Update();

        if (
            Campaign.Current == null)
        {
            return;
        }

        MultiplayerConnectionMonitor
            .Update(
                dt
            );

        RemotePlayerMasterService
            .Update(
                dt
            );

        HostPlayerSnapshotService
            .Update(
                dt,
                MultiplayerCampaignSubModule
                    .GetHost()
            );

        HostClientSnapshotBroadcaster
            .Update(
                dt,
                MultiplayerCampaignSubModule
                    .GetHost()
            );

        RemotePlayerMapRegistry
            .Update();
    }
}




















// ============================================================
// FINAL SESSION RESET
// ============================================================

internal static class FinalSessionReset
{
    public static void ResetAll()
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
            WorldTransferService
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
            MultiplayerWorldSyncState
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
            RemotePlayerFinalCleanup
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
            MasterCleanupRegistry
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

        CampaignSessionCoordinator
            .Reset();

        CampaignInitializationGuard
            .Reset();

        CampaignTickDispatcher
            .Reset();

        MultiplayerSessionId
            .Reset();

        NetworkIdentityService
            .Reset();

        PlayerSnapshotState
            .Reset();

        PlayerSnapshotSendTimer
            .Reset();

        PlayerSnapshotState
            .Reset();

        FinalSessionFlags
            .Reset();
    }
}




















// ============================================================
// FINAL SESSION FLAGS
// ============================================================

internal static class FinalSessionFlags
{
    private static readonly object Sync =
        new object();

    private static bool _started;

    private static bool _host;

    private static bool _client;

    public static bool Started
    {
        get
        {
            lock (Sync)
            {
                return _started;
            }
        }
    }

    public static bool IsHost
    {
        get
        {
            lock (Sync)
            {
                return _host;
            }
        }
    }

    public static bool IsClient
    {
        get
        {
            lock (Sync)
            {
                return _client;
            }
        }
    }

    public static void StartHost()
    {
        lock (Sync)
        {
            _started =
                true;

            _host =
                true;

            _client =
                false;
        }
    }

    public static void StartClient()
    {
        lock (Sync)
        {
            _started =
                true;

            _host =
                false;

            _client =
                true;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _started =
                false;

            _host =
                false;

            _client =
                false;
        }
    }
}




















// ============================================================
// SESSION STATUS UPDATER
// ============================================================

internal static class SessionStatusUpdater
{
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

        if (_timer < 0.25f)
        {
            return;
        }

        _timer =
            0f;

        MultiplayerNetworkClient client =
            MultiplayerNetworkClient
                .Instance;

        bool connected =
            client != null &&
            client.IsConnected;

        MultiplayerUIStateManager
            .Current
            .SetConnected(
                connected
            );

        MultiplayerUIStateManager
            .Current
            .SetWorldReady(
                client != null &&
                client.IsWorldLoaded
            );

        MultiplayerUIStateManager
            .Current
            .SetWorldSyncing(
                MultiplayerWorldSyncState
                    .Receiving
            );

        MultiplayerUIStateManager
            .Current
            .SetRemotePlayers(
                RemotePlayerManager
                    .Snapshot()
                    .Length
            );

        MultiplayerCampaignHost host =
            MultiplayerCampaignSubModule
                .GetHost();

        MultiplayerUIStateManager
            .Current
            .SetServerRunning(
                host != null
            );
    }
}




















// ============================================================
// NETWORK PACKET PROCESSOR
// ============================================================

internal static class NetworkPacketProcessor
{
    public static void Process(
        NetworkMessage message)
    {
        if (message == null)
        {
            return;
        }

        switch (message.Type)
        {
            case NetworkPacketType.Welcome:

                ProcessWelcome(
                    message.Payload
                );

                break;

            case NetworkPacketType.WorldBegin:

                WorldTransferService
                    .ReceiveBegin(
                        message.Payload
                    );

                break;

            case NetworkPacketType.WorldChunk:

                WorldTransferService
                    .ReceiveChunk(
                        message.Payload
                    );

                break;

            case NetworkPacketType.WorldComplete:

                WorldTransferService
                    .ReceiveComplete(
                        message.Payload
                    );

                WorldReadyHandler
                    .Handle();

                break;

            case NetworkPacketType.PlayerSnapshot:

                ProcessPlayerSnapshot(
                    message.Payload
                );

                break;

            case NetworkPacketType.PlayerLeave:

                RemotePlayerLeaveReceiver
                    .Receive(
                        message.Payload
                    );

                break;

            case NetworkPacketType.WorldJoinAck:

                ProcessWorldJoinAck(
                    message.Payload
                );

                break;

            case NetworkPacketType.WorldPartySnapshot:

                WorldPartySynchronizer
                    .EnqueueSnapshot(
                        message.Payload
                    );

                break;

            case NetworkPacketType.Error:

                ProcessError(
                    message.Payload
                );

                break;
        }
    }

    private static void ProcessWelcome(
        byte[] payload)
    {
        string assignedId;
        string message;

        if (
            !SessionHandshake
                .ReadWelcome(
                    payload,
                    out assignedId,
                    out message))
        {
            return;
        }

        NetworkIdentityService
            .SetAssignedId(
                assignedId
            );

        HandshakeState
            .SetWelcome(
                assignedId
            );

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .SynchronizingWorld
            );

        MultiplayerUIStateManager
            .Current
            .SetStatus(
                string.IsNullOrWhiteSpace(
                    message)
                    ? "Connected"
                    : message
            );
    }

    private static void ProcessPlayerSnapshot(
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

        RemotePlayerSnapshotLoop
            .Enqueue(
                snapshot
            );
    }

    private static void ProcessWorldJoinAck(
        byte[] payload)
    {
        string message =
            NetworkProtocol
                .ReadString(
                    payload
                );

        MultiplayerUIStateManager
            .Current
            .SetStatus(
                string.IsNullOrWhiteSpace(
                    message)
                    ? "World synchronized"
                    : message
            );

        WorldSynchronizationController
            .SetClientJoined(
                true
            );
    }

    private static void ProcessError(
        byte[] payload)
    {
        string message =
            NetworkProtocol
                .ReadString(
                    payload
                );

        HostConsole.WriteLine(
            "[!] " +
            (
                string.IsNullOrWhiteSpace(
                    message)
                    ? "Network error."
                    : message
            )
        );

        MultiplayerUIStateManager
            .Current
            .SetStatus(
                string.IsNullOrWhiteSpace(
                    message)
                    ? "Network error."
                    : message
            );
    }
}




















// ============================================================
// NETWORK UPDATE CONTROLLER
// ============================================================

internal static class NetworkUpdateController
{
    private static float _timer;

    public static void Update(
        float dt)
    {
        MultiplayerNetworkClient
            .Instance
            .Update();

        _timer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_timer < 0.05f)
        {
            return;
        }

        _timer =
            0f;

        NetworkUpdateController2
            .Update(
                dt
            );
    }
}




















// ============================================================
// NETWORK UPDATE CONTROLLER 2
// ============================================================

internal static class NetworkUpdateController2
{
    public static void Update(
        float dt)
    {
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
            !MultiplayerCampaignGameState
                .CampaignReady)
        {
            return;
        }

        PlayerSnapshotSendTimer
            .Update(
                dt
            );

        RemotePlayerUpdatePipeline
            .Update(
                dt
            );

        SessionStatusUpdater
            .Update(
                dt
            );

        MasterSessionUpdate
            .Update(
                dt
            );

        RemotePlayerMapMarkerController
            .Tick(
                dt
            );
    }
}




















// ============================================================
// NETWORK CLIENT SEND OVERLOAD
// ============================================================

internal static class NetworkClientSendAdapter
{
    public static void SendSnapshot(
        CampaignPlayerSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        byte[] payload =
            CampaignPlayerSnapshotCodec
                .Encode(
                    snapshot
                );

        if (payload == null)
        {
            return;
        }

        MultiplayerNetworkClient
            .Instance
            .Send(
                NetworkPacketType.PlayerSnapshot,
                payload
            );
    }
}




















// ============================================================
// PLAYER CONNECTION EVENT
// ============================================================

internal static class MultiplayerPlayerConnectionEvents
{
    public static void OnConnected(
        string playerId,
        string playerName)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        RemotePlayerSessionData session =
            RemoteSessionRegistry
                .GetOrCreate(
                    playerId
                );

        if (session == null)
        {
            return;
        }

        session.Connected =
            true;

        session.Ready =
            false;

        session.WorldSynchronized =
            false;

        session.Name =
            NetworkUtilities
                .SafeName(
                    playerName
                );

        session.ConnectedUtc =
            DateTime.UtcNow;

        session.LastUpdateUtc =
            DateTime.UtcNow;

        RemotePlayerNameRegistry
            .Set(
                playerId,
                session.Name
            );
    }

    public static void OnReady(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        RemotePlayerSessionData session;

        if (
            !RemoteSessionRegistry
                .TryGet(
                    playerId,
                    out session))
        {
            return;
        }

        if (session == null)
        {
            return;
        }

        session.Connected =
            true;

        session.Ready =
            true;

        session.WorldSynchronized =
            true;

        session.LastUpdateUtc =
            DateTime.UtcNow;
    }

    public static void OnDisconnected(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return;
        }

        RemoteSessionRegistry
            .Remove(
                playerId
            );

        RemotePlayerManager
            .RemoveSessionInternal(
                playerId
            );

        RemotePlayerMapMarkerCleanup
            .Remove(
                playerId
            );

        RemotePlayerStateProjector
            .Remove(
                playerId
            );
    }
}




















// ============================================================
// SESSION JOIN PACKET
// ============================================================

internal static class SessionJoinPacket
{
    public static byte[] Build(
        string playerId,
        string playerName)
    {
        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        playerId ??
                        ""
                    );

                    writer.Write(
                        NetworkUtilities
                            .SafeName(
                                playerName
                            )
                    );

                    writer.Write(
                        DateTime.UtcNow.Ticks
                    );
                }
            );
    }

    public static bool Read(
        byte[] payload,
        out string playerId,
        out string playerName)
    {
        playerId =
            null;

        playerName =
            "Player";

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
                playerId =
                    reader.ReadString();

                playerName =
                    reader.ReadString();

                if (
                    stream.Position <
                    stream.Length)
                {
                    reader.ReadInt64();
                }

                if (
                    !NetworkStateValidator
                        .IsValidPlayerId(
                            playerId))
                {
                    return false;
                }

                playerName =
                    NetworkUtilities
                        .SafeName(
                            playerName
                        );

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
// SESSION WELCOME PACKET
// ============================================================

internal static class SessionWelcomePacket
{
    public static byte[] Build(
        string assignedId,
        string message)
    {
        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        assignedId ??
                        ""
                    );

                    writer.Write(
                        NetworkUtilities
                            .SafeName(
                                message
                            )
                    );

                    writer.Write(
                        MultiplayerSessionId
                            .Get()
                    );
                }
            );
    }

    public static bool Read(
        byte[] payload,
        out string assignedId,
        out string message)
    {
        assignedId =
            null;

        message =
            "";

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
                assignedId =
                    reader.ReadString();

                message =
                    reader.ReadString();

                if (
                    stream.Position <
                    stream.Length)
                {
                    reader.ReadString();
                }

                if (
                    string.IsNullOrWhiteSpace(
                        assignedId))
                {
                    return false;
                }

                message =
                    string.IsNullOrWhiteSpace(
                        message)
                        ? "Connected"
                        : message;

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
// SESSION JOIN CONTROLLER
// ============================================================

internal static class SessionJoinController
{
    public static void CreateRemotePlayer(
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
            LocalPlayerState
                .GetNetworkId())
        {
            return;
        }

        if (
            Campaign.Current == null)
        {
            return;
        }

        RemotePlayerManager
            .ReceiveSessionInternal(
                id,
                NetworkUtilities.SafeName(
                    name
                ),
                position,
                NetworkUtilities.SafePartySize(
                    partySize
                )
            );

        RemotePlayerMapRegistry
            .Update();

        CampaignRemotePlayerMarkerRegistry
            .Update();
    }

    public static void DestroyRemotePlayer(
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

        RemotePlayerMapMarkerCleanup
            .Remove(
                id
            );
    }
}




















// ============================================================
// TWO PLAYER SESSION VALIDATOR
// ============================================================

internal static class TwoPlayerSessionValidator
{
    public static bool CanAddClient(
        MultiplayerCampaignHost host)
    {
        if (host == null)
        {
            return false;
        }

        HostClientConnection[] clients =
            host.GetClientsSnapshot();

        if (clients == null)
        {
            return true;
        }

        int active =
            0;

        for (
            int i = 0;
            i < clients.Length;
            i++)
        {
            HostClientConnection client =
                clients[i];

            if (
                client != null &&
                client.IsConnected)
            {
                active++;
            }
        }

        /*
         * Two players total:
         *
         * Host + 1 Client.
         */

        return active < 1;
    }
}




















// ============================================================
// SESSION DIAGNOSTICS
// ============================================================

internal static class MultiplayerSessionDiagnostics
{
    public static bool ValidateLocal()
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

    public static bool ValidateRemote(
        RemotePlayerState state)
    {
        if (state == null)
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

    public static int GetRemoteCount()
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
                ValidateRemote(
                    states[i]))
            {
                count++;
            }
        }

        return count;
    }
}




















// ============================================================
// FINAL NETWORK SESSION CONTROLLER
// ============================================================

public static class FinalNetworkSessionController
{
    public static bool StartServer()
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

        MultiplayerSessionState
            .StartHost();

        FinalSessionFlags
            .StartHost();

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .Connected
            );

        return true;
    }

    public static bool ConnectToServer(
        string address)
    {
        if (
            string.IsNullOrWhiteSpace(
                address))
        {
            return false;
        }

        FinalSessionFlags
            .StartClient();

        MultiplayerSessionState
            .StartClient();

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .Connecting
            );

        MultiplayerNetworkClient
            .Instance
            .Connect(
                address.Trim()
            );

        return true;
    }

    public static void Disconnect()
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

        FinalPlayerSyncService
            .Reset();

        FinalStateCleanup
            .Execute();
    }
}




















// ============================================================
// FINAL NETWORK MESSAGE ROUTER
// ============================================================

internal static class FinalNetworkMessageRouter
{
    public static void Route(
        NetworkMessage message)
    {
        if (message == null)
        {
            return;
        }

        FinalErrorGuard.Execute(
            () =>
            {
                switch (message.Type)
                {
                    case NetworkPacketType.PlayerSnapshot:

                        ClientRemoteSnapshotProcessor
                            .Process(
                                message.Payload
                            );

                        ClientCampaignSnapshotReceiver
                            .Receive(
                                message.Payload
                            );

                        break;

                    case NetworkPacketType.PlayerLeave:

                        RemotePlayerLeaveReceiver
                            .Receive(
                                message.Payload
                            );

                        break;

                    case NetworkPacketType.WorldBegin:

                        WorldTransferService
                            .ReceiveBegin(
                                message.Payload
                            );

                        break;

                    case NetworkPacketType.WorldChunk:

                        WorldTransferService
                            .ReceiveChunk(
                                message.Payload
                            );

                        break;

                    case NetworkPacketType.WorldComplete:

                        WorldTransferService
                            .ReceiveComplete(
                                message.Payload
                            );

                        WorldReadyHandler
                            .Handle();

                        break;

                    case NetworkPacketType.Welcome:

                        ProcessWelcome(
                            message.Payload
                        );

                        break;

                    case NetworkPacketType.WorldJoinAck:

                        ProcessWorldJoinAck(
                            message.Payload
                        );

                        break;

                    case NetworkPacketType.Error:

                        ProcessError(
                            message.Payload
                        );

                        break;
                }
            }
            );
    }

    private static void ProcessWelcome(
        byte[] payload)
    {
        string assignedId;
        string message;

        if (
            !SessionWelcomePacket
                .Read(
                    payload,
                    out assignedId,
                    out message))
        {
            /*
             * Backward compatible fallback.
             */

            if (
                SessionHandshake
                    .ReadWelcome(
                        payload,
                        out assignedId,
                        out message))
            {
                NetworkIdentityService
                    .SetAssignedId(
                        assignedId
                    );

                HandshakeState
                    .SetWelcome(
                        assignedId
                    );

                MultiplayerConnectionStatus
                    .Set(
                        MultiplayerConnectionState
                            .SynchronizingWorld
                    );
            }

            return;
        }

        NetworkIdentityService
            .SetAssignedId(
                assignedId
            );

        HandshakeState
            .SetWelcome(
                assignedId
            );

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .SynchronizingWorld
            );

        MultiplayerSessionState
            .StartClient();

        MultiplayerUIStateManager
            .Current
            .SetStatus(
                message
            );
    }

    private static void ProcessWorldJoinAck(
        byte[] payload)
    {
        string message =
            NetworkProtocol
                .ReadString(
                    payload
                );

        WorldSynchronizationController
            .SetClientJoined(
                true
            );

        MultiplayerUIStateManager
            .Current
            .SetStatus(
                string.IsNullOrWhiteSpace(
                    message)
                    ? "World joined."
                    : message
            );
    }

    private static void ProcessError(
        byte[] payload)
    {
        string message =
            NetworkProtocol
                .ReadString(
                    payload
                );

        HostConsole.WriteLine(
            "[!] " +
            (
                string.IsNullOrWhiteSpace(
                    message)
                    ? "Network error."
                    : message
            )
        );
    }
}




















// ============================================================
// END OF CURRENT SECTION
// ============================================================
// ============================================================
// FINAL CAMPAIGN NETWORK CONTROLLER
// ============================================================

public static class FinalCampaignNetworkController
{
    private static readonly object Sync =
        new object();

    private static bool _initialized;

    private static float _timer;

    private static float _snapshotTimer;

    public static bool IsInitialized
    {
        get
        {
            lock (Sync)
            {
                return _initialized;
            }
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized =
                true;

            _timer =
                0f;

            _snapshotTimer =
                0f;
        }

        MultiplayerConnectionStatus
            .Reset();

        HandshakeState
            .Reset();

        NetworkIdentityService
            .Reset();

        CampaignStateSynchronization
            .Reset();

        CampaignSessionCoordinator
            .Initialize();
    }

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

        if (
            Campaign.Current == null)
        {
            return;
        }

        Initialize();

        _timer +=
            dt;

        _snapshotTimer +=
            dt;

        /*
         * Process network packets frequently.
         */

        MultiplayerNetworkClient
            .Instance
            .Update();

        /*
         * Process all queued remote state on the
         * Campaign thread.
         */

        RemotePlayerSnapshotLoop
            .Process();

        RemotePlayerCommandProcessor
            .Process(
                dt
            );

        RemoteSessionProcessor
            .Process(
                dt
            );

        /*
         * Smooth remote movement.
         */

        RemotePlayerManager
            .Update(
                dt
            );

        /*
         * Update map-side logical representations.
         */

        if (_timer >= 0.05f)
        {
            _timer =
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

                    if (!state.Active)
                    {
                        continue;
                    }

                    RemotePlayerStateProjector
                        .Project(
                            state
                        );
                }
            }

            CampaignRemotePlayerMarkerRegistry
                .Update();

            RemotePlayerWorldViewRegistry
                .Update();
        }

        /*
         * Local position synchronization.
         */

        if (
            _snapshotTimer >=
            0.10f)
        {
            _snapshotTimer =
                0f;

            PlayerSnapshotSendTimer
                .Update(
                    0.10f
                );
        }

        RemotePlayerTimeoutService
            .Update(
                dt
            );

        SessionStatusUpdater
            .Update(
                dt
            );
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            _initialized =
                false;

            _timer =
                0f;

            _snapshotTimer =
                0f;
        }

        MultiplayerNetworkClient
            .Instance
            .Disconnect();

        MultiplayerCampaignSubModule
            .StopHost();

        RemotePlayerFinalCleanup
            .Clear();

        RemotePlayerMapRegistry
            .Clear();

        CampaignRemotePlayerMarkerRegistry
            .Clear();

        RemotePlayerWorldViewRegistry
            .Clear();

        CampaignStateSynchronization
            .Reset();

        CampaignSessionCoordinator
            .Reset();

        MultiplayerConnectionStatus
            .Reset();

        MultiplayerSessionState
            .Reset();

        MultiplayerCampaignGameState
            .Reset();

        NetworkIdentityService
            .Reset();

        HandshakeState
            .Reset();

        MultiplayerSessionId
            .Reset();
    }
}




















// ============================================================
// FINAL NETWORK HEALTH CHECK
// ============================================================

internal static class FinalNetworkHealthCheck
{
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

        if (_timer < 2f)
        {
            return;
        }

        _timer =
            0f;

        if (
            MultiplayerNetworkClient
                .Instance
                .IsConnected)
        {
            MultiplayerUIStateManager
                .Current
                .SetConnected(
                    true
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
// FINAL REMOTE PLAYER SESSION
// ============================================================

public static class FinalRemotePlayerSession
{
    public static void Join(
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
            !NetworkUtilities
                .IsValidPosition(
                    position.X,
                    position.Y))
        {
            return;
        }

        RemoteSessionProcessor
            .EnqueueJoin(
                id,
                name,
                position,
                partySize
            );

        RemotePlayerSnapshotLoop
            .Process();

        RemotePlayerCommandProcessor
            .Process(
                0.10f
            );

        CampaignMapIntegration
            .Update(
                0.10f
            );
    }

    public static void Update(
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
            !NetworkUtilities
                .IsValidPosition(
                    position.X,
                    position.Y))
        {
            return;
        }

        RemoteSessionProcessor
            .EnqueueState(
                id,
                name,
                position,
                partySize
            );

        RemoteSessionProcessor
            .Process(
                0.01f
            );
    }

    public static void Leave(
        string id)
    {
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

        RemotePlayerLeaveService
            .Remove(
                id
            );
    }
}




















// ============================================================
// FINAL SESSION TICK
// ============================================================

internal static class FinalSessionTick
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

        MultiplayerNetworkClient
            .Instance
            .Update();

        CampaignThreadDispatcher
            .Process();

        RemotePlayerSnapshotLoop
            .Process();

        RemotePlayerCommandProcessor
            .Process(
                dt
            );

        RemotePlayerManager
            .Update(
                dt
            );

        CampaignMapIntegration
            .Update(
                dt
            );

        _timer +=
            Math.Max(
                0f,
                Math.Min(
                    1f,
                    dt
                )
            );

        if (_timer >= 0.10f)
        {
            _timer =
                0f;

            PlayerSnapshotSendTimer
                .Update(
                    0.10f
                );

            RemotePlayerTimeoutService
                .Update(
                    0.10f
                );
        }
    }

    public static void Reset()
    {
        _timer =
            0f;

        CampaignMapIntegration
            .Clear();

        RemotePlayerSnapshotLoop
            .Clear();
    }
}




















// ============================================================
// FINAL CONNECTION ACCESSOR
// ============================================================

public static class MultiplayerCampaignConnection
{
    public static bool IsConnected
    {
        get
        {
            try
            {
                return
                    MultiplayerNetworkClient
                        .Instance
                        .IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsWorldLoaded
    {
        get
        {
            try
            {
                return
                    MultiplayerNetworkClient
                        .Instance
                        .IsWorldLoaded;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Disconnect()
    {
        FinalShutdown
            .Execute();
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


namespace MultiplayerCampaign
{


    /*
     * ============================================================
     * NETWORK PROTOCOL
     * ============================================================
     */

    internal enum NetworkPacketType : byte
    {
        Hello = 1,

        Welcome = 2,

        WorldBegin = 3,

        WorldChunk = 4,

        WorldComplete = 5,

        PlayerReady = 6,

        PlayerSnapshot = 7,

        PlayerLeave = 8,

        WorldPartySnapshot = 9,

        ResyncRequest = 10,

        Error = 11,

        WorldJoinTest = 12,

        WorldJoinAck = 13
    }




















    internal static class NetworkProtocol
    {
        public const byte Version = 1;

        public const int MaxPacketSize =
            4 * 1024 * 1024;

        public static byte[] BuildFrame(
            NetworkPacketType type,
            byte[] payload)
        {
            if (payload == null)
            {
                payload =
                    Array.Empty<byte>();
            }

            int bodyLength =
                2 +
                payload.Length;

            if (
                bodyLength >
                MaxPacketSize)
            {
                throw new InvalidOperationException(
                    "Packet too large."
                );
            }

            byte[] frame =
                new byte[
                    4 +
                    bodyLength
                ];

            Buffer.BlockCopy(
                BitConverter.GetBytes(
                    bodyLength
                ),
                0,
                frame,
                0,
                4
            );

            frame[4] =
                Version;

            frame[5] =
                (byte)type;

            if (payload.Length > 0)
            {
                Buffer.BlockCopy(
                    payload,
                    0,
                    frame,
                    6,
                    payload.Length
                );
            }

            return frame;
        }

        public static byte[] CreatePayload(
            Action<System.IO.BinaryWriter> action)
        {
            using (
                MemoryStream stream =
                    new MemoryStream())
            using (
                System.IO.BinaryWriter writer =
                    new System.IO.BinaryWriter(
                        stream,
                        Encoding.UTF8,
                        true))
            {
                action(writer);

                writer.Flush();

                return stream.ToArray();
            }
        }

        public static string ReadString(
            byte[] payload)
        {
            if (
                payload == null ||
                payload.Length == 0)
            {
                return "";
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
                return reader.ReadString();
            }
        }
    }




















    internal sealed class NetworkMessage
    {
        public NetworkPacketType Type;

        public byte[] Payload;
    }




















    /*
     * ============================================================
     * CLIENT NETWORK
     * ============================================================
     */

    public sealed class MultiplayerNetworkClient
    {
        private static readonly Lazy<
            MultiplayerNetworkClient>
            InstanceHolder =
            new Lazy<
                MultiplayerNetworkClient>(
                    () =>
                        new MultiplayerNetworkClient()
                );

        public static MultiplayerNetworkClient Instance =>
            InstanceHolder.Value;

        private readonly object
            _connectionLock =
                new object();

        private readonly object
            _sendLock =
                new object();

        private readonly ConcurrentQueue<
            NetworkMessage>
            _incoming =
            new ConcurrentQueue<
                NetworkMessage>();

        private TcpClient _tcpClient;

        private NetworkStream _stream;

        private CancellationTokenSource _cts;

        private MultiplayerCampaignVM _vm;

        private bool _connectionRunning;

        private bool _worldReady;

        private bool _worldLoaded;

        public bool IsConnected
        {
            get;
            private set;
        }

        public bool IsWorldLoaded
        {
            get
            {
                return _worldLoaded;
            }
        }

        public void SetViewModel(
            MultiplayerCampaignVM vm)
        {
            _vm = vm;
        }

        public void SetStatusDirect(
            string message)
        {
            _vm?.SetStatus(
                message
            );
        }

        /*
         * ========================================================
         * CONNECT
         * ========================================================
         */

        public void Connect(
            string ip)
        {
            lock (_connectionLock)
            {
                if (_connectionRunning)
                {
                    return;
                }

                DisconnectInternal();

                _cts =
                    new CancellationTokenSource();

                _connectionRunning =
                    true;

                _ =
                    ConnectAsync(
                        ip,
                        _cts.Token
                    );
            }
        }

        private async Task ConnectAsync(
            string ip,
            CancellationToken token)
        {
            try
            {
                TcpClient client =
                    new TcpClient();

                client.NoDelay =
                    true;

                await client.ConnectAsync(
                    ip,
                    25565
                );

                if (
                    token.IsCancellationRequested)
                {
                    client.Close();
                    return;
                }

                _tcpClient =
                    client;

                _stream =
                    client.GetStream();

                IsConnected =
                    true;

                HostConsole.WriteLine(
                    "[*] TCP connection established."
                );

                SendHello();

                _vm?.SetStatus(
                    "CONNECTED - RECEIVING MCC"
                );

                await ReceiveLoopAsync(
                    token
                );
            }
            catch (Exception ex)
            {
                IsConnected =
                    false;

                if (
                    !token.IsCancellationRequested)
                {
                    HostConsole.WriteLine(
                        "[!] TCP connection error: " +
                        ex.Message
                    );

                    _vm?.SetStatus(
                        "CONNECTION FAILED: " +
                        ex.Message
                    );
                }
            }
            finally
            {
                IsConnected =
                    false;

                lock (_connectionLock)
                {
                    _connectionRunning =
                        false;
                }
            }
        }

        /*
         * ========================================================
         * RECEIVE LOOP
         * ========================================================
         */

        private async Task ReceiveLoopAsync(
            CancellationToken token)
        {
            try
            {
                while (
                    !token.IsCancellationRequested)
                {
                    byte[] lengthBytes =
                        await ReadExactAsync(
                            _stream,
                            4,
                            token
                        );

                    if (lengthBytes == null)
                    {
                        return;
                    }

                    int length =
                        BitConverter.ToInt32(
                            lengthBytes,
                            0
                        );

                    if (
                        length < 2 ||
                        length >
                        NetworkProtocol.MaxPacketSize)
                    {
                        HostConsole.WriteLine(
                            "[!] Invalid packet length."
                        );

                        return;
                    }

                    byte[] body =
                        await ReadExactAsync(
                            _stream,
                            length,
                            token
                        );

                    if (body == null)
                    {
                        return;
                    }

                    byte version =
                        body[0];

                    NetworkPacketType type =
                        (NetworkPacketType)
                        body[1];

                    if (
                        version !=
                        NetworkProtocol.Version)
                    {
                        HostConsole.WriteLine(
                            "[!] Unsupported network protocol."
                        );

                        return;
                    }

                    byte[] payload =
                        new byte[
                            Math.Max(
                                0,
                                length - 2
                            )
                        ];

                    if (payload.Length > 0)
                    {
                        Buffer.BlockCopy(
                            body,
                            2,
                            payload,
                            0,
                            payload.Length
                        );
                    }

                    _incoming.Enqueue(
                        new NetworkMessage
                        {
                            Type = type,
                            Payload = payload
                        }
                    );
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private static async Task<byte[]>
            ReadExactAsync(
                NetworkStream stream,
                int count,
                CancellationToken token)
        {
            if (stream == null)
            {
                return null;
            }

            byte[] buffer =
                new byte[count];

            int offset = 0;

            while (offset < count)
            {
                int read =
                    await stream.ReadAsync(
                        buffer,
                        offset,
                        count - offset,
                        token
                    );

                if (read <= 0)
                {
                    return null;
                }

                offset += read;
            }

            return buffer;
        }

        /*
         * ========================================================
         * UPDATE
         * ========================================================
         */

        public void Update()
        {
            while (
                _incoming.TryDequeue(
                    out NetworkMessage message))
            {
                if (message == null)
                {
                    continue;
                }

                try
                {
                    ProcessMessage(message);
                }
                catch (Exception ex)
                {
                    HostConsole.WriteLine(
                        "[!] Network message error: " +
                        ex.Message
                    );
                }
            }
        }

        private void ProcessMessage(
            NetworkMessage message)
        {
            if (message == null)
            {
                return;
            }

            switch (message.Type)
            {
                case NetworkPacketType.Welcome:
                    HandleWelcome(
                        message.Payload
                    );
                    break;

                case NetworkPacketType.WorldBegin:
                    MultiplayerWorldTransfer
                        .HandleWorldBegin(
                            message.Payload
                        );
                    break;

                case NetworkPacketType.WorldChunk:
                    MultiplayerWorldTransfer
                        .HandleWorldChunk(
                            message.Payload
                        );
                    break;

                case NetworkPacketType.WorldComplete:
                    _worldReady = true;

                    MultiplayerWorldTransfer
                        .HandleWorldComplete(
                            message.Payload
                        );
                    break;

                case NetworkPacketType.PlayerSnapshot:
                    HandlePlayerSnapshot(
                        message.Payload
                    );
                    break;

                case NetworkPacketType.PlayerLeave:
                    HandlePlayerLeave(
                        message.Payload
                    );
                    break;

                case NetworkPacketType.WorldJoinAck:
                    HandleWorldJoinAck(
                        message.Payload
                    );
                    break;

                case NetworkPacketType.WorldPartySnapshot:
                    WorldPartySynchronizer
                        .EnqueueSnapshot(
                            message.Payload
                        );
                    break;

                case NetworkPacketType.Error:
                    HandleError(
                        message.Payload
                    );
                    break;
            }
        }

        private void HandleWelcome(
            byte[] payload)
        {
            string text =
                NetworkProtocol.ReadString(
                    payload
                );

            if (
                string.IsNullOrWhiteSpace(
                    text))
            {
                text =
                    "CONNECTED";
            }

            _vm?.SetStatus(
                text
            );
        }

        private void HandleWorldJoinAck(
            byte[] payload)
        {
            string text =
                NetworkProtocol.ReadString(
                    payload
                );

            if (
                string.IsNullOrWhiteSpace(
                    text))
            {
                text =
                    "WORLD JOINED";
            }

            CampaignMessageFeed.Show(
                text
            );
        }

        private void HandleError(
            byte[] payload)
        {
            string text =
                NetworkProtocol.ReadString(
                    payload
                );

            if (
                string.IsNullOrWhiteSpace(
                    text))
            {
                text =
                    "NETWORK ERROR";
            }

            HostConsole.WriteLine(
                "[!] " +
                text
            );

            _vm?.SetStatus(
                text
            );
        }

        private void HandlePlayerSnapshot(
            byte[] payload)
        {
            RemotePlayerManager
                .QueueSnapshot(
                    payload
                );
        }

        private void HandlePlayerLeave(
            byte[] payload)
        {
            RemotePlayerManager
                .QueueLeave(
                    payload
                );
        }

        public bool ConsumeWorldReady()
        {
            if (!_worldReady)
            {
                return false;
            }

            _worldReady = false;

            return true;
        }

        /*
         * ========================================================
         * HELLO
         * ========================================================
         */

        private void SendHello()
        {
            string name =
                LocalPlayerState
                    .GetDisplayName();

            byte[] payload =
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            name ??
                            "Player"
                        );
                    }
                );

            Send(
                NetworkPacketType.Hello,
                payload
            );
        }

        public void SendPlayerReady()
        {
            byte[] payload =
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            LocalPlayerState
                                .GetDisplayName()
                        );
                    }
                );

            Send(
                NetworkPacketType.PlayerReady,
                payload
            );
        }

        public void SendLocalPlayerState(
            CampaignVec2 position,
            int partySize)
        {
            byte[] payload =
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
                            position.X
                        );

                        writer.Write(
                            position.Y
                        );

                        writer.Write(
                            Math.Max(
                                1,
                                partySize
                            )
                        );
                    }
                );

            Send(
                NetworkPacketType.PlayerSnapshot,
                payload
            );
        }

        internal void Send(
            NetworkPacketType type,
            byte[] payload)
        {
            lock (_sendLock)
            {
                if (
                    !IsConnected ||
                    _stream == null)
                {
                    return;
                }

                try
                {
                    byte[] frame =
                        NetworkProtocol.BuildFrame(
                            type,
                            payload
                        );

                    _stream.Write(
                        frame,
                        0,
                        frame.Length
                    );

                    _stream.Flush();
                }
                catch
                {
                    IsConnected =
                        false;
                }
            }
        }

        public void Disconnect()
        {
            lock (_connectionLock)
            {
                DisconnectInternal();
            }
        }

        private void DisconnectInternal()
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            _cts = null;

            try
            {
                _stream?.Close();
            }
            catch
            {
            }

            try
            {
                _tcpClient?.Close();
            }
            catch
            {
            }

            _stream = null;
            _tcpClient = null;

            IsConnected =
                false;

            _worldReady =
                false;

            _worldLoaded =
                false;

            _connectionRunning =
                false;

            while (
                _incoming.TryDequeue(
                    out _))
            {
            }
        }
    }

}

namespace MultiplayerCampaignRebuildLayer
{

    internal static class MpcSession
    {
        private static readonly object Sync = new object();
        private static int SelectedSlot = -1;
        private static string CharacterId = "";
        private static string CharacterName = "Player";

        public static bool HasSlot
        {
            get { lock (Sync) { return SelectedSlot >= 0 && SelectedSlot < 3; } }
        }

        public static int Slot
        {
            get { lock (Sync) { return SelectedSlot; } }
        }

        public static string Id
        {
            get { lock (Sync) { return CharacterId; } }
        }

        public static string Name
        {
            get { lock (Sync) { return CharacterName; } }
        }

        public static bool SelectSlot(int slot)
        {
            if (slot < 0 || slot >= 3)
                return false;

            CharacterSlotStore.EnsureLoaded();
            CharacterSlotData data = CharacterSlotStore.Get(slot);
            if (data == null || !data.IsValid)
                return false;

            lock (Sync)
            {
                SelectedSlot = slot;
                CharacterId = data.CharacterId;
                CharacterName = data.Name;
            }

            LocalPlayerState.SetDisplayName(CharacterName);
            return true;
        }

        public static void SelectOrCreateIdentityFromCurrentPlayer()
        {
            CharacterSlotStore.EnsureLoaded();
            CharacterSlotData selected = null;

            lock (Sync)
            {
                if (SelectedSlot >= 0 && SelectedSlot < 3)
                    selected = CharacterSlotStore.Get(SelectedSlot);
            }

            if (selected != null && selected.IsValid)
            {
                lock (Sync)
                {
                    CharacterId = selected.CharacterId;
                    CharacterName = selected.Name;
                }
                LocalPlayerState.SetDisplayName(CharacterName);
                return;
            }

            string id = "mpc-character-" + Guid.NewGuid().ToString("N");
            string name = "Player";
            string code = "";

            try
            {
                if (CharacterObject.PlayerCharacter != null)
                {
                    name = CharacterObject.PlayerCharacter.Name != null
                        ? CharacterObject.PlayerCharacter.Name.ToString()
                        : "Player";
                    CharacterCode characterCode =
                        CharacterCode.CreateFrom(CharacterObject.PlayerCharacter);
                    if (characterCode != null)
                        code = characterCode.Code ?? "";
                }
            }
            catch
            {
            }

            CharacterSlotStore.Save(0, id, name, code);
            lock (Sync)
            {
                SelectedSlot = 0;
                CharacterId = id;
                CharacterName = name;
            }
            LocalPlayerState.SetDisplayName(name);
        }
    }



















    internal static class MpcNetworkRuntime
    {
        private static float ClientTimer;
        private static float HostTimer;
        private static long Revision;
        private static double LastHostTimeMs;
        private static DateTime LastHostTimeUtc = DateTime.MinValue;
        private static int HostTimeSpeed = 1;
        private static int HostTimeMode;

        public static void Tick(float dt)
        {
            try
            {
                if (Campaign.Current == null)
                    return;

                CharacterSlotStore.EnsureLoaded();

                MultiplayerNetworkClient client = MultiplayerNetworkClient.Instance;
                bool connected = client != null && client.IsConnected;
                bool worldLoaded = client != null && client.IsWorldLoaded;
                bool isHost = MultiplayerSessionState.IsHost;

                if (connected && worldLoaded)
                {
                    MpcClientParty.Ensure();
                    ClientTimer += ClampDt(dt);
                    if (ClientTimer >= 0.10f)
                    {
                        ClientTimer = 0f;
                        SendLocalState(client);
                        ApplyHostClock(dt);
                    }
                }

                if (isHost)
                {
                    HostTimer += ClampDt(dt);
                    if (HostTimer >= 0.25f)
                    {
                        HostTimer = 0f;
                        BroadcastHostState();
                        BroadcastWorldParties();
                    }
                }
            }
            catch
            {
            }
        }

        private static float ClampDt(float dt)
        {
            if (float.IsNaN(dt) || float.IsInfinity(dt))
                return 0f;
            return Math.Max(0f, Math.Min(1f, dt));
        }

        private static PlayerSyncState CaptureParty(
            string id,
            string name,
            MobileParty party,
            long sequence,
            long revision)
        {
            if (party == null)
                return null;

            CampaignVec2 position = party.Position;
            CampaignVec2 target = party.TargetPosition;
            Vec2 bearing = party.Bearing;

            if (!Valid(position.X, position.Y) || !Valid(target.X, target.Y))
                return null;

            bool moving = false;
            try
            {
                moving = party.PartyMoveMode != MoveModeType.Hold;
            }
            catch
            {
            }

            return new PlayerSyncState
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                X = position.X,
                Y = position.Y,
                TargetX = target.X,
                TargetY = target.Y,
                BearingX = bearing.X,
                BearingY = bearing.Y,
                PartySize = Math.Max(1, Math.Min(10000, CampaignWorld.GetMainPartySize())),
                Moving = moving,
                Active = true,
                Sequence = sequence,
                Revision = revision,
                ServerTimeMs = CampaignTime.Now.ElapsedMillisecondsUntilNow,
                TimeSpeed = 1,
                TimeMode = (int)Campaign.Current.TimeControlMode,
                LastUpdateUtc = DateTime.UtcNow
            };
        }

        private static bool Valid(float x, float y)
        {
            return !float.IsNaN(x) && !float.IsInfinity(x) &&
                   !float.IsNaN(y) && !float.IsInfinity(y);
        }

        private static void SendLocalState(MultiplayerNetworkClient client)
        {
            if (client == null || Campaign.Current == null)
                return;

            MobileParty party = MpcClientParty.ActiveParty;
            if (party == null || party == MobileParty.MainParty)
                party = MobileParty.MainParty;

            if (party == null)
                return;

            PlayerSyncState state = CaptureParty(
                MpcSession.Id,
                MpcSession.Name,
                party,
                WorldRevisionState.NextSequence(),
                ++Revision);

            if (state == null)
                return;

            WorldRevisionState.AcceptPlayer(state);
            byte[] payload = MpcCodec.EncodePlayer(state);
            if (payload != null && payload.Length <= 1024)
            {
                try
                {
                    client.Send(NetworkPacketType.WorldPartySnapshot, payload);
                }
                catch
                {
                }
            }
        }

        private static void BroadcastHostState()
        {
            MultiplayerCampaignHost host = MultiplayerCampaignSubModule.GetHost();
            if (host == null || Campaign.Current == null || MobileParty.MainParty == null)
                return;

            HostClientConnection[] clients = host.GetClientsSnapshot();
            if (clients == null || clients.Length == 0)
                return;

            PlayerSyncState state = CaptureParty(
                LocalPlayerState.GetNetworkId(),
                LocalPlayerState.GetDisplayName(),
                MobileParty.MainParty,
                WorldRevisionState.NextSequence(),
                ++Revision);

            if (state == null)
                return;

            byte[] payload = MpcCodec.EncodePlayer(state);
            if (payload == null)
                return;

            for (int i = 0; i < clients.Length; i++)
            {
                HostClientConnection connection = clients[i];
                if (connection == null || !connection.IsConnected)
                    continue;

                try
                {
                    connection.Send(new NetworkMessageData(
                        NetworkPacketType.WorldPartySnapshot,
                        payload));
                }
                catch
                {
                }
            }
        }

        private static void BroadcastWorldParties()
        {
            MultiplayerCampaignHost host = MultiplayerCampaignSubModule.GetHost();
            if (host == null || Campaign.Current == null)
                return;

            HostClientConnection[] clients = host.GetClientsSnapshot();
            if (clients == null || clients.Length == 0)
                return;

            // Position/target state for important campaign parties.
            // It is data-only: the client does not instantiate arbitrary parties.
            MBReadOnlyList<MobileParty> parties = Campaign.Current.MobileParties;
            if (parties == null)
                return;

            int count = Math.Min(64, parties.Count);
            for (int p = 0; p < count; p++)
            {
                MobileParty party = parties[p];
                if (party == null || party == MobileParty.MainParty)
                    continue;

                string id = "party:" + (party.StringId ?? p.ToString());
                PlayerSyncState state = CaptureParty(
                    id,
                    party.Name != null ? party.Name.ToString() : "Party",
                    party,
                    WorldRevisionState.NextSequence(),
                    Revision);

                if (state == null)
                    continue;

                WorldRevisionState.AcceptParty(state);
                byte[] payload = MpcCodec.EncodePlayer(state);
                if (payload == null || payload.Length > 4096)
                    continue;

                for (int i = 0; i < clients.Length; i++)
                {
                    HostClientConnection connection = clients[i];
                    if (connection == null || !connection.IsConnected)
                        continue;

                    try
                    {
                        connection.Send(new NetworkMessageData(
                            NetworkPacketType.WorldPartySnapshot,
                            payload));
                    }
                    catch
                    {
                    }
                }
            }

            // Authoritative time state.
            byte[] timePayload = MpcCodec.EncodeTime(
                CampaignTime.Now.ElapsedMillisecondsUntilNow,
                Campaign.Current.CampaignDt,
                1,
                (int)Campaign.Current.TimeControlMode,
                ++Revision);

            for (int i = 0; i < clients.Length; i++)
            {
                HostClientConnection connection = clients[i];
                if (connection == null || !connection.IsConnected)
                    continue;

                try
                {
                    connection.Send(new NetworkMessageData(
                        NetworkPacketType.WorldPartySnapshot,
                        timePayload));
                }
                catch
                {
                }
            }
        }

        public static bool ProcessNetworkPayload(
            byte[] payload,
            bool fromHost)
        {
            byte kind;
            PlayerSyncState state;
            double timeMs;
            float campaignDt;
            int speed;
            int mode;
            string removedId;
            long revision;

            if (!MpcCodec.TryDecode(
                payload,
                out kind,
                out state,
                out timeMs,
                out campaignDt,
                out speed,
                out mode,
                out removedId,
                out revision))
                return false;

            if (kind == MpcCodec.Player && state != null)
            {
                if (state.Id == MpcSession.Id ||
                    state.Id == LocalPlayerState.GetNetworkId())
                    return true;

                WorldRevisionState.AcceptPlayer(state);
                WorldRevisionState.AcceptParty(state);
                return true;
            }

            if (kind == MpcCodec.Time)
            {
                LastHostTimeMs = timeMs;
                LastHostTimeUtc = DateTime.UtcNow;
                HostTimeSpeed = Math.Max(0, Math.Min(3, speed));
                HostTimeMode = mode;
                return true;
            }

            if (kind == MpcCodec.Leave)
            {
                WorldRevisionState.Remove(removedId);
                return true;
            }

            if (kind == MpcCodec.Resync)
            {
                WorldRevisionState.Clear();
                return true;
            }

            return fromHost;
        }

        private static void ApplyHostClock(float dt)
        {
            if (Campaign.Current == null || LastHostTimeUtc == DateTime.MinValue)
                return;

            try
            {
                double local = CampaignTime.Now.ElapsedMillisecondsUntilNow;
                double host = LastHostTimeMs;
                double elapsed = (DateTime.UtcNow - LastHostTimeUtc).TotalMilliseconds;
                double predictedHost = host + Math.Max(0d, elapsed);
                double drift = predictedHost - local;

                if (Math.Abs(drift) > 1500d)
                {
                    Campaign.Current.SetTimeSpeed(3);
                }
                else if (Math.Abs(drift) > 250d)
                {
                    Campaign.Current.SetTimeSpeed(drift > 0d ? 2 : 0);
                }
                else
                {
                    Campaign.Current.SetTimeSpeed(HostTimeSpeed);
                }

                try
                {
                    Campaign.Current.TimeControlMode =
                        (CampaignTimeControlMode)HostTimeMode;
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        public static void Clear()
        {
            ClientTimer = 0f;
            HostTimer = 0f;
            Revision = 0;
            LastHostTimeMs = 0d;
            LastHostTimeUtc = DateTime.MinValue;
            HostTimeSpeed = 1;
            HostTimeMode = 0;
            WorldRevisionState.Clear();
            MpcClientParty.Clear();
        }
    }

}

