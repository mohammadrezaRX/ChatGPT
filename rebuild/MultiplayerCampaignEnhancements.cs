using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ViewModelCollection.InitialMenu;

namespace MultiplayerCampaign
{
    internal static class MpcRebuildRuntimeV2
    {
        private const int SlotCount = 3;
        private const int Magic = 0x4D504358;
        private const byte Version = 2;
        private const byte PlayerStatePacket = 14;
        private const byte WorldStatePacket = 15;

        private static readonly object Sync = new object();
        private static readonly ConcurrentQueue<byte[]> PlayerQueue = new ConcurrentQueue<byte[]>();
        private static readonly ConcurrentQueue<byte[]> WorldQueue = new ConcurrentQueue<byte[]>();
        private static readonly Dictionary<string, PlayerState> Players = new Dictionary<string, PlayerState>();
        private static readonly Dictionary<string, WorldPartyState> WorldParties = new Dictionary<string, WorldPartyState>();

        private static int _selectedSlot = -1;
        private static bool _slotDialogOpen;
        private static bool _joinBypass;
        private static bool _creationOpen;
        private static MultiplayerCampaignVM _pendingJoinVm;
        private static int _pendingCreationSlot = -1;
        private static Hero _clientHero;
        private static MobileParty _clientParty;
        private static ulong _sequence;
        private static long _worldRevision;
        private static double _hostHours;
        private static float _hostSpeed = 4f;
        private static int _hostTimeMode = -1;
        private static DateTime _lastHostPacketUtc = DateTime.MinValue;
        private static DateTime _lastResyncRequestUtc = DateTime.MinValue;
        private static DateTime _lastWorldSendUtc = DateTime.MinValue;
        private static DateTime _lastPlayerSendUtc = DateTime.MinValue;
        private static bool _installed;
        private static bool _localReferenceSwapAttempted;

        private sealed class PlayerState
        {
            public string Id;
            public string Name;
            public CampaignVec2 Position;
            public CampaignVec2 Target;
            public int PartySize;
            public bool Moving;
            public float Heading;
            public ulong Sequence;
            public bool IsHost;
            public DateTime ReceivedUtc;
            public Hero Hero;
            public MobileParty Party;
        }

        private sealed class WorldPartyState
        {
            public string Id;
            public CampaignVec2 Position;
            public CampaignVec2 Target;
            public int PartySize;
            public bool Moving;
            public ulong Sequence;
            public DateTime ReceivedUtc;
        }

        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;
            CharacterSlots.Load();
        }

        public static void Reset()
        {
            PlayerState[] players;
            lock (Sync)
            {
                players = new PlayerState[Players.Count];
                Players.Values.CopyTo(players, 0);
                Players.Clear();
                WorldParties.Clear();
            }

            for (int i = 0; i < players.Length; i++)
                DestroyRemote(players[i]);

            while (PlayerQueue.TryDequeue(out _)) { }
            while (WorldQueue.TryDequeue(out _)) { }

            _selectedSlot = -1;
            _slotDialogOpen = false;
            _joinBypass = false;
            _creationOpen = false;
            _pendingJoinVm = null;
            _pendingCreationSlot = -1;
            _clientHero = null;
            _clientParty = null;
            _sequence = 0;
            _worldRevision = 0;
            _hostHours = 0;
            _hostTimeMode = -1;
            _lastHostPacketUtc = DateTime.MinValue;
            _lastResyncRequestUtc = DateTime.MinValue;
            _lastWorldSendUtc = DateTime.MinValue;
            _lastPlayerSendUtc = DateTime.MinValue;
            _localReferenceSwapAttempted = false;
        }

        public static int SelectedSlot => _selectedSlot;

        public static string SelectedCharacterName
        {
            get
            {
                return CharacterSlots.Exists(_selectedSlot)
                    ? CharacterSlots.GetName(_selectedSlot)
                    : string.Empty;
            }
        }

        public static bool HasSelectedCharacter()
        {
            return CharacterSlots.Exists(_selectedSlot);
        }

        public static bool ConsumeJoinBypass()
        {
            if (!_joinBypass)
                return false;

            _joinBypass = false;
            return true;
        }

        public static void BeginJoinCharacterSelection(MultiplayerCampaignVM vm)
        {
            if (_slotDialogOpen || vm == null)
                return;

            CharacterSlots.Load();
            _slotDialogOpen = true;
            ShowSlot1(vm);
        }

        private static void ShowSlot1(MultiplayerCampaignVM vm)
        {
            ShowSlotDialog(
                "CHARACTER SLOT 1",
                FormatSlot(0),
                "SELECT SLOT 1",
                "MORE SLOTS",
                () => SelectSlot(vm, 0),
                () => ShowSlot2(vm));
        }

        private static void ShowSlot2(MultiplayerCampaignVM vm)
        {
            ShowSlotDialog(
                "CHARACTER SLOTS 2 / 3",
                FormatSlot(1) + "\n\n" + FormatSlot(2),
                "SELECT SLOT 2",
                "SELECT SLOT 3",
                () => SelectSlot(vm, 1),
                () => SelectSlot(vm, 2));
        }

        private static string FormatSlot(int slot)
        {
            string name = CharacterSlots.GetName(slot);
            return "SLOT " + (slot + 1) + ": " +
                   (string.IsNullOrWhiteSpace(name) ? "EMPTY" : name);
        }

        private static void ShowSlotDialog(
            string title,
            string text,
            string affirmative,
            string negative,
            Action affirmativeAction,
            Action negativeAction)
        {
            InformationManager.ShowInquiry(
                new InquiryData(
                    title,
                    text + "\n\nChoose a character before joining the Host.",
                    true,
                    true,
                    affirmative,
                    negative,
                    affirmativeAction,
                    negativeAction),
                true,
                true);
        }

