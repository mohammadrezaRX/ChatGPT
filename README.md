using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;
using SandBox;
using Helpers;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.ViewModelCollection.InitialMenu;
using TaleWorlds.SaveSystem.Load;
using TaleWorlds.ScreenSystem;

namespace MultiplayerCampaign
{
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
    }


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
     * MAIN SUBMODULE
     * ============================================================
     */

    public sealed class MultiplayerCampaignSubModule
        : MBSubModuleBase
    {
        private const string HostSaveName =
            "MCC";

        private static MultiplayerCampaignHost _host;

        private static bool _hostRequested;

        private static bool _loadingTransferredWorld;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            HostConsole.Initialize();

            HostConsole.WriteLine(
                "[*] Multiplayer Campaign loaded."
            );

            new Harmony(
                "MultiplayerCampaign"
            ).PatchAll();
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
                game.GameType is Campaign &&
                gameStarter is CampaignGameStarter starter)
            {
                starter.AddBehavior(
                    new MultiplayerCampaignBehavior()
                );
            }
        }

        public static void RequestHost()
        {
            _hostRequested = true;
        }

        public static bool IsHostRequested()
        {
            return _hostRequested;
        }

        public static void BeginTransferredWorldLoad()
        {
            _loadingTransferredWorld = true;
        }

        public static void EndTransferredWorldLoad()
        {
            _loadingTransferredWorld = false;
        }

        public static bool IsLoadingTransferredWorld()
        {
            return _loadingTransferredWorld;
        }

        internal static MultiplayerCampaignHost GetHost()
        {
            return _host;
        }

        public static void StopHost()
        {
            MultiplayerCampaignHost host =
                _host;

            _host = null;
            _hostRequested = false;

            if (host != null)
            {
                host.Stop();
            }
        }

        /*
         * ========================================================
         * LOAD MCC HOST
         * ========================================================
         */

        public static bool LoadHostCampaign()
        {
            try
            {
                HostConsole.WriteLine(
                    "[*] Loading MCC..."
                );

                if (
                    !MBSaveLoad.IsSaveGameFileExists(
                        HostSaveName))
                {
                    HostConsole.WriteLine(
                        "[!] MCC save was not found."
                    );

                    return false;
                }

                LoadResult result =
                    MBSaveLoad.LoadSaveGameData(
                        HostSaveName
                    );

                if (
                    result == null ||
                    !result.Successful)
                {
                    HostConsole.WriteLine(
                        "[!] MCC save could not be loaded."
                    );

                    return false;
                }

                HostConsole.WriteLine(
                    "[*] MCC load requested."
                );

                SandBoxGameManager manager =
                    new SandBoxGameManager(
                        result
                    );

                MBGameManager.StartNewGame(
                    manager
                );

                return true;
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] MCC load error: " +
                    ex.Message
                );

                return false;
            }
        }

        /*
         * ========================================================
         * START HOST
         * ========================================================
         */

        public static void StartHostIfReady()
        {
            if (!_hostRequested)
            {
                return;
            }

            if (_host != null)
            {
                return;
            }

            if (Campaign.Current == null)
            {
                return;
            }

            _host =
                new MultiplayerCampaignHost(
                    LocalPlayerState.GetDisplayName()
                );

            _host.Start();
        }

        /*
         * ========================================================
         * GAME END
         * ========================================================
         */

        public override void OnGameEnd(
            Game game)
        {
            /*
             * When Client's old Campaign is destroyed while
             * the transferred MCC Campaign is being loaded,
             * TCP must remain alive.
             */

            if (_loadingTransferredWorld)
            {
                base.OnGameEnd(game);
                return;
            }

            RemotePlayerManager.Clear();

            StopHost();

            MultiplayerNetworkClient
                .Instance
                .Disconnect();

            base.OnGameEnd(game);
        }

        protected override void OnSubModuleUnloaded()
        {
            RemotePlayerManager.Clear();

            StopHost();

            MultiplayerNetworkClient
                .Instance
                .Disconnect();

            base.OnSubModuleUnloaded();
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
     * INITIAL MENU
     * ============================================================
     */

    [HarmonyPatch(
        typeof(InitialMenuVM),
        "RefreshMenuOptions"
    )]
    public static class InitialMenuPatch
    {
        private const string OptionId =
            "MultiplayerCampaign";

        public static void Postfix(
            InitialMenuVM __instance)
        {
            for (
                int i = 0;
                i < __instance.MenuOptions.Count;
                i++)
            {
                InitialMenuOptionVM option =
                    __instance.MenuOptions[i];

                if (option == null)
                {
                    continue;
                }

                if (option.InitialStateOption == null)
                {
                    continue;
                }

                if (
                    option.InitialStateOption.Id ==
                    OptionId)
                {
                    return;
                }
            }

            InitialStateOption optionToAdd =
                new InitialStateOption(
                    OptionId,
                    new TextObject(
                        "Multiplayer Campaign"
                    ),
                    90,
                    OpenMultiplayerCampaign,
                    () => (
                        false,
                        new TextObject("")
                    ),
                    new TextObject(""),
                    () => false
                );

            __instance.MenuOptions.Add(
                new InitialMenuOptionVM(
                    optionToAdd
                )
            );
        }

        private static void
            OpenMultiplayerCampaign()
        {
            ScreenManager.PushScreen(
                ViewCreatorManager.CreateScreenView<
                    MultiplayerCampaignScreen
                >()
            );
        }
    }


    /*
     * ============================================================
     * GUI SCREEN
     * ============================================================
     */

    public sealed class MultiplayerCampaignScreen
        : ScreenBase
    {
        private GauntletLayer _layer;

        private GauntletMovieIdentifier _movie;

        private MultiplayerCampaignVM _vm;

        protected override void OnInitialize()
        {
            base.OnInitialize();

            _vm =
                new MultiplayerCampaignVM();

            _layer =
                new GauntletLayer(
                    "MultiplayerCampaign",
                    100,
                    true
                );

            _layer.IsFocusLayer = true;

            AddLayer(_layer);

            _layer.InputRestrictions
                .SetInputRestrictions();

            _movie =
                _layer.LoadMovie(
                    "MultiplayerCampaign",
                    _vm
                );
        }

        protected override void OnActivate()
        {
            base.OnActivate();

            if (_layer != null)
            {
                _layer.IsFocusLayer = true;

                ScreenManager.TrySetFocus(
                    _layer
                );
            }
        }

        protected override void OnFrameTick(
            float dt)
        {
            base.OnFrameTick(dt);

            _vm?.UpdateNetwork();
        }

        protected override void OnDeactivate()
        {
            if (_layer != null)
            {
                _layer.IsFocusLayer = false;

                ScreenManager.TryLoseFocus(
                    _layer
                );
            }

            base.OnDeactivate();
        }

        protected override void OnFinalize()
        {
            /*
             * Closing GUI must NOT disconnect TCP.
             */

            if (
                _layer != null &&
                _movie != null)
            {
                _layer.ReleaseMovie(
                    _movie
                );

                _movie = null;
            }

            if (_layer != null)
            {
                _layer.InputRestrictions
                    .ResetInputRestrictions();

                RemoveLayer(_layer);

                _layer = null;
            }

            _vm = null;

            base.OnFinalize();
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

                    if (
                        body[0] !=
                        NetworkProtocol.Version)
                    {
                        return;
                    }

                    NetworkPacketType type =
                        (NetworkPacketType)
                        body[1];

                    if (
                        !Enum.IsDefined(
                            typeof(NetworkPacketType),
                            type))
                    {
                        return;
                    }

                    byte[] payload =
                        new byte[
                            length - 2
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
                            Type =
                                type,

                            Payload =
                                payload
                        }
                    );
                }
            }
            catch
            {
            }

            IsConnected =
                false;
        }

        /*
         * ========================================================
         * GAME THREAD PROCESSING
         * ========================================================
         */

        public void Update()
        {
            while (
                _incoming.TryDequeue(
                    out NetworkMessage message))
            {
                try
                {
                    ProcessMessage(
                        message
                    );
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
            switch (message.Type)
            {
                case NetworkPacketType.Welcome:

                    _vm?.SetStatus(
                        "HOST FOUND - RECEIVING MCC"
                    );

                    break;

                case NetworkPacketType.WorldBegin:

                    MultiplayerWorldTransfer
                        .BeginClientTransfer(
                            message.Payload
                        );

                    break;

                case NetworkPacketType.WorldChunk:

                    MultiplayerWorldTransfer
                        .WriteClientChunk(
                            message.Payload
                        );

                    break;

                case NetworkPacketType.WorldComplete:

                    if (
                        MultiplayerWorldTransfer
                            .CompleteClientTransfer(
                                message.Payload))
                    {
                        _worldReady =
                            true;

                        HostConsole.WriteLine(
                            "[*] MCC received."
                        );
                    }
                    else
                    {
                        _vm?.SetStatus(
                            "MCC TRANSFER FAILED"
                        );
                    }

                    break;

                case NetworkPacketType.PlayerSnapshot:

                    PlayerSnapshotSerializer
                        .Deserialize(
                            message.Payload
                        );

                    break;

                case NetworkPacketType.WorldPartySnapshot:

                    WorldPartySynchronizer
                        .QueueSnapshot(
                            message.Payload
                        );

                    break;

                case NetworkPacketType.PlayerLeave:

                    try
                    {
                        string playerId =
                            NetworkProtocol.ReadString(
                                message.Payload
                            );

                        RemotePlayerManager
                            .QueueLeave(
                                playerId
                            );
                    }
                    catch
                    {
                    }

                    break;

                case NetworkPacketType.WorldJoinAck:

                    ProcessWorldJoinAck(
                        message.Payload
                    );

                    break;

                case NetworkPacketType.Error:

                    try
                    {
                        SetStatusDirect(
                            NetworkProtocol.ReadString(
                                message.Payload
                            )
                        );
                    }
                    catch
                    {
                    }

                    break;
            }
        }

        /*
         * ========================================================
         * WORLD JOIN ACK
         * ========================================================
         */

        private void ProcessWorldJoinAck(
            byte[] payload)
        {
            string hostName =
                "Host";

            try
            {
                if (
                    payload != null &&
                    payload.Length > 0)
                {
                    hostName =
                        NetworkProtocol.ReadString(
                            payload
                        );
                }
            }
            catch
            {
            }

            HostConsole.WriteLine(
                "[+] Host confirmed world session."
            );

            CampaignMessageFeed.Show(
                "[Multiplayer] Connected to " +
                hostName +
                " World."
            );
        }

        /*
         * ========================================================
         * HELLO
         * ========================================================
         *
         * Internal only.
         */

        private void SendHello()
        {
            byte[] payload =
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            PlayerIdentity
                                .GetLocalId()
                        );

                        writer.Write(
                            LocalPlayerState
                                .GetDisplayName()
                        );
                    }
                );

            SendPacket(
                NetworkPacketType.Hello,
                payload
            );
        }

        /*
         * ========================================================
         * PLAYER READY
         * ========================================================
         */

        public void SendPlayerReady()
        {
            if (!IsConnected)
            {
                return;
            }

            string playerName =
                LocalPlayerState
                    .GetDisplayName();

            byte[] payload =
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            playerName
                        );
                    }
                );

            SendPacket(
                NetworkPacketType.PlayerReady,
                payload
            );

            /*
             * REAL CLIENT -> HOST TEST MESSAGE
             */

            byte[] testPayload =
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            playerName
                        );
                    }
                );

            SendPacket(
                NetworkPacketType.WorldJoinTest,
                testPayload
            );

            HostConsole.WriteLine(
                "[*] WorldJoinTest sent."
            );
        }

        /*
         * ========================================================
         * LOCAL PLAYER STATE
         * ========================================================
         */

        public void SendLocalPlayerState(
            CampaignVec2 position,
            int partySize)
        {
            if (!IsConnected)
            {
                return;
            }

            partySize =
                Math.Max(
                    1,
                    Math.Min(
                        10000,
                        partySize
                    )
                );

            byte[] payload =
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
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

            SendPacket(
                NetworkPacketType.PlayerSnapshot,
                payload
            );
        }

        /*
         * ========================================================
         * RESYNC
         * ========================================================
         */

        public void RequestResync()
        {
            if (!IsConnected)
            {
                return;
            }

            SendPacket(
                NetworkPacketType.ResyncRequest,
                Array.Empty<byte>()
            );
        }

        /*
         * ========================================================
         * SEND PACKET
         * ========================================================
         */

        private void SendPacket(
            NetworkPacketType type,
            byte[] payload)
        {
            NetworkStream stream =
                _stream;

            if (stream == null)
            {
                return;
            }

            byte[] frame;

            try
            {
                frame =
                    NetworkProtocol.BuildFrame(
                        type,
                        payload
                    );
            }
            catch
            {
                return;
            }

            lock (_sendLock)
            {
                try
                {
                    stream.Write(
                        frame,
                        0,
                        frame.Length
                    );

                    stream.Flush();
                }
                catch
                {
                    IsConnected =
                        false;
                }
            }
        }

        /*
         * ========================================================
         * WORLD FLAGS
         * ========================================================
         */

        public bool ConsumeWorldReady()
        {
            if (!_worldReady)
            {
                return false;
            }

            _worldReady =
                false;

            return true;
        }

        public void MarkWorldLoaded()
        {
            _worldLoaded =
                true;
        }

        /*
         * ========================================================
         * DISCONNECT
         * ========================================================
         */

        public void Disconnect()
        {
            lock (_connectionLock)
            {
                DisconnectInternal();
            }
        }

        private void DisconnectInternal()
        {
            IsConnected =
                false;

            _worldReady =
                false;

            _worldLoaded =
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

            try
            {
                _cts?.Dispose();
            }
            catch
            {
            }

            _stream = null;

            _tcpClient = null;

            _cts = null;

            while (
                _incoming.TryDequeue(
                    out NetworkMessage _))
            {
            }
        }

        /*
         * ========================================================
         * READ EXACT
         * ========================================================
         */

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

            if (count <= 0)
            {
                return Array.Empty<byte>();
            }

            byte[] buffer =
                new byte[count];

            int offset = 0;

            while (
                offset < count)
            {
                if (
                    token.IsCancellationRequested)
                {
                    return null;
                }

                int read =
                    await stream.ReadAsync(
                        buffer,
                        offset,
                        count - offset
                    );

                if (read <= 0)
                {
                    return null;
                }

                offset +=
                    read;
            }

            return buffer;
        }
    }


    /*
     * ============================================================
     * HOST SERVER
     * ============================================================
     */

    public sealed class MultiplayerCampaignHost
    {
        private const int Port =
            25565;

        private const string HostSaveName =
            "MCC";

        private const string ResyncSaveName =
            "MCC_RESYNC";

        private readonly string _hostName;

        private readonly ConcurrentDictionary<
            int,
            HostConnection>
            _connections =
            new ConcurrentDictionary<
                int,
                HostConnection>();

        private readonly ConcurrentDictionary<
            string,
            ServerPlayerState>
            _players =
            new ConcurrentDictionary<
                string,
                ServerPlayerState>();

        private readonly ConcurrentDictionary<
            string,
            byte>
            _joinedPlayers =
            new ConcurrentDictionary<
                string,
                byte>();

        private TcpListener _listener;

        private CancellationTokenSource _cts;

        private int _nextConnectionId;

        private float _timer;

        private bool _resyncRequested;

        private bool _resyncRunning;

        public MultiplayerCampaignHost(
            string hostName)
        {
            _hostName =
                string.IsNullOrWhiteSpace(
                    hostName)
                    ? "Host"
                    : hostName;
        }

        /*
         * ========================================================
         * START
         * ========================================================
         */

        public void Start()
        {
            if (_listener != null)
            {
                return;
            }

            HostConsole.Initialize();

            _cts =
                new CancellationTokenSource();

            try
            {
                _listener =
                    new TcpListener(
                        IPAddress.Any,
                        Port
                    );

                _listener.Start();

                HostConsole.WriteLine(
                    "[*] Server started on port 25565."
                );

                HostConsole.WriteLine(
                    "[*] Host: " +
                    _hostName
                );

                _ =
                    AcceptLoopAsync(
                        _cts.Token
                    );
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Server start failed: " +
                    ex.Message
                );

                _listener = null;
            }
        }

        /*
         * ========================================================
         * UPDATE
         * ========================================================
         */

        public void Update()
        {
            _timer +=
                0.10f;

            if (_timer < 0.25f)
            {
                return;
            }

            _timer =
                0f;

            if (
                _resyncRequested &&
                !_resyncRunning)
            {
                StartResyncSave();
            }

            /*
             * HOST AUTHORITATIVE PLAYER SNAPSHOT
             */

            BroadcastPlayerSnapshot();

            /*
             * NPC WORLD SNAPSHOT
             */

            BroadcastWorldPartySnapshot();
        }

        /*
         * ========================================================
         * ACCEPT LOOP
         * ========================================================
         */

        private async Task AcceptLoopAsync(
            CancellationToken token)
        {
            try
            {
                while (
                    !token.IsCancellationRequested)
                {
                    TcpListener listener =
                        _listener;

                    if (listener == null)
                    {
                        return;
                    }

                    TcpClient client =
                        await listener
                            .AcceptTcpClientAsync();

                    if (
                        token.IsCancellationRequested)
                    {
                        try
                        {
                            client.Close();
                        }
                        catch
                        {
                        }

                        return;
                    }

                    client.NoDelay =
                        true;

                    int connectionId =
                        Interlocked.Increment(
                            ref _nextConnectionId
                        );

                    HostConnection connection =
                        new HostConnection(
                            connectionId,
                            client
                        );

                    _connections[
                        connectionId
                    ] =
                        connection;

                    _ =
                        HandleClientAsync(
                            connection,
                            token
                        );
                }
            }
            catch
            {
            }
        }

        /*
         * ========================================================
         * HANDLE CLIENT
         * ========================================================
         */

        private async Task HandleClientAsync(
            HostConnection connection,
            CancellationToken token)
        {
            try
            {
                connection.Stream =
                    connection.Client
                        .GetStream();

                NetworkMessage hello =
                    await ReadPacketAsync(
                        connection.Stream,
                        token
                    );

                if (hello == null)
                {
                    return;
                }

                if (
                    hello.Type !=
                    NetworkPacketType.Hello)
                {
                    return;
                }

                ParseHello(
                    hello.Payload,
                    connection
                );

                if (
                    string.IsNullOrWhiteSpace(
                        connection.PlayerId))
                {
                    return;
                }

                HostConsole.WriteLine(
                    "[+] Client connected: " +
                    connection.PlayerName
                );

                SendDirect(
                    connection,
                    NetworkPacketType.Welcome,
                    NetworkProtocol.CreatePayload(
                        writer =>
                        {
                            writer.Write(
                                _hostName
                            );
                        }
                    )
                );

                string file =
                    SaveFileLocator.Find(
                        HostSaveName
                    );

                if (
                    string.IsNullOrWhiteSpace(file) ||
                    !File.Exists(file))
                {
                    SendError(
                        connection,
                        "HOST SAVE MCC NOT FOUND"
                    );

                    return;
                }

                HostConsole.WriteLine(
                    "[*] MCC sending to " +
                    connection.PlayerName +
                    "..."
                );

                MultiplayerWorldTransfer
                    .StartHostTransfer(
                        connection,
                        file
                    );

                await ReceiveClientLoopAsync(
                    connection,
                    token
                );
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Client connection error: " +
                    ex.Message
                );
            }
            finally
            {
                RemoveConnection(
                    connection
                );
            }
        }

        /*
         * ========================================================
         * HELLO PARSE
         * ========================================================
         *
         * IMPORTANT:
         *
         * No Campaign access occurs here.
         *
         * This is a Network Thread.
         */

        private void ParseHello(
            byte[] payload,
            HostConnection connection)
        {
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
                    string playerId =
                        reader.ReadString();

                    string playerName =
                        reader.ReadString();

                    if (
                        string.IsNullOrWhiteSpace(
                            playerId))
                    {
                        connection.PlayerId =
                            null;

                        return;
                    }

                    if (
                        string.IsNullOrWhiteSpace(
                            playerName))
                    {
                        playerName =
                            "Player";
                    }

                    connection.PlayerId =
                        playerId;

                    connection.PlayerName =
                        playerName;

                    _players[
                        playerId
                    ] =
                        new ServerPlayerState
                        {
                            PlayerId =
                                playerId,

                            Name =
                                playerName,

                            Position =
                                CampaignVec2.Zero,

                            PartySize =
                                1,

                            Ready =
                                false
                        };
                }
            }
            catch
            {
                connection.PlayerId =
                    null;

                connection.PlayerName =
                    "Player";
            }
        }

        /*
         * ========================================================
         * RECEIVE CLIENT
         * ========================================================
         */

        private async Task
            ReceiveClientLoopAsync(
                HostConnection connection,
                CancellationToken token)
        {
            while (
                !token.IsCancellationRequested)
            {
                NetworkMessage message =
                    await ReadPacketAsync(
                        connection.Stream,
                        token
                    );

                if (message == null)
                {
                    return;
                }

                switch (message.Type)
                {
                    case NetworkPacketType.PlayerReady:

                        MarkPlayerReady(
                            connection,
                            message.Payload
                        );

                        break;

                    case NetworkPacketType.PlayerSnapshot:

                        ApplyPlayerSnapshot(
                            connection,
                            message.Payload
                        );

                        break;

                    case NetworkPacketType.WorldJoinTest:

                        HandleWorldJoinTest(
                            connection,
                            message.Payload
                        );

                        break;

                    case NetworkPacketType.ResyncRequest:

                        _resyncRequested =
                            true;

                        break;
                }
            }
        }

        /*
         * ========================================================
         * PLAYER READY
         * ========================================================
         */

        private void MarkPlayerReady(
            HostConnection connection,
            byte[] payload)
        {
            try
            {
                string name =
                    connection.PlayerName;

                if (
                    payload != null &&
                    payload.Length > 0)
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
                        name =
                            reader.ReadString();
                    }
                }

                if (
                    string.IsNullOrWhiteSpace(
                        name))
                {
                    name =
                        "Player";
                }

                connection.PlayerName =
                    name;

                ServerPlayerState state;

                if (
                    _players.TryGetValue(
                        connection.PlayerId,
                        out state))
                {
                    state.Name =
                        name;

                    state.Ready =
                        true;

                    /*
                     * Initial spawn position is taken from the
                     * first valid PlayerSnapshot.
                     *
                     * If none arrived yet, zero is temporarily used.
                     */

                    RemotePlayerManager.QueueJoinWithState(
                        connection.PlayerId,
                        name,
                        state.Position,
                        state.PartySize
                    );
                }

                if (
                    _joinedPlayers.TryAdd(
                        connection.PlayerId,
                        0))
                {
                    HostConsole.WriteLine(
                        "[+] Player joined the world: " +
                        name
                    );

                    /*
                     * IMPORTANT:
                     *
                     * This is an actual Bannerlord Message Feed
                     * notification on the Host Game Thread.
                     *
                     * We only queue it through a GameThread command
                     * so the Network Thread never directly touches
                     * InformationManager.
                     */

                    RemotePlayerManager.QueueWorldJoinFeed(
                        name
                    );
                }
            }
            catch
            {
            }
        }

        /*
         * ========================================================
         * WORLD JOIN TEST
         * ========================================================
         */

        private void HandleWorldJoinTest(
            HostConnection connection,
            byte[] payload)
        {
            string playerName =
                connection.PlayerName;

            try
            {
                if (
                    payload != null &&
                    payload.Length > 0)
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
                        playerName =
                            reader.ReadString();
                    }
                }
            }
            catch
            {
            }

            HostConsole.WriteLine(
                "[+] Test message received from " +
                playerName
            );

            /*
             * Message Feed is handled on Game Thread.
             */

            RemotePlayerManager
                .QueueWorldJoinFeed(
                    playerName
                );

            /*
             * Acknowledgement back to Client.
             */

            SendDirect(
                connection,
                NetworkPacketType.WorldJoinAck,
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            _hostName
                        );
                    }
                )
            );
        }

        /*
         * ========================================================
         * PLAYER SNAPSHOT
         * ========================================================
         */

        private void ApplyPlayerSnapshot(
            HostConnection connection,
            byte[] payload)
        {
            if (
                string.IsNullOrWhiteSpace(
                    connection.PlayerId))
            {
                return;
            }

            try
            {
                if (
                    payload == null ||
                    payload.Length < 12)
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
                    float x =
                        reader.ReadSingle();

                    float y =
                        reader.ReadSingle();

                    int partySize =
                        reader.ReadInt32();

                    if (
                        float.IsNaN(x) ||
                        float.IsNaN(y) ||
                        float.IsInfinity(x) ||
                        float.IsInfinity(y))
                    {
                        return;
                    }

                    partySize =
                        Math.Max(
                            1,
                            Math.Min(
                                10000,
                                partySize
                            )
                        );

                    ServerPlayerState state;

                    if (
                        !_players.TryGetValue(
                            connection.PlayerId,
                            out state))
                    {
                        return;
                    }

                    state.Position =
                        new CampaignVec2(
                            new Vec2(
                                x,
                                y
                            ),
                            true
                        );

                    state.PartySize =
                        partySize;

                    if (state.Ready)
                    {
                        /*
                         * Queue remote update.
                         *
                         * Still no Campaign object access here.
                         */

                        RemotePlayerManager
                            .QueueState(
                                state.PlayerId,
                                state.Name,
                                state.Position,
                                state.PartySize
                            );
                    }
                }
            }
            catch
            {
            }
        }

        /*
         * ========================================================
         * PLAYER SNAPSHOT BROADCAST
         * ========================================================
         *
         * Game Thread.
         */

        private void BroadcastPlayerSnapshot()
        {
            if (_connections.Count == 0)
            {
                return;
            }

            CampaignVec2 hostPosition =
                CampaignWorld
                    .GetMainPartyPosition();

            int hostPartySize =
                CampaignWorld
                    .GetMainPartySize();

            List<ServerPlayerState> players =
                new List<ServerPlayerState>(
                    _players.Values
                );

            /*
             * HOST + CLIENTS
             */

            byte[] payload =
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            players.Count + 1
                        );

                        /*
                         * HOST
                         *
                         * "HOST" is intentionally used as
                         * a remote identity on Clients.
                         */

                        writer.Write(
                            "HOST"
                        );

                        writer.Write(
                            _hostName
                        );

                        writer.Write(
                            hostPosition.X
                        );

                        writer.Write(
                            hostPosition.Y
                        );

                        writer.Write(
                            hostPartySize
                        );

                        /*
                         * CLIENTS
                         */

                        for (
                            int i = 0;
                            i < players.Count;
                            i++)
                        {
                            ServerPlayerState player =
                                players[i];

                            if (player == null)
                            {
                                continue;
                            }

                            writer.Write(
                                player.PlayerId
                            );

                            writer.Write(
                                string.IsNullOrWhiteSpace(
                                    player.Name)
                                    ? "Player"
                                    : player.Name
                            );

                            writer.Write(
                                player.Position.X
                            );

                            writer.Write(
                                player.Position.Y
                            );

                            writer.Write(
                                Math.Max(
                                    1,
                                    player.PartySize
                                )
                            );
                        }
                    }
                );

            foreach (
                HostConnection connection
                in _connections.Values)
            {
                SendDirect(
                    connection,
                    NetworkPacketType.PlayerSnapshot,
                    payload
                );
            }
        }

        /*
         * ========================================================
         * NPC WORLD SNAPSHOT
         * ========================================================
         */

        private void BroadcastWorldPartySnapshot()
        {
            if (_connections.Count == 0)
            {
                return;
            }

            byte[] payload =
                WorldPartySynchronizer
                    .BuildHostSnapshot();

            foreach (
                HostConnection connection
                in _connections.Values)
            {
                SendDirect(
                    connection,
                    NetworkPacketType.WorldPartySnapshot,
                    payload
                );
            }
        }

        /*
         * ========================================================
         * RESYNC
         * ========================================================
         */

        private void StartResyncSave()
        {
            _resyncRequested =
                false;

            _resyncRunning =
                true;

            try
            {
                HostConsole.WriteLine(
                    "[*] Resync requested..."
                );

                CampaignSaveMetaDataArgs metadata =
                    new CampaignSaveMetaDataArgs(
                        new[]
                        {
                            "Native",
                            "SandBoxCore",
                            "SandBox",
                            "MultiplayerCampaign"
                        }
                    );

                MBSaveLoad.SaveAsCurrentGame(
                    metadata,
                    ResyncSaveName,
                    result =>
                    {
                        _resyncRunning =
                            false;

                        if (
                            result.Item1 !=
                            SaveResult.Success)
                        {
                            HostConsole.WriteLine(
                                "[!] Resync save failed."
                            );

                            return;
                        }

                        string file =
                            SaveFileLocator.Find(
                                ResyncSaveName
                            );

                        if (
                            string.IsNullOrWhiteSpace(file) ||
                            !File.Exists(file))
                        {
                            HostConsole.WriteLine(
                                "[!] Resync save not found."
                            );

                            return;
                        }

                        foreach (
                            HostConnection connection
                            in _connections.Values)
                        {
                            MultiplayerWorldTransfer
                                .StartHostTransfer(
                                    connection,
                                    file
                                );
                        }

                        HostConsole.WriteLine(
                            "[*] Resync sent."
                        );
                    }
                );
            }
            catch (Exception ex)
            {
                _resyncRunning =
                    false;

                HostConsole.WriteLine(
                    "[!] Resync error: " +
                    ex.Message
                );
            }
        }

        /*
         * ========================================================
         * REMOVE CONNECTION
         * ========================================================
         */

        private void RemoveConnection(
            HostConnection connection)
        {
            _connections.TryRemove(
                connection.ConnectionId,
                out _
            );

            if (
                !string.IsNullOrWhiteSpace(
                    connection.PlayerId))
            {
                _players.TryRemove(
                    connection.PlayerId,
                    out _
                );

                _joinedPlayers.TryRemove(
                    connection.PlayerId,
                    out _
                );

                /*
                 * Queue remote cleanup on Game Thread.
                 */

                RemotePlayerManager
                    .QueueLeave(
                        connection.PlayerId
                    );

                byte[] leavePayload =
                    NetworkProtocol.CreatePayload(
                        writer =>
                        {
                            writer.Write(
                                connection.PlayerId
                            );
                        }
                    );

                foreach (
                    HostConnection client
                    in _connections.Values)
                {
                    SendDirect(
                        client,
                        NetworkPacketType.PlayerLeave,
                        leavePayload
                    );
                }
            }

            HostConsole.WriteLine(
                "[-] Player disconnected."
            );
        }

        /*
         * ========================================================
         * ERROR
         * ========================================================
         */

        private static void SendError(
            HostConnection connection,
            string message)
        {
            SendDirect(
                connection,
                NetworkPacketType.Error,
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        writer.Write(
                            message
                        );
                    }
                )
            );
        }

        /*
         * ========================================================
         * NETWORK READ
         * ========================================================
         */

        private static async Task<
            NetworkMessage>
            ReadPacketAsync(
                NetworkStream stream,
                CancellationToken token)
        {
            byte[] lengthBytes =
                await ReadExactAsync(
                    stream,
                    4,
                    token
                );

            if (lengthBytes == null)
            {
                return null;
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
                return null;
            }

            byte[] body =
                await ReadExactAsync(
                    stream,
                    length,
                    token
                );

            if (body == null)
            {
                return null;
            }

            if (
                body[0] !=
                NetworkProtocol.Version)
            {
                return null;
            }

            NetworkPacketType type =
                (NetworkPacketType)
                body[1];

            if (
                !Enum.IsDefined(
                    typeof(NetworkPacketType),
                    type))
            {
                return null;
            }

            byte[] payload =
                new byte[
                    length - 2
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

            return new NetworkMessage
            {
                Type =
                    type,

                Payload =
                    payload
            };
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

            while (
                offset < count)
            {
                if (
                    token.IsCancellationRequested)
                {
                    return null;
                }

                int read =
                    await stream.ReadAsync(
                        buffer,
                        offset,
                        count - offset
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
         * DIRECT SEND
         * ========================================================
         */

        private static void SendDirect(
            HostConnection connection,
            NetworkPacketType type,
            byte[] payload)
        {
            if (
                connection == null ||
                connection.Stream == null)
            {
                return;
            }

            byte[] frame;

            try
            {
                frame =
                    NetworkProtocol.BuildFrame(
                        type,
                        payload
                    );
            }
            catch
            {
                return;
            }

            lock (connection.SendLock)
            {
                try
                {
                    connection.Stream.Write(
                        frame,
                        0,
                        frame.Length
                    );

                    connection.Stream.Flush();
                }
                catch
                {
                }
            }
        }

        /*
         * ========================================================
         * STOP
         * ========================================================
         */

        public void Stop()
        {
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

            foreach (
                HostConnection connection
                in _connections.Values)
            {
                try
                {
                    connection.Client.Close();
                }
                catch
                {
                }
            }

            _connections.Clear();

            _players.Clear();

            _joinedPlayers.Clear();

            try
            {
                _cts?.Dispose();
            }
            catch
            {
            }

            _listener = null;

            _cts = null;
        }
    }


    /*
     * ============================================================
     * WORLD TRANSFER
     * ============================================================
     */

    public static class MultiplayerWorldTransfer
    {
        private const int ChunkSize =
            64 * 1024;

        private static readonly object
            FileLock =
                new object();

        private static FileStream _clientStream;

        private static string _clientSaveName;

        private static long _expectedLength;

        private static long _receivedLength;

        private static string _expectedHash;

        private static bool _transferActive;

        /*
         * ========================================================
         * HOST SEND
         * ========================================================
         */

        public static void StartHostTransfer(
            HostConnection connection,
            string filePath)
        {
            if (connection == null)
            {
                return;
            }

            if (
                string.IsNullOrWhiteSpace(
                    filePath))
            {
                return;
            }

            _ =
                SendFileAsync(
                    connection,
                    filePath
                );
        }

        private static async Task
            SendFileAsync(
                HostConnection connection,
                string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                FileInfo info =
                    new FileInfo(
                        filePath
                    );

                byte[] hash =
                    ComputeHash(
                        filePath
                    );

                byte[] beginPayload =
                    NetworkProtocol.CreatePayload(
                        writer =>
                        {
                            writer.Write(
                                info.Length
                            );

                            writer.Write(
                                ToHex(hash)
                            );

                            writer.Write(
                                Path.GetFileName(
                                    filePath
                                )
                            );
                        }
                    );

                SendDirect(
                    connection,
                    NetworkPacketType.WorldBegin,
                    beginPayload
                );

                using (
                    FileStream input =
                        new FileStream(
                            filePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read))
                {
                    byte[] buffer =
                        new byte[
                            ChunkSize
                        ];

                    int read;

                    long offset = 0;

                    while (
                        (read =
                            await input.ReadAsync(
                                buffer,
                                0,
                                buffer.Length
                            )) > 0)
                    {
                        byte[] packet =
                            new byte[
                                12 +
                                read
                            ];

                        Buffer.BlockCopy(
                            BitConverter.GetBytes(
                                offset
                            ),
                            0,
                            packet,
                            0,
                            8
                        );

                        Buffer.BlockCopy(
                            BitConverter.GetBytes(
                                read
                            ),
                            0,
                            packet,
                            8,
                            4
                        );

                        Buffer.BlockCopy(
                            buffer,
                            0,
                            packet,
                            12,
                            read
                        );

                        SendDirect(
                            connection,
                            NetworkPacketType.WorldChunk,
                            packet
                        );

                        offset +=
                            read;
                    }
                }

                SendDirect(
                    connection,
                    NetworkPacketType.WorldComplete,
                    Array.Empty<byte>()
                );

                HostConsole.WriteLine(
                    "[*] MCC sent."
                );
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] World transfer error: " +
                    ex.Message
                );
            }
        }

        /*
         * ========================================================
         * DIRECT SEND
         * ========================================================
         */

        private static void SendDirect(
            HostConnection connection,
            NetworkPacketType type,
            byte[] payload)
        {
            if (
                connection == null ||
                connection.Stream == null)
            {
                return;
            }

            byte[] frame;

            try
            {
                frame =
                    NetworkProtocol.BuildFrame(
                        type,
                        payload
                    );
            }
            catch
            {
                return;
            }

            lock (connection.SendLock)
            {
                try
                {
                    connection.Stream.Write(
                        frame,
                        0,
                        frame.Length
                    );

                    connection.Stream.Flush();
                }
                catch
                {
                }
            }
        }

        /*
         * ========================================================
         * CLIENT BEGIN
         * ========================================================
         */

        public static void
            BeginClientTransfer(
                byte[] payload)
        {
            lock (FileLock)
            {
                try
                {
                    CloseClientStream();

                    if (
                        payload == null ||
                        payload.Length < 8)
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
                        _expectedLength =
                            reader.ReadInt64();

                        _expectedHash =
                            reader.ReadString();

                        reader.ReadString();

                        if (
                            _expectedLength <= 0 ||
                            _expectedLength >
                            1024L *
                            1024L *
                            1024L)
                        {
                            return;
                        }

                        _clientSaveName =
                            "MCC_Client_" +
                            PlayerIdentity
                                .GetLocalId();

                        string directory =
                            SaveFileLocator
                                .GetNativeSaveDirectory();

                        Directory.CreateDirectory(
                            directory
                        );

                        string temp =
                            Path.Combine(
                                directory,
                                _clientSaveName +
                                ".sav.part"
                            );

                        _clientStream =
                            new FileStream(
                                temp,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None
                            );

                        _receivedLength =
                            0;

                        _transferActive =
                            true;

                        MultiplayerNetworkClient
                            .Instance
                            .SetStatusDirect(
                                "RECEIVING MCC..."
                            );
                    }
                }
                catch
                {
                    CloseClientStream();
                }
            }
        }

        /*
         * ========================================================
         * CLIENT CHUNK
         * ========================================================
         */

        public static void WriteClientChunk(
            byte[] payload)
        {
            lock (FileLock)
            {
                if (!_transferActive)
                {
                    return;
                }

                if (_clientStream == null)
                {
                    return;
                }

                if (
                    payload == null ||
                    payload.Length < 12)
                {
                    return;
                }

                try
                {
                    long offset =
                        BitConverter.ToInt64(
                            payload,
                            0
                        );

                    int count =
                        BitConverter.ToInt32(
                            payload,
                            8
                        );

                    if (
                        offset !=
                        _receivedLength)
                    {
                        return;
                    }

                    if (
                        count < 0 ||
                        count >
                        payload.Length - 12)
                    {
                        return;
                    }

                    if (
                        _receivedLength +
                        count >
                        _expectedLength)
                    {
                        return;
                    }

                    _clientStream.Write(
                        payload,
                        12,
                        count
                    );

                    _receivedLength +=
                        count;
                }
                catch
                {
                    CloseClientStream();
                }
            }
        }

        /*
         * ========================================================
         * CLIENT COMPLETE
         * ========================================================
         */

        public static bool
            CompleteClientTransfer(
                byte[] payload)
        {
            lock (FileLock)
            {
                if (!_transferActive)
                {
                    return false;
                }

                if (_clientStream == null)
                {
                    return false;
                }

                try
                {
                    _clientStream.Flush();

                    _clientStream.Dispose();

                    _clientStream =
                        null;

                    _transferActive =
                        false;

                    if (
                        _receivedLength !=
                        _expectedLength)
                    {
                        return false;
                    }

                    string directory =
                        SaveFileLocator
                            .GetNativeSaveDirectory();

                    string temp =
                        Path.Combine(
                            directory,
                            _clientSaveName +
                            ".sav.part"
                        );

                    string final =
                        Path.Combine(
                            directory,
                            _clientSaveName +
                            ".sav"
                        );

                    if (!File.Exists(temp))
                    {
                        return false;
                    }

                    byte[] actualHash =
                        ComputeHash(
                            temp
                        );

                    if (
                        !string.Equals(
                            _expectedHash,
                            ToHex(
                                actualHash
                            ),
                            StringComparison
                                .OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.Delete(
                                temp
                            );
                        }
                        catch
                        {
                        }

                        return false;
                    }

                    if (
                        File.Exists(
                            final))
                    {
                        File.Delete(
                            final
                        );
                    }

                    File.Move(
                        temp,
                        final
                    );

                    HostConsole.WriteLine(
                        "[*] MCC received."
                    );

                    MultiplayerNetworkClient
                        .Instance
                        .SetStatusDirect(
                            "MCC RECEIVED"
                        );

                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /*
         * ========================================================
         * CLIENT LOAD
         * ========================================================
         */

        public static void
            FinishClientLoad()
        {
            try
            {
                if (
                    string.IsNullOrWhiteSpace(
                        _clientSaveName))
                {
                    throw new InvalidOperationException(
                        "Client save name is missing."
                    );
                }

                if (
                    !MBSaveLoad
                        .IsSaveGameFileExists(
                            _clientSaveName))
                {
                    throw new InvalidOperationException(
                        "Transferred MCC save was not found."
                    );
                }

                LoadResult result =
                    MBSaveLoad
                        .LoadSaveGameData(
                            _clientSaveName
                        );

                if (
                    result == null ||
                    !result.Successful)
                {
                    throw new InvalidOperationException(
                        "MCC LoadResult is unsuccessful."
                    );
                }

                MultiplayerCampaignSubModule
                    .BeginTransferredWorldLoad();

                MultiplayerNetworkClient
                    .Instance
                    .SetStatusDirect(
                        "LOADING MCC WORLD..."
                    );

                SandBoxGameManager manager =
                    new SandBoxGameManager(
                        result
                    );

                MBGameManager.StartNewGame(
                    manager
                );

                MultiplayerNetworkClient
                    .Instance
                    .MarkWorldLoaded();
            }
            catch (Exception ex)
            {
                MultiplayerNetworkClient
                    .Instance
                    .SetStatusDirect(
                        "MCC LOAD ERROR: " +
                        ex.Message
                    );
            }
        }

        /*
         * ========================================================
         * HASH
         * ========================================================
         */

        private static byte[]
            ComputeHash(
                string file)
        {
            using (
                SHA256 sha =
                    SHA256.Create())
            using (
                FileStream stream =
                    File.OpenRead(file))
            {
                return
                    sha.ComputeHash(
                        stream
                    );
            }
        }

        private static string ToHex(
            byte[] bytes)
        {
            StringBuilder result =
                new StringBuilder(
                    bytes.Length * 2
                );

            foreach (
                byte value
                in bytes)
            {
                result.Append(
                    value.ToString(
                        "x2"
                    )
                );
            }

            return result.ToString();
        }

        private static void
            CloseClientStream()
        {
            if (_clientStream == null)
            {
                _transferActive =
                    false;

                return;
            }

            try
            {
                _clientStream.Dispose();
            }
            catch
            {
            }

            _clientStream =
                null;

            _transferActive =
                false;
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

        public MobileParty Party;

        public Hero Hero;

        public bool Active;

        public bool Spawned;

        public bool WorldJoinFeedPending;

        public bool IsHostRemote;
    }


    /*
     * ============================================================
     * REMOTE PLAYER COMMAND
     * ============================================================
     */

    internal enum RemotePlayerCommandType
    {
        Join,
        Leave,
        State,
        WorldJoinFeed
    }


    internal sealed class RemotePlayerCommand
    {
        public RemotePlayerCommandType Type;

        public string PlayerId;

        public string Name;

        public CampaignVec2 Position;

        public int PartySize;

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

                Position =
                    CampaignVec2.Zero,

                PartySize =
                    1
            };
        }

        public static RemotePlayerCommand JoinWithState(
            string playerId,
            string name,
            CampaignVec2 position,
            int partySize)
        {
            return new RemotePlayerCommand
            {
                Type =
                    RemotePlayerCommandType.Join,

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

        public static RemotePlayerCommand WorldJoinFeed(
            string name)
        {
            return new RemotePlayerCommand
            {
                Type =
                    RemotePlayerCommandType.WorldJoinFeed,

                Name =
                    name
            };
        }
    }


    /*
     * ============================================================
     * REMOTE PLAYER MANAGER
     * ============================================================
     *
     * This is the most important part of the current stage.
     *
     * The local player remains:
     *
     *     Hero.MainHero
     *     MobileParty.MainParty
     *
     * A remote player is:
     *
     *     separate Hero
     *     separate MobileParty
     *
     * The remote Hero MUST NEVER equal MainHero.
     *
     * ============================================================
     */

    internal static class RemotePlayerManager
    {
        private const string TestRemoteName =
            "Test";

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

        /*
         * ========================================================
         * QUEUE JOIN
         * ========================================================
         */

        public static void QueueJoin(
            string playerId,
            string name)
        {
            QueueJoinWithState(
                playerId,
                name,
                CampaignVec2.Zero,
                1
            );
        }

        /*
         * ========================================================
         * QUEUE JOIN WITH STATE
         * ========================================================
         */

        public static void QueueJoinWithState(
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

            Commands.Enqueue(
                RemotePlayerCommand.JoinWithState(
                    playerId,
                    TestRemoteName,
                    position,
                    Math.Max(
                        1,
                        Math.Min(
                            10000,
                            partySize
                        )
                    )
                )
            );
        }

        /*
         * ========================================================
         * QUEUE LEAVE
         * ========================================================
         */

        public static void QueueLeave(
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

        /*
         * ========================================================
         * QUEUE STATE
         * ========================================================
         */

        public static void QueueState(
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

            Commands.Enqueue(
                RemotePlayerCommand.State(
                    playerId,
                    TestRemoteName,
                    position,
                    Math.Max(
                        1,
                        Math.Min(
                            10000,
                            partySize
                        )
                    )
                )
            );
        }

        public static void ApplySnapshot(
            string playerId,
            string name,
            CampaignVec2 position,
            int partySize)
        {
            QueueState(
                playerId,
                name,
                position,
                partySize
            );
        }

        /*
         * ========================================================
         * MESSAGE FEED
         * ========================================================
         *
         * Must happen on Game Thread.
         */

        public static void QueueWorldJoinFeed(
            string name)
        {
            Commands.Enqueue(
                RemotePlayerCommand.WorldJoinFeed(
                    name
                )
            );
        }

        /*
         * ========================================================
         * GAME THREAD UPDATE
         * ========================================================
         */

        public static void Update(
            float dt)
        {
            /*
             * Apply queued commands.
             */

            while (
                Commands.TryDequeue(
                    out RemotePlayerCommand command))
            {
                try
                {
                    ApplyCommand(
                        command
                    );
                }
                catch (Exception ex)
                {
                    HostConsole.WriteLine(
                        "[!] Remote command error: " +
                        ex.Message
                    );
                }
            }

            if (Players.Count == 0)
            {
                return;
            }

            RemotePlayerState[] states =
                new RemotePlayerState[
                    Players.Count
                ];

            int index = 0;

            foreach (
                RemotePlayerState state
                in Players.Values)
            {
                states[index++] =
                    state;
            }

            /*
             * Remote party interpolation.
             */

            for (
                int i = 0;
                i < states.Length;
                i++)
            {
                RemotePlayerState state =
                    states[i];

                if (!state.Active)
                {
                    continue;
                }

                if (!state.Spawned)
                {
                    continue;
                }

                MobileParty party =
                    state.Party;

                if (party == null)
                {
                    continue;
                }

                CampaignVec2 current =
                    state.CurrentPosition;

                CampaignVec2 target =
                    state.TargetPosition;

                float alpha =
                    Math.Min(
                        1f,
                        Math.Max(
                            0.01f,
                            dt * 8f
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

                CampaignVec2 interpolated =
                    new CampaignVec2(
                        new Vec2(
                            x,
                            y
                        ),
                        true
                    );

                state.CurrentPosition =
                    interpolated;

                try
                {
                    /*
                     * Game Thread only.
                     */

                    party.Position =
                        interpolated;
                }
                catch
                {
                    /*
                     * Don't crash the Campaign if a remote
                     * party becomes invalid.
                     */

                    state.Active =
                        false;
                }
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

            /*
             * MESSAGE FEED
             */

            if (
                command.Type ==
                RemotePlayerCommandType.WorldJoinFeed)
            {
                if (
                    string.IsNullOrWhiteSpace(
                        command.Name))
                {
                    return;
                }

                CampaignMessageFeed.Show(
                    "* " +
                    command.Name +
                    " joined the world."
                );

                HostConsole.WriteLine(
                    "[*] Player joined the world."
                );

                return;
            }

            if (
                string.IsNullOrWhiteSpace(
                    command.PlayerId))
            {
                return;
            }

            /*
             * Never create ourselves as remote.
             */

            if (
                command.PlayerId ==
                PlayerIdentity.GetLocalId())
            {
                return;
            }

            /*
             * ====================================================
             * LEAVE
             * ====================================================
             */

            if (
                command.Type ==
                RemotePlayerCommandType.Leave)
            {
                RemotePlayerState existing;

                if (
                    Players.TryGetValue(
                        command.PlayerId,
                        out existing))
                {
                    DestroyRemotePlayer(
                        existing
                    );

                    Players.Remove(
                        command.PlayerId
                    );

                    HostConsole.WriteLine(
                        "[-] Remote player removed: " +
                        existing.Name
                    );
                }

                return;
            }

            /*
             * ====================================================
             * CREATE
             * ====================================================
             */

            RemotePlayerState state;

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
                            TestRemoteName,

                        CurrentPosition =
                            command.Position,

                        TargetPosition =
                            command.Position,

                        PartySize =
                            Math.Max(
                                1,
                                command.PartySize
                            ),

                        Party =
                            null,

                        Hero =
                            null,

                        Active =
                            false,

                        Spawned =
                            false,

                        WorldJoinFeedPending =
                            false,

                        IsHostRemote =
                            command.PlayerId ==
                            "HOST"
                    };

                if (
                    !CreateRemotePlayer(
                        state))
                {
                    return;
                }

                Players[
                    command.PlayerId
                ] =
                    state;

                HostConsole.WriteLine(
                    "[+] Player spawned: " +
                    TestRemoteName
                );

                CampaignMessageFeed.Show(
                    "[Multiplayer] " +
                    TestRemoteName +
                    " spawned."
                );
            }

            /*
             * ====================================================
             * UPDATE
             * ====================================================
             */

            state.Name =
                TestRemoteName;

            state.TargetPosition =
                command.Position;

            state.PartySize =
                Math.Max(
                    1,
                    Math.Min(
                        10000,
                        command.PartySize
                    )
                );

            if (!state.Spawned)
            {
                if (
                    CreateRemotePlayer(
                        state))
                {
                    HostConsole.WriteLine(
                        "[+] Player spawned: " +
                        TestRemoteName
                    );
                }
            }

            state.Active =
                state.Party != null;

            UpdateRemoteRoster(
                state
            );
        }

        /*
         * ========================================================
         * CREATE REMOTE PLAYER
         * ========================================================
         *
         * Bannerlord 1.3.4:
         *
         * MobilePartyHelper.SpawnLordParty(
         *     Hero,
         *     CampaignVec2,
         *     float)
         *
         * is available.
         *
         * ========================================================
         */

        private static bool
            CreateRemotePlayer(
                RemotePlayerState state)
        {
            if (state == null)
            {
                return false;
            }

            if (
                state.Spawned &&
                state.Hero != null &&
                state.Party != null)
            {
                return true;
            }

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

            CharacterObject template =
                Hero.MainHero.CharacterObject;

            if (template == null)
            {
                return false;
            }

            /*
             * Never use the reference to MainHero.
             */

            Hero remoteHero = null;

            MobileParty remoteParty = null;

            try
            {
                /*
                 * Unique remote Hero ID.
                 *
                 * "HOST" is allowed because it is not the
                 * local player's persistent ID.
                 *
                 * Client players receive the Host as:
                 *
                 *     mpc_remote_HOST
                 *
                 * Host receives Client as:
                 *
                 *     mpc_remote_<client-id>
                 */

                string safePlayerId =
                    SanitizeId(
                        state.PlayerId
                    );

                string heroId =
                    "mpc_remote_" +
                    safePlayerId;

                /*
                 * Create completely separate Hero.
                 *
                 * The source CharacterObject is only used as
                 * the template. This does NOT reuse MainHero.
                 */

                bool created =
                    HeroCreator.CreateBasicHero(
                        heroId,
                        template,
                        out remoteHero,
                        true
                    );

                if (
                    !created ||
                    remoteHero == null)
                {
                    /*
                     * If the Hero already exists due to a
                     * repeated command, do not create a reference
                     * to MainHero.
                     */

                    return false;
                }

                /*
                 * Safety check.
                 */

                if (
                    remoteHero ==
                    Hero.MainHero)
                {
                    return false;
                }

                /*
                 * Experimental test character.
                 *
                 * The current stage intentionally uses:
                 *
                 *     Test
                 *
                 * This keeps character generation simple while
                 * we verify real two-player visibility.
                 */

                remoteHero.SetName(
                    new TextObject(
                        TestRemoteName
                    ),
                    new TextObject(
                        TestRemoteName
                    )
                );

                /*
                 * Create a REAL MobileParty.
                 *
                 * Not a proxy.
                 * Not a visual marker.
                 * Not MainParty.
                 */

                remoteParty =
                    MobilePartyHelper
                        .SpawnLordParty(
                            remoteHero,
                            state.TargetPosition,
                            0f
                        );

                if (remoteParty == null)
                {
                    return false;
                }

                /*
                 * Safety:
                 *
                 * Remote party must never equal MainParty.
                 */

                if (
                    remoteParty ==
                    MobileParty.MainParty)
                {
                    try
                    {
                        DestroyPartyAction.Apply(
                            null,
                            remoteParty
                        );
                    }
                    catch
                    {
                    }

                    return false;
                }

                /*
                 * Prevent autonomous local movement.
                 */

                remoteParty.SetMoveModeHold();

                /*
                 * Initial exact position.
                 */

                remoteParty.Position =
                    state.TargetPosition;

                /*
                 * Create basic roster.
                 */

                AddInitialRemoteTroops(
                    remoteParty,
                    template
                );

                state.Hero =
                    remoteHero;

                state.Party =
                    remoteParty;

                state.CurrentPosition =
                    state.TargetPosition;

                state.TargetPosition =
                    state.TargetPosition;

                state.Spawned =
                    true;

                state.Active =
                    true;

                /*
                 * Final safety check.
                 */

                if (
                    Hero.MainHero ==
                    remoteHero ||
                    MobileParty.MainParty ==
                    remoteParty)
                {
                    try
                    {
                        DestroyPartyAction.Apply(
                            null,
                            remoteParty
                        );
                    }
                    catch
                    {
                    }

                    state.Hero =
                        null;

                    state.Party =
                        null;

                    state.Spawned =
                        false;

                    state.Active =
                        false;

                    return false;
                }

                HostConsole.WriteLine(
                    "[+] Remote Hero created: Test"
                );

                HostConsole.WriteLine(
                    "[+] Remote MobileParty created."
                );

                return true;
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine(
                    "[!] Remote Hero/Party creation failed: " +
                    ex.Message
                );

                try
                {
                    if (remoteParty != null)
                    {
                        DestroyPartyAction.Apply(
                            null,
                            remoteParty
                        );
                    }
                }
                catch
                {
                }

                state.Party =
                    null;

                state.Hero =
                    null;

                state.Spawned =
                    false;

                state.Active =
                    false;

                return false;
            }
        }

        /*
         * ========================================================
         * INITIAL TROOPS
         * ========================================================
         */

        private static void AddInitialRemoteTroops(
            MobileParty party,
            CharacterObject template)
        {
            try
            {
                if (
                    party == null ||
                    template == null)
                {
                    return;
                }

                if (
                    party.MemberRoster == null)
                {
                    return;
                }

                /*
                 * Very small test party.
                 *
                 * Later this will be replaced by complete
                 * synchronized Party Roster data.
                 */

                party.MemberRoster
                    .AddToCounts(
                        template,
                        5
                    );
            }
            catch
            {
            }
        }

        /*
         * ========================================================
         * UPDATE REMOTE ROSTER
         * ========================================================
         */

        private static void UpdateRemoteRoster(
            RemotePlayerState state)
        {
            try
            {
                if (
                    state == null ||
                    state.Party == null ||
                    Hero.MainHero == null)
                {
                    return;
                }

                if (
                    state.Party.MemberRoster == null)
                {
                    return;
                }

                CharacterObject template =
                    Hero.MainHero.CharacterObject;

                if (template == null)
                {
                    return;
                }

                int desired =
                    Math.Max(
                        1,
                        Math.Min(
                            10000,
                            state.PartySize
                        )
                    );

                int current =
                    state.Party
                        .MemberRoster
                        .TotalManCount;

                if (current < desired)
                {
                    state.Party
                        .MemberRoster
                        .AddToCounts(
                            template,
                            desired - current
                        );
                }
            }
            catch
            {
            }
        }

        /*
         * ========================================================
         * DESTROY REMOTE PLAYER
         * ========================================================
         */

        private static void
            DestroyRemotePlayer(
                RemotePlayerState state)
        {
            if (state == null)
            {
                return;
            }

            try
            {
                if (
                    state.Party != null &&
                    state.Party !=
                    MobileParty.MainParty)
                {
                    DestroyPartyAction.Apply(
                        null,
                        state.Party
                    );
                }
            }
            catch
            {
            }

            state.Party =
                null;

            state.Hero =
                null;

            state.Spawned =
                false;

            state.Active =
                false;
        }

        /*
         * ========================================================
         * SANITIZE ID
         * ========================================================
         */

        private static string SanitizeId(
            string value)
        {
            if (
                string.IsNullOrWhiteSpace(
                    value))
            {
                return "player";
            }

            StringBuilder builder =
                new StringBuilder();

            for (
                int i = 0;
                i < value.Length;
                i++)
            {
                char c =
                    value[i];

                if (
                    char.IsLetterOrDigit(c) ||
                    c == '_' ||
                    c == '-')
                {
                    builder.Append(c);
                }
            }

            if (builder.Length == 0)
            {
                return "player";
            }

            return builder.ToString();
        }

        /*
         * ========================================================
         * CLEAR
         * ========================================================
         */

        public static void Clear()
        {
            foreach (
                RemotePlayerState state
                in Players.Values)
            {
                DestroyRemotePlayer(
                    state
                );
            }

            Players.Clear();

            while (
                Commands.TryDequeue(
                    out RemotePlayerCommand _))
            {
            }
        }
    }


    /*
     * ============================================================
     * PLAYER SNAPSHOT SERIALIZER
     * ============================================================
     */

    internal static class PlayerSnapshotSerializer
    {
        public static void Deserialize(
            byte[] payload)
        {
            try
            {
                if (
                    payload == null ||
                    payload.Length < 4)
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
                    int count =
                        reader.ReadInt32();

                    if (
                        count < 0 ||
                        count > 128)
                    {
                        return;
                    }

                    for (
                        int i = 0;
                        i < count;
                        i++)
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
                            continue;
                        }

                        if (
                            float.IsNaN(x) ||
                            float.IsNaN(y) ||
                            float.IsInfinity(x) ||
                            float.IsInfinity(y))
                        {
                            continue;
                        }

                        /*
                         * Ignore ourselves.
                         */

                        if (
                            playerId ==
                            PlayerIdentity
                                .GetLocalId())
                        {
                            continue;
                        }

                        RemotePlayerManager
                            .ApplySnapshot(
                                playerId,
                                name,
                                new CampaignVec2(
                                    new Vec2(
                                        x,
                                        y
                                    ),
                                    true
                                ),
                                Math.Max(
                                    1,
                                    Math.Min(
                                        10000,
                                        partySize
                                    )
                                )
                            );
                    }
                }
            }
            catch
            {
            }
        }
    }


    /*
     * ============================================================
     * WORLD PARTY SYNC
     * ============================================================
     */

    internal sealed class WorldPartyState
    {
        public string StringId;

        public CampaignVec2 Position;

        public int PartySize;
    }


    internal static class WorldPartySynchronizer
    {
        private static readonly ConcurrentQueue<
            List<WorldPartyState>>
            Pending =
            new ConcurrentQueue<
                List<WorldPartyState>>();

        /*
         * ========================================================
         * BUILD HOST SNAPSHOT
         * ========================================================
         *
         * Game Thread only.
         */

        public static byte[]
            BuildHostSnapshot()
        {
            return
                NetworkProtocol.CreatePayload(
                    writer =>
                    {
                        MBReadOnlyList<MobileParty>
                            parties =
                            MobileParty
                                .AllLordParties;

                        int count =
                            0;

                        if (parties != null)
                        {
                            for (
                                int i = 0;
                                i < parties.Count;
                                i++)
                            {
                                MobileParty party =
                                    parties[i];

                                if (party == null)
                                {
                                    continue;
                                }

                                if (
                                    party ==
                                    MobileParty.MainParty)
                                {
                                    continue;
                                }

                                if (
                                    string.IsNullOrWhiteSpace(
                                        party.StringId))
                                {
                                    continue;
                                }

                                /*
                                 * Remote test parties are
                                 * not NPC world parties.
                                 *
                                 * Their exact StringId is not
                                 * assumed here because Bannerlord
                                 * generates the party StringId.
                                 *
                                 * Remote parties are therefore
                                 * filtered later on the receiver
                                 * using RemotePlayerManager.
                                 */

                                bool isRemote =
                                    RemotePlayerManager
                                        .IsRemoteParty(
                                            party
                                        );

                                if (isRemote)
                                {
                                    continue;
                                }

                                count++;
                            }
                        }

                        writer.Write(
                            count
                        );

                        if (parties == null)
                        {
                            return;
                        }

                        for (
                            int i = 0;
                            i < parties.Count;
                            i++)
                        {
                            MobileParty party =
                                parties[i];

                            if (party == null)
                            {
                                continue;
                            }

                            if (
                                party ==
                                MobileParty.MainParty)
                            {
                                continue;
                            }

                            if (
                                string.IsNullOrWhiteSpace(
                                    party.StringId))
                            {
                                continue;
                            }

                            if (
                                RemotePlayerManager
                                    .IsRemoteParty(
                                        party
                                    ))
                            {
                                continue;
                            }

                            writer.Write(
                                party.StringId
                            );

                            writer.Write(
                                party.Position.X
                            );

                            writer.Write(
                                party.Position.Y
                            );

                            int size =
                                0;

                            if (
                                party.MemberRoster !=
                                null)
                            {
                                size =
                                    party
                                        .MemberRoster
                                        .TotalManCount;
                            }

                            writer.Write(
                                size
                            );
                        }
                    }
                );
        }

        /*
         * ========================================================
         * QUEUE SNAPSHOT
         * ========================================================
         */

        public static void QueueSnapshot(
            byte[] payload)
        {
            try
            {
                if (
                    payload == null ||
                    payload.Length < 4)
                {
                    return;
                }

                List<WorldPartyState>
                    snapshot =
                    new List<
                        WorldPartyState>();

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
                    int count =
                        reader.ReadInt32();

                    if (
                        count < 0 ||
                        count > 100000)
                    {
                        return;
                    }

                    for (
                        int i = 0;
                        i < count;
                        i++)
                    {
                        string id =
                            reader.ReadString();

                        float x =
                            reader.ReadSingle();

                        float y =
                            reader.ReadSingle();

                        int size =
                            reader.ReadInt32();

                        if (
                            string.IsNullOrWhiteSpace(
                                id))
                        {
                            continue;
                        }

                        if (
                            float.IsNaN(x) ||
                            float.IsNaN(y) ||
                            float.IsInfinity(x) ||
                            float.IsInfinity(y))
                        {
                            continue;
                        }

                        snapshot.Add(
                            new WorldPartyState
                            {
                                StringId =
                                    id,

                                Position =
                                    new CampaignVec2(
                                        new Vec2(
                                            x,
                                            y
                                        ),
                                        true
                                    ),

                                PartySize =
                                    Math.Max(
                                        0,
                                        size
                                    )
                            }
                        );
                    }
                }

                Pending.Enqueue(
                    snapshot
                );
            }
            catch
            {
            }
        }

        /*
         * ========================================================
         * APPLY GAME THREAD
         * ========================================================
         */

        public static void ApplyPending(
            float dt)
        {
            List<WorldPartyState>
                snapshot =
                null;

            while (
                Pending.TryDequeue(
                    out List<WorldPartyState> next))
            {
                snapshot =
                    next;
            }

            if (snapshot == null)
            {
                return;
            }

            try
            {
                Dictionary<
                    string,
                    WorldPartyState>
                    map =
                    new Dictionary<
                        string,
                        WorldPartyState>();

                foreach (
                    WorldPartyState state
                    in snapshot)
                {
                    if (
                        string.IsNullOrWhiteSpace(
                            state.StringId))
                    {
                        continue;
                    }

                    map[
                        state.StringId
                    ] =
                        state;
                }

                MBReadOnlyList<MobileParty>
                    parties =
                    MobileParty
                        .AllLordParties;

                if (parties == null)
                {
                    return;
                }

                for (
                    int i = 0;
                    i < parties.Count;
                    i++)
                {
                    MobileParty party =
                        parties[i];

                    if (party == null)
                    {
                        continue;
                    }

                    if (
                        party ==
                        MobileParty.MainParty)
                    {
                        continue;
                    }

                    if (
                        RemotePlayerManager
                            .IsRemoteParty(
                                party
                            ))
                    {
                        continue;
                    }

                    WorldPartyState state;

                    if (
                        !map.TryGetValue(
                            party.StringId,
                            out state))
                    {
                        continue;
                    }

                    CampaignVec2 current =
                        party.Position;

                    float alpha =
                        Math.Min(
                            1f,
                            Math.Max(
                                0.01f,
                                dt * 5f
                            )
                        );

                    float x =
                        current.X +
                        (
                            state.Position.X -
                            current.X
                        ) *
                        alpha;

                    float y =
                        current.Y +
                        (
                            state.Position.Y -
                            current.Y
                        ) *
                        alpha;

                    party.Position =
                        new CampaignVec2(
                            new Vec2(
                                x,
                                y
                            ),
                            true
                        );
                }
            }
            catch
            {
            }
        }
    }


    /*
     * ============================================================
     * SERVER PLAYER STATE
     * ============================================================
     */

    internal sealed class ServerPlayerState
    {
        public string PlayerId;

        public string Name;

        public CampaignVec2 Position;

        public int PartySize;

        public bool Ready;
    }


    /*
     * ============================================================
     * HOST CONNECTION
     * ============================================================
     */

    public sealed class HostConnection
    {
        public readonly int ConnectionId;

        public readonly TcpClient Client;

        public NetworkStream Stream;

        public string PlayerId;

        public string PlayerName;

        public readonly object SendLock =
            new object();

        public HostConnection(
            int connectionId,
            TcpClient client)
        {
            ConnectionId =
                connectionId;

            Client =
                client;
        }
    }


    /*
     * ============================================================
     * LOCAL PLAYER STATE
     * ============================================================
     */

    internal static class LocalPlayerState
    {
        private static string _displayName;

        public static string GetDisplayName()
        {
            if (
                !string.IsNullOrWhiteSpace(
                    _displayName))
            {
                return _displayName;
            }

            try
            {
                if (
                    Hero.MainHero != null &&
                    Hero.MainHero.Name != null)
                {
                    _displayName =
                        Hero.MainHero
                            .Name
                            .ToString();

                    return _displayName;
                }
            }
            catch
            {
            }

            return "Player";
        }

        public static void SetDisplayName(
            string name)
        {
            if (
                string.IsNullOrWhiteSpace(
                    name))
            {
                name =
                    "Player";
            }

            _displayName =
                name.Trim();
        }
    }


    /*
     * ============================================================
     * PLAYER IDENTITY
     * ============================================================
     *
     * Internal only.
     *
     * Never printed in normal logs.
     */

    internal static class PlayerIdentity
    {
        private static string _id;

        public static string GetLocalId()
        {
            if (
                !string.IsNullOrWhiteSpace(
                    _id))
            {
                return _id;
            }

            try
            {
                string directory =
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder
                                .ApplicationData
                        ),
                        "MultiplayerCampaign"
                    );

                Directory.CreateDirectory(
                    directory
                );

                string file =
                    Path.Combine(
                        directory,
                        "player.id"
                    );

                if (
                    File.Exists(
                        file))
                {
                    string existing =
                        File.ReadAllText(
                            file
                        ).Trim();

                    Guid parsed;

                    if (
                        Guid.TryParse(
                            existing,
                            out parsed))
                    {
                        _id =
                            existing;

                        return _id;
                    }
                }

                _id =
                    Guid.NewGuid()
                        .ToString(
                            "N"
                        );

                File.WriteAllText(
                    file,
                    _id
                );

                return _id;
            }
            catch
            {
                return
                    Guid.NewGuid()
                        .ToString(
                            "N"
                        );
            }
        }
    }


    /*
     * ============================================================
     * SAVE LOCATION
     * ============================================================
     */

    internal static class SaveFileLocator
    {
        public static string
            GetGameSavesDirectory()
        {
            string documents =
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .MyDocuments
                );

            return Path.Combine(
                documents,
                "Mount and Blade II Bannerlord",
                "Game Saves"
            );
        }

        public static string
            GetNativeSaveDirectory()
        {
            string root =
                GetGameSavesDirectory();

            string native =
                Path.Combine(
                    root,
                    "Native"
                );

            if (
                Directory.Exists(
                    native))
            {
                return native;
            }

            return root;
        }

        public static string Find(
            string saveName)
        {
            if (
                string.IsNullOrWhiteSpace(
                    saveName))
            {
                return null;
            }

            string root =
                GetGameSavesDirectory();

            if (
                !Directory.Exists(
                    root))
            {
                return null;
            }

            string target =
                saveName +
                ".sav";

            string[] files =
                Directory.GetFiles(
                    root,
                    target,
                    SearchOption
                        .AllDirectories
                );

            if (files.Length == 0)
            {
                return null;
            }

            return files[0];
        }
    }


    /*
     * ============================================================
     * HOST CONSOLE
     * ============================================================
     */

    internal static class HostConsole
    {
        private static bool _initialized;

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        private static extern bool
            AllocConsole();

        [DllImport(
            "kernel32.dll")]
        private static extern bool
            SetConsoleTitle(
                string title
            );

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                AllocConsole();

                SetConsoleTitle(
                    "Multiplayer Campaign"
                );
            }
            catch
            {
            }

            _initialized =
                true;

            try
            {
                Console.WriteLine(
                    "[" +
                    DateTime.Now.ToString(
                        "HH:mm:ss"
                    ) +
                    "] [*] Host console initialized."
                );
            }
            catch
            {
            }
        }

        public static void WriteLine(
            string message)
        {
            try
            {
                Console.WriteLine(
                    "[" +
                    DateTime.Now.ToString(
                        "HH:mm:ss"
                    ) +
                    "] " +
                    message
                );
            }
            catch
            {
            }
        }
    }
}
