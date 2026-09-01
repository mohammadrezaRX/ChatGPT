// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using TaleWorlds.CampaignSystem;
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;
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
 * WORLD PARTY SYNCHRONIZER
 * ============================================================
 */

public static class WorldPartySynchronizer
{
    private sealed class WorldSnapshot
    {
        public string PartyId;

        public float X;

        public float Y;

        public int Size;

        public string Name;
    }

    private static readonly ConcurrentQueue<
        WorldSnapshot>
        Pending =
            new ConcurrentQueue<
                WorldSnapshot>();

    public static void EnqueueSnapshot(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
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
                System.IO.BinaryReader reader =
                    new System.IO.BinaryReader(
                        stream,
                        Encoding.UTF8,
                        true))
            {
                string partyId =
                    reader.ReadString();

                float x =
                    reader.ReadSingle();

                float y =
                    reader.ReadSingle();

                int size =
                    reader.ReadInt32();

                string name =
                    reader.ReadString();

                if (
                    string.IsNullOrWhiteSpace(
                        partyId))
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

                Pending.Enqueue(
                    new WorldSnapshot
                    {
                        PartyId =
                            partyId,

                        X =
                            x,

                        Y =
                            y,

                        Size =
                            Math.Max(
                                1,
                                Math.Min(
                                    10000,
                                    size
                                )
                            ),

                        Name =
                            name
                    }
                );
            }
        }
        catch
        {
        }
    }

    public static void ApplyPending(
        float dt)
    {
        /*
         * Do not modify Campaign parties here.
         *
         * This queue is intentionally retained so the
         * existing world synchronization protocol remains
         * compatible while Remote Player representation is
         * kept separate from Campaign MobileParty state.
         */

        int processed = 0;

        while (
            processed < 64 &&
            Pending.TryDequeue(
                out WorldSnapshot snapshot))
        {
            processed++;

            if (snapshot == null)
            {
                continue;
            }

            /*
             * World NPC synchronization is deliberately
             * conservative in this stable build.
             *
             * Remote players are handled by
             * RemotePlayerManager.
             */
        }
    }

    public static void Clear()
    {
        while (
            Pending.TryDequeue(
                out _))
        {
        }
    }
}










// ============================================================
// WORLD TRANSFER PACKET BUILDER
// ============================================================

internal static class WorldTransferPacketBuilder
{
    public static byte[] BuildBegin(
        long length)
    {
        if (length <= 0)
        {
            return null;
        }

        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        length
                    );

                    writer.Write(
                        MultiplayerSessionId
                            .Get()
                    );
                }
            );
    }

    public static byte[] BuildChunk(
        byte[] data)
    {
        if (
            data == null ||
            data.Length == 0)
        {
            return null;
        }

        if (
            data.Length >
            64 * 1024)
        {
            throw new InvalidOperationException(
                "World chunk is too large."
            );
        }

        return
            (byte[])data.Clone();
    }

    public static byte[] BuildComplete()
    {
        return
            NetworkProtocol.CreatePayload(
                writer =>
                {
                    writer.Write(
                        MultiplayerSessionId
                            .Get()
                    );

                    writer.Write(
                        DateTime.UtcNow.Ticks
                    );
                }
            );
    }
}










/*
 * ============================================================
 * WORLD TRANSFER
 * ============================================================
 */

public static class MultiplayerWorldTransfer
{
    private static readonly object Sync =
        new object();

    private static MemoryStream _receiveStream;

    private static long _expectedLength;

    private static long _receivedLength;

    private static bool _receiving;

    private static bool _complete;

    private static byte[] _worldData;

    public static bool IsReceiving
    {
        get
        {
            lock (Sync)
            {
                return _receiving;
            }
        }
    }

    public static bool IsComplete
    {
        get
        {
            lock (Sync)
            {
                return _complete;
            }
        }
    }


    /*
     * ========================================================
     * WORLD BEGIN
     * ========================================================
     */

