using MultiplayerCampaign;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BinaryReader = System.IO.BinaryReader;
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
        internal const string HostSaveName =
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
// REMOTE PLAYER PARTY CACHE
// ============================================================

internal static class CampaignMapRemotePartyCache
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        int>
        Sizes =
            new Dictionary<
                string,
                int>();

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
                        string.IsNullOrWhiteSpace(
                            player.PlayerId))
                    {
                        continue;
                    }

                    current.Add(
                        player.PlayerId
                    );

                    Sizes[
                        player.PlayerId] =
                        Math.Max(
                            1,
                            player.PartySize
                        );
                }
            }

            List<string> remove =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    int>
                pair in Sizes)
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
                Sizes.Remove(
                    remove[i]
                );
            }
        }
    }

    public static int Get(
        string playerId)
    {
        if (
            string.IsNullOrWhiteSpace(
                playerId))
        {
            return 1;
        }

        lock (Sync)
        {
            int size;

            if (
                Sizes.TryGetValue(
                    playerId,
                    out size))
            {
                return
                    Math.Max(
                        1,
                        size
                    );
            }
        }

        return 1;
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Sizes.Clear();
        }
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
// REMOTE PLAYER PARTY SIZE REGISTRY
// ============================================================

public static class RemotePlayerPartyRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        int>
        Sizes =
            new Dictionary<
                string,
                int>();

    public static void Set(
        string id,
        int size)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        size =
            NetworkUtilities.SafePartySize(
                size
            );

        lock (Sync)
        {
            Sizes[id] =
                size;
        }
    }

    public static int Get(
        string id)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return 1;
        }

        lock (Sync)
        {
            int size;

            if (
                Sizes.TryGetValue(
                    id,
                    out size))
            {
                return
                    NetworkUtilities.SafePartySize(
                        size
                    );
            }
        }

        return 1;
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
            Sizes.Remove(
                id
            );
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Sizes.Clear();
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
// REMOTE PLAYER WORLD VIEW
// ============================================================

public sealed class RemotePlayerWorldView
{
    public string PlayerId;

    public string Name;

    public CampaignVec2 Position;

    public int PartySize;

    public bool Active;

    public DateTime UpdatedUtc;
}


// ============================================================
// REMOTE PLAYER WORLD VIEW REGISTRY
// ============================================================

public static class RemotePlayerWorldViewRegistry
{
    private static readonly object Sync =
        new object();

    private static readonly Dictionary<
        string,
        RemotePlayerWorldView>
        Views =
            new Dictionary<
                string,
                RemotePlayerWorldView>();

    public static void Update()
    {
        RemotePlayerState[] states =
            RemotePlayerManager
                .Snapshot();

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
                        string.IsNullOrWhiteSpace(
                            state.PlayerId))
                    {
                        continue;
                    }

                    activeIds.Add(
                        state.PlayerId
                    );

                    RemotePlayerWorldView view;

                    if (
                        !Views.TryGetValue(
                            state.PlayerId,
                            out view))
                    {
                        view =
                            new RemotePlayerWorldView();

                        Views.Add(
                            state.PlayerId,
                            view
                        );
                    }

                    view.PlayerId =
                        state.PlayerId;

                    view.Name =
                        NetworkUtilities
                            .SafeName(
                                state.Name
                            );

                    view.Position =
                        state.CurrentPosition;

                    view.PartySize =
                        NetworkUtilities
                            .SafePartySize(
                                state.PartySize
                            );

                    view.Active =
                        state.Active;

