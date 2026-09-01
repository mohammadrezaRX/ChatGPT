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


namespace MultiplayerCampaign
{


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