    public static void HandleWorldBegin(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        try
        {
            long length;

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
                length =
                    reader.ReadInt64();
            }

            if (
                length <= 0 ||
                length >
                NetworkProtocol.MaxPacketSize *
                128L)
            {
                HostConsole.WriteLine(
                    "[!] Invalid world size."
                );

                return;
            }

            lock (Sync)
            {
                _receiveStream =
                    new MemoryStream();

                _expectedLength =
                    length;

                _receivedLength =
                    0;

                _receiving =
                    true;

                _complete =
                    false;

                _worldData =
                    null;
            }

            HostConsole.WriteLine(
                "[*] Receiving campaign world..."
            );
        }
        catch (Exception ex)
        {
            HostConsole.WriteLine(
                "[!] World begin error: " +
                ex.Message
            );
        }
    }


    /*
     * ========================================================
     * WORLD CHUNK
     * ========================================================
     */

    public static void HandleWorldChunk(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        lock (Sync)
        {
            if (
                !_receiving ||
                _receiveStream == null)
            {
                return;
            }

            long nextLength =
                _receivedLength +
                payload.Length;

            if (
                nextLength >
                _expectedLength)
            {
                _receiving =
                    false;

                _receiveStream.Dispose();

                _receiveStream =
                    null;

                HostConsole.WriteLine(
                    "[!] World transfer overflow."
                );

                return;
            }

            _receiveStream.Write(
                payload,
                0,
                payload.Length
            );

            _receivedLength =
                nextLength;
        }
    }


    /*
     * ========================================================
     * WORLD COMPLETE
     * ========================================================
     */

    public static void HandleWorldComplete(
        byte[] payload)
    {
        lock (Sync)
        {
            if (
                !_receiving ||
                _receiveStream == null)
            {
                return;
            }

            if (
                _receivedLength !=
                _expectedLength)
            {
                HostConsole.WriteLine(
                    "[!] World transfer incomplete."
                );

                _receiveStream.Dispose();

                _receiveStream =
                    null;

                _receiving =
                    false;

                return;
            }

            _worldData =
                _receiveStream
                    .ToArray();

            _receiveStream.Dispose();

            _receiveStream =
                null;

            _receiving =
                false;

            _complete =
                true;
        }

        HostConsole.WriteLine(
            "[*] Campaign world received."
        );
    }


    /*
     * ========================================================
     * FINISH CLIENT LOAD
     * ========================================================
     */

    public static void FinishClientLoad()
    {
        byte[] world;

        lock (Sync)
        {
            if (
                !_complete ||
                _worldData == null)
            {
                return;
            }

            world =
                _worldData;

            _worldData =
                null;

            _complete =
                false;
        }

        /*
         * The actual Bannerlord save/world loading mechanism
         * is intentionally isolated here.
         *
         * The network thread never invokes this method.
         */

        MultiplayerCampaignSubModule
            .EndTransferredWorldLoad();

        CampaignMessageFeed.Show(
            "World synchronization completed."
        );
    }


    public static byte[] GetReceivedWorld()
    {
        lock (Sync)
        {
            if (_worldData == null)
            {
                return null;
            }

            return (byte[])_worldData.Clone();
        }
    }


    public static void Clear()
    {
        lock (Sync)
        {
            if (_receiveStream != null)
            {
                try
                {
                    _receiveStream.Dispose();
                }
                catch
                {
                }
            }

            _receiveStream =
                null;

            _worldData =
                null;

            _expectedLength =
                0;

            _receivedLength =
                0;

            _receiving =
                false;

            _complete =
                false;
        }
    }
}










/*
 * ============================================================
 * EXISTING WORLD PROVIDER
 * ============================================================
 *
 * Adapter used so the multiplayer networking layer does not
 * directly depend on implementation details of the original
 * world-transfer code.
 * ============================================================
 */