        private static void SelectSlot(MultiplayerCampaignVM vm, int slot)
        {
            _slotDialogOpen = false;
            _selectedSlot = slot;

            if (CharacterSlots.Exists(slot))
            {
                LocalPlayerState.SetDisplayName(CharacterSlots.GetName(slot));
                ContinueJoin(vm);
                return;
            }

            OpenNativeCharacterCreation(vm, slot);
        }

        private static void OpenNativeCharacterCreation(MultiplayerCampaignVM vm, int slot)
        {
            if (_creationOpen)
                return;

            _creationOpen = true;
            _pendingJoinVm = vm;
            _pendingCreationSlot = slot;

            try
            {
                GameStateManager manager = GameStateManager.Current;
                if (manager == null)
                    throw new InvalidOperationException("Character creation state manager is unavailable.");

                CharacterCreationState state =
                    manager.CreateState<CharacterCreationState>();

                manager.CleanAndPushState(state);
            }
            catch (Exception ex)
            {
                _creationOpen = false;
                HostConsole.WriteLine("[!] Character creation could not open: " + ex.Message);
                OpenFallbackNameEntry(vm, slot);
            }
        }

        private static void OpenFallbackNameEntry(MultiplayerCampaignVM vm, int slot)
        {
            InformationManager.ShowTextInquiry(
                new TextInquiryData(
                    "CHARACTER CREATION",
                    "The native character creator could not be opened. Enter the character name for this slot.",
                    true,
                    true,
                    "CREATE",
                    "CANCEL",
                    name =>
                    {
                        string clean = SanitizeName(name);
                        if (clean.Length == 0)
                        {
                            OpenFallbackNameEntry(vm, slot);
                            return;
                        }

                        CharacterSlots.Save(slot, clean);
                        LocalPlayerState.SetDisplayName(clean);
                        _creationOpen = false;
                        _pendingCreationSlot = -1;
                        _pendingJoinVm = null;
                        ContinueJoin(vm);
                    },
                    () =>
                    {
                        _creationOpen = false;
                        _pendingCreationSlot = -1;
                        _pendingJoinVm = null;
                    },
                    false,
                    text =>
                    {
                        string clean = SanitizeName(text);
                        return new Tuple<bool, string>(
                            clean.Length > 0,
                            clean.Length > 0 ? string.Empty : "Character name is required.");
                    },
                    string.Empty,
                    "Player"),
                true,
                true);
        }

        private static void ContinueJoin(MultiplayerCampaignVM vm)
        {
            if (vm == null)
                return;

            _joinBypass = true;
            try
            {
                vm.ExecuteJoinHost();
            }
            finally
            {
                _joinBypass = false;
            }
        }