                    view.UpdatedUtc =
                        state.LastPacketUtc;
                }
            }

            List<string> remove =
                new List<string>();

            foreach (
                KeyValuePair<
                    string,
                    RemotePlayerWorldView>
                item in Views)
            {
                if (
                    !activeIds.Contains(
                        item.Key))
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
                Views.Remove(
                    remove[i]
                );
            }
        }
    }

    public static RemotePlayerWorldView[]
        Snapshot()
    {
        lock (Sync)
        {
            RemotePlayerWorldView[] result =
                new RemotePlayerWorldView[
                    Views.Count
                ];

            int index =
                0;

            foreach (
                RemotePlayerWorldView view
                in Views.Values)
            {
                result[index++] =
                    view;
            }

            return result;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Views.Clear();
        }
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
// END OF CURRENT CONTINUATION
// ============================================================
// ============================================================
// MULTIPLAYER UI STATE
// ============================================================

public sealed class MultiplayerUIState
{
    private static readonly object Sync =
        new object();

    private bool _visible;

    private bool _connected;

    private bool _serverRunning;

    private bool _worldSyncing;

    private bool _worldReady;

    private string _status =
        "";

    private int _remotePlayers;

    public bool Visible
    {
        get
        {
            lock (Sync)
            {
                return _visible;
            }
        }
    }

    public bool Connected
    {
        get
        {
            lock (Sync)
            {
                return _connected;
            }
        }
    }

    public bool ServerRunning
    {
        get
        {
            lock (Sync)
            {
                return _serverRunning;
            }
        }
    }

    public bool WorldSyncing
    {
        get
        {
            lock (Sync)
            {
                return _worldSyncing;
            }
        }
    }

    public bool WorldReady
    {
        get
        {
            lock (Sync)
            {
                return _worldReady;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (Sync)
            {
                return _status;
            }
        }
    }

    public int RemotePlayers
    {
        get
        {
            lock (Sync)
            {
                return _remotePlayers;
            }
        }
    }

    public void SetVisible(
        bool value)
    {
        lock (Sync)
        {
            _visible =
                value;
        }
    }

    public void SetConnected(
        bool value)
    {
        lock (Sync)
        {
            _connected =
                value;
        }
    }

    public void SetServerRunning(
        bool value)
    {
        lock (Sync)
        {
            _serverRunning =
                value;
        }
    }

    public void SetWorldSyncing(
        bool value)
    {
        lock (Sync)
        {
            _worldSyncing =
                value;
        }
    }

    public void SetWorldReady(
        bool value)
    {
        lock (Sync)
        {
            _worldReady =
                value;
        }
    }

    public void SetStatus(
        string value)
    {
        lock (Sync)
        {
            _status =
                value ??
                "";
        }
    }

    public void SetRemotePlayers(
        int count)
    {
        lock (Sync)
        {
            _remotePlayers =
                Math.Max(
                    0,
                    count
                );
        }
    }

    public void Reset()
    {
        lock (Sync)
        {
            _visible =
                false;

            _connected =
                false;

            _serverRunning =
                false;

            _worldSyncing =
                false;

            _worldReady =
                false;

            _status =
                "";

            _remotePlayers =
                0;
        }
    }
}


// ============================================================
// GLOBAL UI STATE
// ============================================================

internal static class MultiplayerUIStateManager
{
    private static readonly MultiplayerUIState State =
        new MultiplayerUIState();

    public static MultiplayerUIState Current
    {
        get
        {
            return State;
        }
    }

    public static void Reset()
    {
        State.Reset();
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
// REMOTE PLAYER PARTY SERVICE
// ============================================================

internal static class RemotePlayerPartyService
{
    public static void Update(
        string id,
        int size)
    {
        if (
            string.IsNullOrWhiteSpace(
                id))
        {
            return;
        }

        size =
            NetworkUtilities.SafePartySize(
                size
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

        state.PartySize =
            size;

        RemotePlayerPartyRegistry
            .Set(
                id,
                size
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
// SAFE CAMPAIGN SAVE GUARD
// ============================================================

internal static class SafeCampaignSaveGuard
{
    public static bool CanSave()
    {
        try
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
        catch
        {
            return false;
        }
    }

    public static void Prepare()
    {
        /*
         * IMPORTANT:
         *
         * There is intentionally no remote Hero or
         * remote MobileParty registered in Campaign.
         *
         * Therefore Bannerlord's normal save system
         * only serializes the actual local Campaign.
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
// REMOTE PLAYER PARTY COUNT
// ============================================================

public static class RemotePlayerPartyCount
{
    public static int Get(
        string playerId)
    {
        return
            RemotePlayerPartyRegistry
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

    internal static class MpcClientParty
    {
        private static readonly object Sync = new object();
        private static Hero Hero;
        private static MobileParty Party;
        private static Clan Clan;
        private static string CharacterId = "";

        public static MobileParty ActiveParty
        {
            get { lock (Sync) { return Party; } }
        }

        public static Hero ActiveHero
        {
            get { lock (Sync) { return Hero; } }
        }

        public static MobileParty Ensure()
        {
            if (Campaign.Current == null)
                return null;

            MpcSession.SelectOrCreateIdentityFromCurrentPlayer();

            lock (Sync)
            {
                if (Party != null && Party != MobileParty.MainParty)
                    return Party;

                CharacterId = MpcSession.Id;
                if (string.IsNullOrWhiteSpace(CharacterId))
                    return null;

                try
                {
                    string heroId = "mpc.client.hero." + CharacterId;
                    Hero = TaleWorlds.CampaignSystem.Hero.Find(heroId);

                    if (Hero == null)
                    {
                        CharacterObject template = CharacterObject.PlayerCharacter;
                        if (template == null)
                            return null;

                        CharacterObject clone = CharacterObject.CreateFrom(template);
                        Hero created;
                        if (!HeroCreator.CreateBasicHero(heroId, clone, out created, true))
                            created = TaleWorlds.CampaignSystem.Hero.Find(heroId);

                        Hero = created;
                    }

                    if (Hero == null)
                        return null;

                    string clanId = "mpc.client.clan." + CharacterId;
                    Clan = TaleWorlds.CampaignSystem.Clan.FindFirst(
                        c => c != null && c.StringId == clanId);
                    if (Clan == null)
                        Clan = TaleWorlds.CampaignSystem.Clan.CreateClan(clanId);

                    if (Clan != null)
                    {
                        try { Clan.SetLeader(Hero); } catch { }
                    }

                    Party = Helpers.MobilePartyHelper.CreateNewClanMobileParty(Hero, Clan);
                    if (Party == MobileParty.MainParty)
                    {
                        Party = null;
                        return null;
                    }

                    if (Party != null)
                    {
                        try { Party.Party.SetCustomName(new TextObject(MpcSession.Name)); } catch { }
                    }

                    return Party;
                }
                catch
                {
                    Party = null;
                    return null;
                }
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Party = null;
                Hero = null;
                Clan = null;
                CharacterId = "";
            }
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

// ================= MPC FINAL SAFETY LAYER V2 =================
// Preserves the complete existing rebuild source and adds only compatibility/safety fixes.

namespace MultiplayerCampaign
{
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

    internal static class MpcFinalRuntimeStatusV2
    {
        public static bool CharacterSelected { get { try { return MpcFinalCharacterSystemV2.HasSelection; } catch { return false; } } }
        public static int CharacterSlot { get { try { return MpcFinalCharacterSystemV2.Selected; } catch { return -1; } } }
        public static string CharacterId { get { try { return MpcFinalCharacterSystemV2.GetSelectedCharacterId(); } catch { return null; } } }
        public static string CharacterName { get { try { return MpcFinalCharacterSystemV2.GetSelectedName(); } catch { return "Player"; } } }
    }
}