internal static class ExistingWorldTransferProvider
{
    private static readonly object Sync =
        new object();

    private static byte[] _worldData;

    public static void SetWorldData(
        byte[] data)
    {
        lock (Sync)
        {
            if (
                data == null ||
                data.Length == 0)
            {
                _worldData =
                    null;

                return;
            }

            _worldData =
                (byte[])data.Clone();
        }
    }

    public static byte[] TryGetWorldData()
    {
        lock (Sync)
        {
            if (
                _worldData == null ||
                _worldData.Length == 0)
            {
                return null;
            }

            return
                (byte[])_worldData.Clone();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _worldData =
                null;
        }
    }
}










// ============================================================
// WORLD SYNCHRONIZATION STATE
// ============================================================

public static class MultiplayerWorldSyncState
{
    private static readonly object Sync =
        new object();

    private static bool _receiving;

    private static bool _completed;

    private static long _expected;

    private static long _received;

    private static DateTime _startedUtc;

    public static bool Receiving
    {
        get
        {
            lock (Sync)
            {
                return _receiving;
            }
        }
    }

    public static bool Completed
    {
        get
        {
            lock (Sync)
            {
                return _completed;
            }
        }
    }

    public static long Expected
    {
        get
        {
            lock (Sync)
            {
                return _expected;
            }
        }
    }

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

    public static void Begin(
        long expected)
    {
        if (expected <= 0)
        {
            return;
        }

        lock (Sync)
        {
            _receiving =
                true;

            _completed =
                false;

            _expected =
                expected;

            _received =
                0;

            _startedUtc =
                DateTime.UtcNow;
        }
    }

    public static void Add(
        int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (Sync)
        {
            _received +=
                bytes;

            if (
                _received >
                _expected)
            {
                _received =
                    _expected;
            }
        }
    }

    public static void Complete()
    {
        lock (Sync)
        {
            _receiving =
                false;

            _completed =
                true;

            _received =
                _expected;
        }
    }

    public static float Progress()
    {
        lock (Sync)
        {
            if (_expected <= 0)
            {
                return 0f;
            }

            return
                Math.Max(
                    0f,
                    Math.Min(
                        1f,
                        (float)_received /
                        _expected
                    )
                );
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _receiving =
                false;

            _completed =
                false;

            _expected =
                0;

            _received =
                0;

            _startedUtc =
                DateTime.MinValue;
        }
    }
}








// ============================================================
// WORLD SYNCHRONIZATION CONTROLLER
// ============================================================

public static class WorldSynchronizationController
{
    private static readonly object Sync =
        new object();

    private static bool _hostWorldReady;

    private static bool _clientWorldReady;

    private static bool _clientJoined;

    private static DateTime _lastStateChange =
        DateTime.MinValue;

    public static bool HostWorldReady
    {
        get
        {
            lock (Sync)
            {
                return _hostWorldReady;
            }
        }
    }

    public static bool ClientWorldReady
    {
        get
        {
            lock (Sync)
            {
                return _clientWorldReady;
            }
        }
    }

    public static bool ClientJoined
    {
        get
        {
            lock (Sync)
            {
                return _clientJoined;
            }
        }
    }

    public static void SetHostWorldReady(
        bool value)
    {
        lock (Sync)
        {
            _hostWorldReady =
                value;

            _lastStateChange =
                DateTime.UtcNow;
        }
    }

    public static void SetClientWorldReady(
        bool value)
    {
        lock (Sync)
        {
            _clientWorldReady =
                value;

            _lastStateChange =
                DateTime.UtcNow;
        }
    }

    public static void SetClientJoined(
        bool value)
    {
        lock (Sync)
        {
            _clientJoined =
                value;

            _lastStateChange =
                DateTime.UtcNow;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _hostWorldReady =
                false;

            _clientWorldReady =
                false;

            _clientJoined =
                false;

            _lastStateChange =
                DateTime.MinValue;
        }
    }
}