        private static string ReadCreationName()
        {
            try
            {
                CharacterCreationState state =
                    GameStateManager.Current == null
                        ? null
                        : GameStateManager.Current.ActiveState as CharacterCreationState;

                if (state != null)
                {
                    object manager = state.CharacterCreationManager;
                    string value = FindNamedString(manager, "MainCharacterName", 3);
                    if (!string.IsNullOrWhiteSpace(value))
                        return SanitizeName(value);
                }
            }
            catch
            {
            }

            try
            {
                if (Hero.MainHero != null && Hero.MainHero.Name != null)
                    return SanitizeName(Hero.MainHero.Name.ToString());
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string FindNamedString(object root, string propertyName, int depth)
        {
            if (root == null || depth < 0)
                return string.Empty;

            try
            {
                Type type = root.GetType();
                PropertyInfo property = type.GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    object value = property.GetValue(root, null);
                    if (value is string)
                        return (string)value;
                }

                FieldInfo field = type.GetField(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    object value = field.GetValue(root);
                    if (value is string)
                        return (string)value;
                }

                if (depth == 0)
                    return string.Empty;

                PropertyInfo[] properties = type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < properties.Length && i < 32; i++)
                {
                    PropertyInfo p = properties[i];
                    if (p.GetIndexParameters().Length != 0 || !p.CanRead)
                        continue;

                    object value;
                    try
                    {
                        value = p.GetValue(root, null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value == null || value is string)
                        continue;

                    string found = FindNamedString(value, propertyName, depth - 1);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static void OnCharacterCreationFinalized()
        {
            if (!_creationOpen)
                return;

            int slot = _pendingCreationSlot;
            MultiplayerCampaignVM vm = _pendingJoinVm;
            string name = ReadCreationName();

            _creationOpen = false;
            _pendingCreationSlot = -1;
            _pendingJoinVm = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                OpenFallbackNameEntry(vm, slot);
                return;
            }

            CharacterSlots.Save(slot, name);
            _selectedSlot = slot;
            LocalPlayerState.SetDisplayName(name);
            ContinueJoin(vm);
        }

        public static void Tick(float dt)
        {
            if (Campaign.Current == null)
                return;

            if (dt < 0f || float.IsNaN(dt) || float.IsInfinity(dt))
                return;

            BootstrapClientPlayer();
            DrainQueues();
            ApplyAuthoritativeTime();
            ApplyRemotePlayers(dt);
            ApplyWorldParties(dt);

            DateTime now = DateTime.UtcNow;

            if (MultiplayerNetworkClient.Instance.IsConnected)
            {
                if (now - _lastPlayerSendUtc >= TimeSpan.FromMilliseconds(100))
                {
                    _lastPlayerSendUtc = now;
                    SendLocalPlayerState();
                }

                if (now - _lastHostPacketUtc >= TimeSpan.FromSeconds(5) &&
                    now - _lastResyncRequestUtc >= TimeSpan.FromSeconds(5))
                {
                    _lastResyncRequestUtc = now;
                    MultiplayerNetworkClient.Instance.RequestResync();
                }
            }

            if (IsHostSession() && now - _lastWorldSendUtc >= TimeSpan.FromMilliseconds(250))
            {
                _lastWorldSendUtc = now;
                BroadcastHostState();
            }

            CleanupStalePlayers();
        }

        private static bool IsHostSession()
        {
            try
            {
                return MultiplayerCampaignSubModule.IsHostRequested() &&
                       MultiplayerCampaignSubModule.GetHost() != null;
            }
            catch
            {
                return false;
            }
        }

        private static void BootstrapClientPlayer()
        {
            if (!MultiplayerNetworkClient.Instance.IsConnected ||
                Campaign.Current == null ||
                !HasSelectedCharacter())
                return;

            if (_clientHero == null)
            {
                try
                {
                    _clientHero = Hero.Find(CharacterSlots.GetId(_selectedSlot));
                }
                catch
                {
                }
            }

            if (_clientHero == null)
            {
                Hero mainHero = null;
                try { mainHero = Hero.MainHero; } catch { }

                if (mainHero == null || mainHero.CharacterObject == null)
                    return;

                Hero created;
                try
                {
                    if (HeroCreator.CreateBasicHero(
                            CharacterSlots.GetId(_selectedSlot),
                            mainHero.CharacterObject,
                            out created,
                            true))
                    {
                        _clientHero = created;
                    }
                    else
                    {
                        _clientHero = Hero.Find(CharacterSlots.GetId(_selectedSlot));
                    }
                }
                catch
                {
                    try { _clientHero = Hero.Find(CharacterSlots.GetId(_selectedSlot)); }
                    catch { _clientHero = null; }
                }

                if (_clientHero != null)
                {
                    try
                    {
                        _clientHero.SetName(
                            new TextObject(CharacterSlots.GetName(_selectedSlot)),
                            new TextObject(CharacterSlots.GetName(_selectedSlot)));
                    }
                    catch
                    {
                    }
                }
            }

            if (_clientHero == null || _clientHero == Hero.MainHero)
                return;

            if (_clientParty == null)
            {
                string storedPartyId = CharacterSlots.GetPartyId(_selectedSlot);
                if (!string.IsNullOrWhiteSpace(storedPartyId))
                    _clientParty = FindParty(storedPartyId);

                if (_clientParty == null)
                {
                    CampaignVec2 spawn = MobileParty.MainParty != null
                        ? MobileParty.MainParty.Position
                        : new CampaignVec2(new Vec2(0f, 0f), true);

                    try
                    {
                        _clientParty = MobilePartyHelper.SpawnLordParty(
                            _clientHero,
                            spawn,
                            0f);
                    }
                    catch
                    {
                        _clientParty = null;
                    }

                    if (_clientParty != null)
                    {
                        try
                        {
                            _clientParty.MemberRoster.AddToCounts(
                                _clientHero.CharacterObject,
                                5);
                        }
                        catch
                        {
                        }

                        CharacterSlots.SavePartyId(
                            _selectedSlot,
                            _clientParty.StringId);
                    }
                }
            }

            if (_clientParty == null || _clientParty == MobileParty.MainParty)
                return;

            TryMakeClientReferencesPrimary();
        }

        private static void TryMakeClientReferencesPrimary()
        {
            if (_localReferenceSwapAttempted || _clientHero == null || _clientParty == null)
                return;

            _localReferenceSwapAttempted = true;

            try
            {
                Hero oldHero = Hero.MainHero;
                MobileParty oldParty = MobileParty.MainParty;

                bool heroChanged = SetStaticReference(
                    typeof(Hero), "MainHero", oldHero, _clientHero);
                bool partyChanged = SetStaticReference(
                    typeof(MobileParty), "MainParty", oldParty, _clientParty);

                if (!heroChanged || !partyChanged)
                {
                    if (heroChanged)
                        SetStaticReference(typeof(Hero), "MainHero", _clientHero, oldHero);
                    if (partyChanged)
                        SetStaticReference(typeof(MobileParty), "MainParty", _clientParty, oldParty);
                    return;
                }

                try
                {
                    if (oldParty != null && oldParty != _clientParty)
                    {
                        oldParty.IsVisible = false;
                        oldParty.IsActive = false;
                    }
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        private static bool SetStaticReference(Type type, string memberName, object expected, object replacement)
        {
            try
            {
                PropertyInfo property = type.GetProperty(
                    memberName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (property != null)
                {
                    MethodInfo setter = property.GetSetMethod(true);
                    if (setter != null)
                    {
                        object current = property.GetValue(null, null);
                        if (expected == null || ReferenceEquals(current, expected))
                        {
                            setter.Invoke(null, new[] { replacement });
                            return true;
                        }
                    }
                }

                string[] names =
                {
                    "_" + char.ToLowerInvariant(memberName[0]) + memberName.Substring(1),
                    memberName,
                    "<" + memberName + ">k__BackingField"
                };

                for (int i = 0; i < names.Length; i++)
                {
                    FieldInfo field = type.GetField(
                        names[i],
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null)
                        continue;

                    object current = field.GetValue(null);
                    if (expected != null && !ReferenceEquals(current, expected))
                        continue;

                    field.SetValue(null, replacement);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void SendLocalPlayerState()
        {
            MobileParty party = _clientParty ?? MobileParty.MainParty;
            if (party == null || !MultiplayerNetworkClient.Instance.IsConnected)
                return;

            CampaignVec2 position = party.Position;
            CampaignVec2 target = GetTarget(party, position);
            bool moving = GetBool(party, "IsMoving");
            float heading = GetFloat(party, "MovementSpeed");

            byte[] payload = BuildPlayerPacket(
                PlayerIdentity.GetLocalId(),
                CharacterSlots.Exists(_selectedSlot)
                    ? CharacterSlots.GetName(_selectedSlot)
                    : LocalPlayerState.GetDisplayName(),
                position,
                target,
                GetPartySize(party),
                moving,
                heading,
                ++_sequence,
                CampaignTime.Now.ToHours,
                Campaign.Current.TimeControlMode,
                Campaign.Current.SpeedUpMultiplier,
                false);

            TryInvokeClientSend(PlayerStatePacket, payload);
        }

        private static void BroadcastHostState()
        {
            MultiplayerCampaignHost host = MultiplayerCampaignSubModule.GetHost();
            if (host == null || Campaign.Current == null || MobileParty.MainParty == null)
                return;

            List<byte[]> packets = new List<byte[]>();
            MobileParty hostParty = MobileParty.MainParty;
            packets.Add(BuildPlayerPacket(
                "HOST",
                LocalPlayerState.GetDisplayName(),
                hostParty.Position,
                GetTarget(hostParty, hostParty.Position),
                GetPartySize(hostParty),
                GetBool(hostParty, "IsMoving"),
                GetFloat(hostParty, "MovementSpeed"),
                ++_sequence,
                CampaignTime.Now.ToHours,
                Campaign.Current.TimeControlMode,
                Campaign.Current.SpeedUpMultiplier,
                true));

            lock (Sync)
            {
                foreach (PlayerState state in Players.Values)
                {
                    if (state == null || state.IsHost || string.IsNullOrWhiteSpace(state.Id))
                        continue;

                    packets.Add(BuildPlayerPacket(
                        state.Id,
                        state.Name,
                        state.Position,
                        state.Target,
                        state.PartySize,
                        state.Moving,
                        state.Heading,
                        state.Sequence,
                        CampaignTime.Now.ToHours,
                        Campaign.Current.TimeControlMode,
                        Campaign.Current.SpeedUpMultiplier,
                        false));
                }
            }

            foreach (HostConnection connection in GetHostConnections(host))
            {
                for (int i = 0; i < packets.Count; i++)
                    TryInvokeHostSend(host, connection, PlayerStatePacket, packets[i]);
            }

            BroadcastWorldParties(host);
        }

        private static void BroadcastWorldParties(MultiplayerCampaignHost host)
        {
            if (MobileParty.AllLordParties == null)
                return;

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(WorldStatePacket);
                writer.Write(++_worldRevision);

                int count = 0;
                for (int i = 0; i < MobileParty.AllLordParties.Count; i++)
                {
                    MobileParty party = MobileParty.AllLordParties[i];
                    if (IsWorldParty(party))
                        count++;
                }
                writer.Write(count);

                for (int i = 0; i < MobileParty.AllLordParties.Count; i++)
                {
                    MobileParty party = MobileParty.AllLordParties[i];
                    if (!IsWorldParty(party))
                        continue;

                    CampaignVec2 position = party.Position;
                    CampaignVec2 target = GetTarget(party, position);
                    writer.Write(party.StringId);
                    writer.Write(position.X);
                    writer.Write(position.Y);
                    writer.Write(target.X);
                    writer.Write(target.Y);
                    writer.Write(GetPartySize(party));
                    writer.Write(GetBool(party, "IsMoving"));
                    writer.Write(++_sequence);
                }

                byte[] payload = stream.ToArray();
                foreach (HostConnection connection in GetHostConnections(host))
                    TryInvokeHostSend(host, connection, WorldStatePacket, payload);
            }
        }

        private static bool IsWorldParty(MobileParty party)
        {
            return party != null &&
                   !string.IsNullOrWhiteSpace(party.StringId) &&
                   party != MobileParty.MainParty &&
                   party != _clientParty &&
                   !RemotePlayerManager.IsRemoteParty(party) &&
                   IsFinite(party.Position.X) &&
                   IsFinite(party.Position.Y);
        }

        private static byte[] BuildPlayerPacket(
            string id,
            string name,
            CampaignVec2 position,
            CampaignVec2 target,
            int partySize,
            bool moving,
            float heading,
            ulong sequence,
            double hostHours,
            CampaignTimeControlMode timeMode,
            float speed,
            bool isHost)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(PlayerStatePacket);
                writer.Write(sequence);
                writer.Write(hostHours);
                writer.Write((int)timeMode);
                writer.Write(speed);
                writer.Write(isHost);
                writer.Write(id ?? string.Empty);
                writer.Write(SanitizeName(name));
                writer.Write(position.X);
                writer.Write(position.Y);
                writer.Write(target.X);
                writer.Write(target.Y);
                writer.Write(Math.Max(1, Math.Min(10000, partySize)));
                writer.Write(moving);
                writer.Write(IsFinite(heading) ? heading : 0f);
                return stream.ToArray();
            }
        }

        public static bool QueuePlayerPacket(byte[] payload)
        {
            if (!ValidateHeader(payload, PlayerStatePacket))
                return false;

            PlayerQueue.Enqueue((byte[])payload.Clone());
            return true;
        }

        public static bool QueueWorldPacket(byte[] payload)
        {
            if (!ValidateHeader(payload, WorldStatePacket))
                return false;

            WorldQueue.Enqueue((byte[])payload.Clone());
            return true;
        }

        private static bool ValidateHeader(byte[] payload, byte expectedType)
        {
            if (payload == null || payload.Length < 6 || payload.Length > 1024 * 1024)
                return false;

            try
            {
                using (MemoryStream stream = new MemoryStream(payload))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadInt32() == Magic &&
                           reader.ReadByte() == Version &&
                           reader.ReadByte() == expectedType;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void DrainQueues()
        {
            byte[] payload;
            while (PlayerQueue.TryDequeue(out payload))
                ReadPlayer(payload);

            while (WorldQueue.TryDequeue(out payload))
                ReadWorld(payload);
        }

        private static void ReadPlayer(byte[] payload)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(payload))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    reader.ReadInt32();
                    reader.ReadByte();
                    reader.ReadByte();
                    ulong sequence = reader.ReadUInt64();
                    double hostHours = reader.ReadDouble();
                    int timeMode = reader.ReadInt32();
                    float speed = reader.ReadSingle();
                    bool isHost = reader.ReadBoolean();
                    string id = reader.ReadString();
                    string name = SanitizeName(reader.ReadString());
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float tx = reader.ReadSingle();
                    float ty = reader.ReadSingle();
                    int partySize = reader.ReadInt32();
                    bool moving = reader.ReadBoolean();
                    float heading = reader.ReadSingle();

                    if (!IsValidId(id) ||
                        !IsFinite(x) || !IsFinite(y) ||
                        !IsFinite(tx) || !IsFinite(ty) ||
                        !IsFinite(speed) || !IsFinite(heading))
                        return;

                    lock (Sync)
                    {
                        PlayerState previous;
                        if (Players.TryGetValue(id, out previous) && sequence <= previous.Sequence)
                            return;

                        Players[id] = new PlayerState
                        {
                            Id = id,
                            Name = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                            Position = new CampaignVec2(new Vec2(x, y), true),
                            Target = new CampaignVec2(new Vec2(tx, ty), true),
                            PartySize = Math.Max(1, Math.Min(10000, partySize)),
                            Moving = moving,
                            Heading = heading,
                            Sequence = sequence,
                            IsHost = isHost,
                            ReceivedUtc = DateTime.UtcNow,
                            Hero = previous == null ? null : previous.Hero,
                            Party = previous == null ? null : previous.Party
                        };
                    }

                    if (isHost)
                    {
                        _hostHours = hostHours;
                        _hostTimeMode = timeMode;
                        _hostSpeed = Math.Max(0f, Math.Min(100f, speed));
                        _lastHostPacketUtc = DateTime.UtcNow;
                    }
                }
            }
            catch
            {
            }
        }

        private static void ReadWorld(byte[] payload)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(payload))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    reader.ReadInt32();
                    reader.ReadByte();
                    reader.ReadByte();
                    long revision = reader.ReadInt64();

                    if (revision < _worldRevision)
                        return;

                    int count = reader.ReadInt32();
                    if (count < 0 || count > 100000)
                        return;

                    Dictionary<string, WorldPartyState> next = new Dictionary<string, WorldPartyState>();
                    for (int i = 0; i < count; i++)
                    {
                        string id = reader.ReadString();
                        float x = reader.ReadSingle();
                        float y = reader.ReadSingle();
                        float tx = reader.ReadSingle();
                        float ty = reader.ReadSingle();
                        int size = reader.ReadInt32();
                        bool moving = reader.ReadBoolean();
                        ulong sequence = reader.ReadUInt64();

                        if (string.IsNullOrWhiteSpace(id) ||
                            !IsFinite(x) || !IsFinite(y) ||
                            !IsFinite(tx) || !IsFinite(ty))
                            continue;

                        next[id] = new WorldPartyState
                        {
                            Id = id,
                            Position = new CampaignVec2(new Vec2(x, y), true),
                            Target = new CampaignVec2(new Vec2(tx, ty), true),
                            PartySize = Math.Max(0, Math.Min(10000, size)),
                            Moving = moving,
                            Sequence = sequence,
                            ReceivedUtc = DateTime.UtcNow
                        };
                    }

                    lock (Sync)
                    {
                        _worldRevision = revision;
                        foreach (KeyValuePair<string, WorldPartyState> item in next)
                        {
                            WorldPartyState old;
                            if (!WorldParties.TryGetValue(item.Key, out old) || item.Value.Sequence > old.Sequence)
                                WorldParties[item.Key] = item.Value;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void ApplyAuthoritativeTime()
        {
            if (!MultiplayerNetworkClient.Instance.IsConnected ||
                !_lastHostPacketUtc.Ticks.Equals(DateTime.MinValue.Ticks) ||
                !_lastHostPacketUtc.Equals(DateTime.MinValue))
            {
                if (!_lastHostPacketUtc.Equals(DateTime.MinValue) && Campaign.Current != null)
                {
                    try
                    {
                        if (_hostTimeMode >= 0 && _hostTimeMode <= 16)
                            Campaign.Current.TimeControlMode = (CampaignTimeControlMode)_hostTimeMode;
                        Campaign.Current.SpeedUpMultiplier = _hostSpeed;
                    }
                    catch
                    {
                    }

                    try
                    {
                        double localHours = CampaignTime.Now.ToHours;
                        if (Math.Abs(localHours - _hostHours) > 0.5d)
                            TrySetCampaignTime(_hostHours);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool TrySetCampaignTime(double hours)
        {
            try
            {
                Campaign campaign = Campaign.Current;
                if (campaign == null)
                    return false;

                CampaignTime target = CampaignTime.Hours((float)hours);
                MethodInfo[] methods = campaign.GetType().GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!string.Equals(method.Name, "SetTime", StringComparison.OrdinalIgnoreCase))
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CampaignTime))
                    {
                        method.Invoke(campaign, new object[] { target });
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ApplyRemotePlayers(float dt)
        {
            PlayerState[] snapshot;
            lock (Sync)
            {
                snapshot = new PlayerState[Players.Count];
                Players.Values.CopyTo(snapshot, 0);
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                PlayerState state = snapshot[i];
                if (state == null || state.Id == PlayerIdentity.GetLocalId())
                    continue;

                if (state.Hero == null || state.Party == null)
                    CreateRemoteObjects(state);

                if (state.Party == null)
                    continue;

                try
                {
                    CampaignVec2 current = state.Party.Position;
                    float alpha = Math.Max(0.05f, Math.Min(1f, dt * 10f));
                    state.Party.Position = new CampaignVec2(
                        new Vec2(
                            current.X + (state.Target.X - current.X) * alpha,
                            current.Y + (state.Target.Y - current.Y) * alpha),
                        true);
                }
                catch
                {
                    state.Party = null;
                }

                try { UpdateRemoteRoster(state); } catch { }
            }
        }

        private static void CreateRemoteObjects(PlayerState state)
        {
            if (Campaign.Current == null || Hero.MainHero == null ||
                Hero.MainHero.CharacterObject == null || state == null)
                return;

            if (state.Hero != null && state.Party != null)
                return;

            try
            {
                string heroId = "mpc_remote_" + SanitizeId(state.Id);
                Hero hero = Hero.Find(heroId);

                if (hero == null)
                {
                    Hero created;
                    if (HeroCreator.CreateBasicHero(
                            heroId,
                            Hero.MainHero.CharacterObject,
                            out created,
                            true))
                        hero = created;
                    else
                        hero = Hero.Find(heroId);
                }

                if (hero == null || hero == Hero.MainHero)
                    return;

                hero.SetName(new TextObject(state.Name), new TextObject(state.Name));

                MobileParty party = MobilePartyHelper.SpawnLordParty(
                    hero,
                    state.Position,
                    0f);

                if (party == null || party == MobileParty.MainParty)
                    return;

                party.SetMoveModeHold();
                party.Position = state.Position;

                try
                {
                    party.MemberRoster.AddToCounts(
                        Hero.MainHero.CharacterObject,
                        Math.Max(1, Math.Min(10000, state.PartySize)));
                }
                catch
                {
                }

                state.Hero = hero;
                state.Party = party;
            }
            catch
            {
            }
        }

        private static void UpdateRemoteRoster(PlayerState state)
        {
            if (state.Party == null || state.Party.MemberRoster == null ||
                Hero.MainHero == null || Hero.MainHero.CharacterObject == null)
                return;

            int desired = Math.Max(1, Math.Min(10000, state.PartySize));
            int current = state.Party.MemberRoster.TotalManCount;
            if (current < desired)
            {
                state.Party.MemberRoster.AddToCounts(
                    Hero.MainHero.CharacterObject,
                    desired - current);
            }
        }

        private static void ApplyWorldParties(float dt)
        {
            WorldPartySyncState[] snapshot;
            lock (Sync)
            {
                snapshot = new WorldPartySyncState[WorldParties.Count];
                WorldParties.Values.CopyTo(snapshot, 0);
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                WorldPartySyncState state = snapshot[i];
                if (state == null)
                    continue;

                MobileParty party = FindParty(state.Id);
                if (party == null || party == MobileParty.MainParty ||
                    party == _clientParty || RemotePlayerManager.IsRemoteParty(party))
                    continue;

                try
                {
                    CampaignVec2 current = party.Position;
                    float alpha = Math.Max(0.05f, Math.Min(1f, dt * 7f));
                    party.Position = new CampaignVec2(
                        new Vec2(
                            current.X + (state.Position.X - current.X) * alpha,
                            current.Y + (state.Position.Y - current.Y) * alpha),
                        true);
                }
                catch
                {
                }
            }
        }

        private static MobileParty FindParty(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || MobileParty.AllLordParties == null)
                return null;

            for (int i = 0; i < MobileParty.AllLordParties.Count; i++)
            {
                MobileParty party = MobileParty.AllLordParties[i];
                if (party != null && string.Equals(party.StringId, id, StringComparison.Ordinal))
                    return party;
            }

            return null;
        }

        private static void CleanupStalePlayers()
        {
            List<string> remove = new List<string>();
            DateTime now = DateTime.UtcNow;

            lock (Sync)
            {
                foreach (KeyValuePair<string, PlayerState> item in Players)
                {
                    if (now - item.Value.ReceivedUtc > TimeSpan.FromSeconds(12))
                        remove.Add(item.Key);
                }

                for (int i = 0; i < remove.Count; i++)
                {
                    PlayerState state;
                    if (Players.TryGetValue(remove[i], out state))
                        DestroyRemote(state);
                    Players.Remove(remove[i]);
                }
            }
        }

        public static void RemovePlayer(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                return;

            lock (Sync)
            {
                PlayerState state;
                if (Players.TryGetValue(playerId, out state))
                    DestroyRemote(state);
                Players.Remove(playerId);
            }
        }

        private static void DestroyRemote(PlayerState state)
        {
            if (state == null)
                return;

            try
            {
                if (state.Party != null && state.Party != MobileParty.MainParty)
                    DestroyPartyAction.Apply(null, state.Party);
            }
            catch
            {
            }

            state.Party = null;
            state.Hero = null;
        }

        private static void TryInvokeClientSend(byte packetType, byte[] payload)
        {
            try
            {
                MethodInfo method = AccessTools.Method(
                    typeof(MultiplayerNetworkClient),
                    "SendPacket",
                    new[] { typeof(NetworkPacketType), typeof(byte[]) });
                if (method == null)
                    return;

                object type = Enum.ToObject(typeof(NetworkPacketType), packetType);
                method.Invoke(MultiplayerNetworkClient.Instance, new object[] { type, payload });
            }
            catch
            {
            }
        }

        private static void TryInvokeHostSend(
            MultiplayerCampaignHost host,
            HostConnection connection,
            byte packetType,
            byte[] payload)
        {
            try
            {
                MethodInfo method = AccessTools.Method(
                    typeof(MultiplayerCampaignHost),
                    "SendDirect",
                    new[] { typeof(HostConnection), typeof(NetworkPacketType), typeof(byte[]) });
                if (method == null)
                    return;

                object type = Enum.ToObject(typeof(NetworkPacketType), packetType);
                method.Invoke(host, new object[] { connection, type, payload });
            }
            catch
            {
            }
        }

        private static IEnumerable<HostConnection> GetHostConnections(MultiplayerCampaignHost host)
        {
            List<HostConnection> result = new List<HostConnection>();
            if (host == null)
                return result;

            try
            {
                FieldInfo field = AccessTools.Field(typeof(MultiplayerCampaignHost), "_connections");
                object dict = field == null ? null : field.GetValue(host);
                if (dict == null)
                    return result;

                PropertyInfo values = dict.GetType().GetProperty("Values");
                IEnumerable enumerable = values == null ? null : values.GetValue(dict, null) as IEnumerable;
                if (enumerable == null)
                    return result;

                foreach (object item in enumerable)
                {
                    HostConnection connection = item as HostConnection;
                    if (connection != null)
                        result.Add(connection);
                }
            }
            catch
            {
            }

            return result;
        }

        private static CampaignVec2 GetTarget(MobileParty party, CampaignVec2 fallback)
        {
            try
            {
                object value = ReadMember(party, "TargetPosition");
                if (value is CampaignVec2)
                    return (CampaignVec2)value;

                value = ReadMember(party, "TargetParty");
                MobileParty targetParty = value as MobileParty;
                if (targetParty != null)
                    return targetParty.Position;
            }
            catch
            {
            }

            return fallback;
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null)
                return null;

            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0 && property.CanRead)
                return property.GetValue(instance, null);

            FieldInfo field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance);
        }

        private static bool GetBool(object instance, string name)
        {
            try
            {
                object value = ReadMember(instance, name);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private static float GetFloat(object instance, string name)
        {
            try
            {
                object value = ReadMember(instance, name);
                if (value is float)
                    return (float)value;
                if (value is double)
                    return (float)(double)value;
            }
            catch
            {
            }

            return 0f;
        }

        private static int GetPartySize(MobileParty party)
        {
            try
            {
                if (party == null || party.MemberRoster == null)
                    return 1;
                return Math.Max(1, Math.Min(10000, party.MemberRoster.TotalManCount));
            }
            catch
            {
                return 1;
            }
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string clean = value.Trim()
                .Replace("|", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty);

            if (clean.Length > 32)
                clean = clean.Substring(0, 32);

            return clean;
        }

        private static bool IsValidId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
        }

        private static string SanitizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "player";

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    builder.Append(c);
            }
            return builder.Length == 0 ? "player" : builder.ToString();
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    internal static class CharacterSlots
    {
        private const int Count = 3;
        private static readonly object Sync = new object();
        private static readonly string[] Names = new string[Count];
        private static readonly string[] Ids = new string[Count];
        private static readonly string[] PartyIds = new string[Count];
        private static bool _loaded;

        private static string DirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MultiplayerCampaign");

        private static string FilePath => Path.Combine(
            DirectoryPath,
            "character_slots.txt");

        public static void Load()
        {
            lock (Sync)
            {
                if (_loaded)
                    return;

                _loaded = true;
                for (int i = 0; i < Count; i++)
                {
                    Names[i] = string.Empty;
                    Ids[i] = string.Empty;
                    PartyIds[i] = string.Empty;
                }

                try
                {
                    if (!File.Exists(FilePath))
                        return;

                    string[] lines = File.ReadAllLines(FilePath);
                    for (int i = 0; i < lines.Length && i < Count; i++)
                    {
                        string[] parts = (lines[i] ?? string.Empty).Split(new[] { '|' }, 3);
                        if (parts.Length > 0) Names[i] = Clean(parts[0]);
                        if (parts.Length > 1) Ids[i] = Clean(parts[1]);
                        if (parts.Length > 2) PartyIds[i] = Clean(parts[2]);
                    }
                }
                catch
                {
                }
            }
        }

        public static bool Exists(int slot)
        {
            Load();
            lock (Sync)
                return slot >= 0 && slot < Count &&
                       !string.IsNullOrWhiteSpace(Names[slot]) &&
                       !string.IsNullOrWhiteSpace(Ids[slot]);
        }

        public static string GetName(int slot)
        {
            Load();
            lock (Sync)
                return slot >= 0 && slot < Count ? Names[slot] : string.Empty;
        }

        public static string GetId(int slot)
        {
            Load();
            lock (Sync)
                return slot >= 0 && slot < Count ? Ids[slot] : string.Empty;
        }

        public static string GetPartyId(int slot)
        {
            Load();
            lock (Sync)
                return slot >= 0 && slot < Count ? PartyIds[slot] : string.Empty;
        }

        public static void Save(int slot, string name)
        {
            Load();
            if (slot < 0 || slot >= Count)
                return;

            string clean = Clean(name);
            if (string.IsNullOrWhiteSpace(clean))
                return;

            lock (Sync)
            {
                Names[slot] = clean;
                if (string.IsNullOrWhiteSpace(Ids[slot]))
                    Ids[slot] = "mpc_char_" + Guid.NewGuid().ToString("N");
                PersistLocked();
            }
        }

        public static void SavePartyId(int slot, string partyId)
        {
            Load();
            if (slot < 0 || slot >= Count)
                return;

            lock (Sync)
            {
                PartyIds[slot] = Clean(partyId);
                PersistLocked();
            }
        }

        private static void PersistLocked()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string[] lines = new string[Count];
                for (int i = 0; i < Count; i++)
                {
                    lines[i] = (Names[i] ?? string.Empty) + "|" +
                               (Ids[i] ?? string.Empty) + "|" +
                               (PartyIds[i] ?? string.Empty);
                }
                File.WriteAllLines(FilePath, lines);
            }
            catch
            {
            }
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Trim()
                .Replace("|", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty);
        }
    }

    [HarmonyPatch]
    internal static class MpcLoadPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MultiplayerCampaignSubModule), "OnSubModuleLoad");
        }

        private static void Postfix()
        {
            MpcRebuildRuntimeV2.Install();
        }
    }