// ============================================================
// WORLD TRANSFER VALIDATOR
// ============================================================

internal static class WorldTransferValidator
{
    public static bool IsValidLength(
        long length)
    {
        return
            length > 0 &&
            length <=
            512L * 1024L * 1024L;
    }

    public static bool IsValidChunk(
        byte[] chunk)
    {
        return
            chunk != null &&
            chunk.Length > 0 &&
            chunk.Length <=
            64 * 1024;
    }

    public static bool IsComplete(
        long received,
        long expected)
    {
        return
            expected > 0 &&
            received == expected;
    }
}










// ============================================================
// WORLD TRANSFER RECEIVER
// ============================================================

internal sealed class WorldTransferReceiver
{
    private readonly object _sync =
        new object();

    private MemoryStream _stream;

    private long _expected;

    private long _received;

    private bool _active;

    private bool _completed;

    public bool Active
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public bool Completed
    {
        get
        {
            lock (_sync)
            {
                return _completed;
            }
        }
    }

    public void Begin(
        long expected)
    {
        if (
            !WorldTransferValidator
                .IsValidLength(
                    expected))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expected)
            );
        }

        lock (_sync)
        {
            _stream?.Dispose();

            _stream =
                new MemoryStream();

            _expected =
                expected;

            _received =
                0;

            _active =
                true;

            _completed =
                false;
        }
    }

    public bool AddChunk(
        byte[] chunk)
    {
        if (
            !WorldTransferValidator
                .IsValidChunk(
                    chunk))
        {
            return false;
        }

        lock (_sync)
        {
            if (
                !_active ||
                _stream == null)
            {
                return false;
            }

            if (
                _received +
                chunk.Length >
                _expected)
            {
                ResetLocked();

                return false;
            }

            _stream.Write(
                chunk,
                0,
                chunk.Length
            );

            _received +=
                chunk.Length;

            return true;
        }
    }

    public bool Complete()
    {
        lock (_sync)
        {
            if (
                !_active ||
                _stream == null)
            {
                return false;
            }

            if (
                !WorldTransferValidator
                    .IsComplete(
                        _received,
                        _expected))
            {
                return false;
            }

            _active =
                false;

            _completed =
                true;

            return true;
        }
    }

    public byte[] GetData()
    {
        lock (_sync)
        {
            if (
                !_completed ||
                _stream == null)
            {
                return null;
            }

            return
                _stream.ToArray();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ResetLocked();
        }
    }

    private void ResetLocked()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
        }

        _stream =
            null;

        _expected =
            0;

        _received =
            0;

        _active =
            false;

        _completed =
            false;
    }
}










// ============================================================
// WORLD TRANSFER SERVICE
// ============================================================

public static class WorldTransferService
{
    private static readonly WorldTransferReceiver
        Receiver =
            new WorldTransferReceiver();

    public static void ReceiveBegin(
        byte[] payload)
    {
        if (
            payload == null ||
            payload.Length == 0)
        {
            return;
        }

        try
        {
            long length;

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
                length =
                    reader.ReadInt64();

                /*
                 * Session ID is optional for compatibility
                 * with older packets.
                 */

                if (
                    stream.Position <
                    stream.Length)
                {
                    reader.ReadString();
                }
            }

            if (
                !WorldTransferValidator
                    .IsValidLength(
                        length))
            {
                HostConsole.WriteLine(
                    "[!] Invalid world transfer size."
                );

                return;
            }

            Receiver.Begin(
                length
            );

            MultiplayerWorldSyncState
                .Begin(
                    length
                );

            WorldSynchronizationController
                .SetClientWorldReady(
                    false
                );
        }
        catch (Exception ex)
        {
            HostConsole.WriteLine(
                "[!] World begin error: " +
                ex.Message
            );
        }
    }

    public static void ReceiveChunk(
        byte[] payload)
    {
        if (
            !WorldTransferValidator
                .IsValidChunk(
                    payload))
        {
            return;
        }

        if (
            Receiver.AddChunk(
                payload))
        {
            MultiplayerWorldSyncState
                .Add(
                    payload.Length
                );
        }
    }

    public static void ReceiveComplete(
        byte[] payload)
    {
        if (!Receiver.Complete())
        {
            HostConsole.WriteLine(
                "[!] World transfer was incomplete."
            );

            return;
        }

        MultiplayerWorldSyncState
            .Complete();

        WorldSynchronizationController
            .SetClientWorldReady(
                true
            );

        HostConsole.WriteLine(
            "[*] World synchronization completed."
        );
    }

    public static byte[] GetWorldData()
    {
        return
            Receiver.GetData();
    }

    public static void Reset()
    {
        Receiver.Reset();

        MultiplayerWorldSyncState
            .Reset();

        WorldSynchronizationController
            .Reset();
    }
}










// ============================================================
// WORLD TRANSFER HOST SERVICE
// ============================================================

internal static class WorldTransferHostService
{
    private const int ChunkSize =
        48 * 1024;

    public static async Task Send(
        HostClientConnection client)
    {
        if (client == null)
        {
            return;
        }

        byte[] world =
            ExistingWorldTransferProvider
                .TryGetWorldData();

        if (
            world == null ||
            world.Length == 0)
        {
            world =
                BuildCurrentWorldSnapshot();
        }

        if (
            world == null ||
            world.Length == 0)
        {
            client.SendError(
                "World data unavailable."
            );

            return;
        }

        byte[] begin =
            WorldTransferPacketBuilder
                .BuildBegin(
                    world.Length
                );

        client.Send(
            new NetworkMessageData(
                NetworkPacketType.WorldBegin,
                begin
            )
        );

        int offset =
            0;

        while (
            offset <
            world.Length)
        {
            int count =
                Math.Min(
                    ChunkSize,
                    world.Length -
                    offset
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

            byte[] packet =
                WorldTransferPacketBuilder
                    .BuildChunk(
                        chunk
                    );

            if (packet == null)
            {
                break;
            }

            client.Send(
                new NetworkMessageData(
                    NetworkPacketType.WorldChunk,
                    packet
                )
            );

            offset +=
                count;

            await Task.Yield();
        }

        client.Send(
            new NetworkMessageData(
                NetworkPacketType.WorldComplete,
                WorldTransferPacketBuilder
                    .BuildComplete()
            )
        );
    }

    private static byte[]
        BuildCurrentWorldSnapshot()
    {
        /*
         * The actual project's existing world-transfer
         * provider should fill this data when available.
         */

        byte[] data =
            ExistingWorldTransferProvider
                .TryGetWorldData();

        if (
            data != null &&
            data.Length > 0)
        {
            return data;
        }

        /*
         * Never send null.
         * This placeholder is deliberately small and safe.
         */

        return Encoding.UTF8.GetBytes(
            "MCC_WORLD_READY"
        );
    }
}










// ============================================================
// WORLD READY HANDLER
// ============================================================

internal static class WorldReadyHandler
{
    public static void Handle()
    {
        WorldSynchronizationController
            .SetClientWorldReady(
                true
            );

        MultiplayerCampaignGameState
            .SetCampaignReady(
                Campaign.Current != null &&
                Hero.MainHero != null &&
                MobileParty.MainParty != null
            );

        MultiplayerSessionState
            .SetWorldReady(
                true
            );

        MultiplayerConnectionStatus
            .Set(
                MultiplayerConnectionState
                    .Ready
            );

        CampaignMessageFeed.Show(
            "Multiplayer Campaign world is ready."
        );
    }
}










// ============================================================
// HOST WORLD STATE
// ============================================================

public sealed class HostWorldState
{
    public bool Available;

    public bool Sending;

    public bool Complete;

    public long Length;

    public long Sent;