    [HarmonyPatch]
    internal static class MpcJoinPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MultiplayerCampaignVM), "ExecuteJoinHost");
        }

        private static bool Prefix(MultiplayerCampaignVM __instance)
        {
            if (MpcRebuildRuntimeV2.ConsumeJoinBypass())
                return true;

            if (!MpcRebuildRuntimeV2.HasSelectedCharacter())
            {
                MpcRebuildRuntimeV2.BeginJoinCharacterSelection(__instance);
                return false;
            }

            LocalPlayerState.SetDisplayName(
                MpcRebuildRuntimeV2.SelectedCharacterName);
            return true;
        }
    }

    [HarmonyPatch]
    internal static class MpcCampaignStartPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MultiplayerCampaignSubModule), "OnGameStart");
        }

        private static void Postfix(Game game, IGameStarter gameStarter)
        {
            try
            {
                CampaignGameStarter starter = gameStarter as CampaignGameStarter;
                if (starter != null && game != null && game.GameType is Campaign)
                    starter.AddBehavior(new MpcRebuildBehavior());
            }
            catch
            {
            }
        }
    }

    internal sealed class MpcRebuildBehavior : CampaignBehaviorBase
    {
        private bool _registered;
        private float _timer;

        public override void RegisterEvents()
        {
            if (_registered)
                return;

            _registered = true;
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
        }

        private void OnTick(float dt)
        {
            try
            {
                _timer += Math.Max(0f, Math.Min(1f, dt));
                if (_timer < 0.05f)
                    return;

                float elapsed = _timer;
                _timer = 0f;
                MpcRebuildRuntimeV2.Tick(elapsed);
            }
            catch
            {
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
        }
    }

    [HarmonyPatch]
    internal static class MpcClientMessagePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MultiplayerNetworkClient), "ProcessMessage");
        }

        private static bool Prefix(NetworkMessage message)
        {
            try
            {
                if (message == null || message.Payload == null)
                    return true;

                byte rawType = (byte)message.Type;
                if (rawType == 14 && MpcRebuildRuntimeV2.QueuePlayerPacket(message.Payload))
                    return false;
                if (rawType == 15 && MpcRebuildRuntimeV2.QueueWorldPacket(message.Payload))
                    return false;
            }
            catch
            {
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class MpcHostPlayerPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MultiplayerCampaignHost), "ApplyPlayerSnapshot");
        }

        private static bool Prefix(byte[] payload)
        {
            try
            {
                if (MpcRebuildRuntimeV2.QueuePlayerPacket(payload))
                    return false;
            }
            catch
            {
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class MpcCharacterFinalizationPatch
    {
        private static MethodBase TargetMethod()
        {
            Type screenType = AccessTools.TypeByName(
                "SandBox.View.CharacterCreation.CharacterCreationScreen");
            return screenType == null
                ? null
                : AccessTools.Method(screenType, "OnCharacterCreationFinalized");
        }

        private static void Postfix()
        {
            MpcRebuildRuntimeV2.OnCharacterCreationFinalized();
        }
    }

    [HarmonyPatch]
    internal static class MpcRemoteLeavePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(RemotePlayerManager), "QueueLeave");
        }

        private static void Prefix(string playerId)
        {
            MpcRebuildRuntimeV2.RemovePlayer(playerId);
        }
    }

    [HarmonyPatch]
    internal static class MpcShutdownPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MultiplayerCampaignSubModule), "OnGameEnd");
        }

        private static void Prefix()
        {
            MpcRebuildRuntimeV2.Reset();
        }
    }

    [HarmonyPatch]
    internal static class MpcMapInfoSafetyPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("SandBox.View.Map.MapInfoVM");
            return type == null ? null : AccessTools.Method(type, "UpdatePlayerInfo");
        }

        private static bool Prefix()
        {
            try
            {
                if (Campaign.Current == null)
                    return false;

                // In multiplayer we do not allow an incomplete selected-player object
                // to enter the vanilla map-info update path. Remote parties have their
                // own safe visual state handled by the rebuild runtime.
                return !MultiplayerNetworkClient.Instance.IsConnected &&
                       !MultiplayerCampaignSubModule.IsHostRequested();
            }
            catch
            {
                return false;
            }
        }
    }
}