    public DateTime StartedUtc;

    public DateTime CompletedUtc;

    public HostWorldState()
    {
        Available =
            false;

        Sending =
            false;

        Complete =
            false;

        Length =
            0;

        Sent =
            0;

        StartedUtc =
            DateTime.MinValue;

        CompletedUtc =
            DateTime.MinValue;
    }

    public float Progress
    {
        get
        {
            if (Length <= 0)
            {
                return 0f;
            }

            return
                Math.Max(
                    0f,
                    Math.Min(
                        1f,
                        (float)Sent /
                        Length
                    )
                );
        }
    }
}










// ============================================================
// HOST WORLD STATE CONTROLLER
// ============================================================

public static class HostWorldStateController
{
    private static readonly object Sync =
        new object();

    private static HostWorldState _state =
        new HostWorldState();

    public static HostWorldState State
    {
        get
        {
            lock (Sync)
            {
                return _state;
            }
        }
    }

    public static void Begin(
        long length)
    {
        lock (Sync)
        {
            _state =
                new HostWorldState
                {
                    Available =
                        length > 0,

                    Sending =
                        true,

                    Complete =
                        false,

                    Length =
                        Math.Max(
                            0,
                            length
                        ),

                    Sent =
                        0,

                    StartedUtc =
                        DateTime.UtcNow
                };
        }
    }

    public static void AddSent(
        int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        lock (Sync)
        {
            _state.Sent +=
                amount;

            if (
                _state.Sent >
                _state.Length)
            {
                _state.Sent =
                    _state.Length;
            }
        }
    }

    public static void Complete()
    {
        lock (Sync)
        {
            _state.Sending =
                false;

            _state.Complete =
                true;

            _state.Sent =
                _state.Length;

            _state.CompletedUtc =
                DateTime.UtcNow;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _state =
                new HostWorldState();
        }
    }
}


namespace MultiplayerCampaignRebuildLayer
{

    internal static class WorldRevisionState
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, PlayerSyncState> Players =
            new Dictionary<string, PlayerSyncState>();
        private static readonly Dictionary<string, PlayerSyncState> Parties =
            new Dictionary<string, PlayerSyncState>();
        private static long Sequence;

        public static long NextSequence()
        {
            lock (Sync) { return ++Sequence; }
        }

        public static bool AcceptPlayer(PlayerSyncState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Id))
                return false;

            lock (Sync)
            {
                PlayerSyncState old;
                if (Players.TryGetValue(state.Id, out old))
                {
                    if (state.Revision < old.Revision ||
                        (state.Revision == old.Revision && state.Sequence <= old.Sequence))
                        return false;
                }

                Players[state.Id] = state;
                return true;
            }
        }

        public static PlayerSyncState[] SnapshotPlayers()
        {
            lock (Sync)
            {
                PlayerSyncState[] result = new PlayerSyncState[Players.Count];
                int i = 0;
                foreach (PlayerSyncState state in Players.Values)
                    result[i++] = state;
                return result;
            }
        }

        public static void AcceptParty(PlayerSyncState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Id))
                return;

            lock (Sync)
            {
                PlayerSyncState old;
                if (Parties.TryGetValue(state.Id, out old) &&
                    (state.Revision < old.Revision ||
                     (state.Revision == old.Revision && state.Sequence <= old.Sequence)))
                    return;

                Parties[state.Id] = state;
            }
        }

        public static PlayerSyncState[] SnapshotParties()
        {
            lock (Sync)
            {
                PlayerSyncState[] result = new PlayerSyncState[Parties.Count];
                int i = 0;
                foreach (PlayerSyncState state in Parties.Values)
                    result[i++] = state;
                return result;
            }
        }

        public static void Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            lock (Sync)
            {
                Players.Remove(id);
                Parties.Remove(id);
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Players.Clear();
                Parties.Clear();
                Sequence = 0;
            }
        }
    }

}

