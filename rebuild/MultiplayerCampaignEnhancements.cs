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
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ViewModelCollection.InitialMenu;

namespace MultiplayerCampaign
{
    /*
     * ============================================================
     * REBUILD / MULTIPLAYER EXTENSION LAYER
     * ============================================================
     *
     * This file extends the copied 6304-line core without replacing it.
     * It is deliberately defensive: network callbacks only queue data;
     * Campaign objects are touched from CampaignEvents.TickEvent.
     *
     * Scope of this stage:
     *  - 3 client character slots
     *  - mandatory slot selection before Join
     *  - persistent character identity/name
     *  - independent remote Hero/MobileParty objects
     *  - authoritative Host time/state snapshots
     *  - player position/target/movement state
     *  - NPC party position/target/movement snapshots
     *  - sequence/revision validation
     *  - remote cleanup and crash guards
     *  - safe MapInfoVM suppression when its player data is incomplete
     *
     * Battle/Siege/Encounter/Quest/Economy/Combat/Inventory/AI-decision
     * synchronization is intentionally not implemented in this stage.
     * ============================================================
     */

    internal static class MpcRebuildRuntime
    {
        private const int SlotCount = 3;
        private const int ProtocolMagic = 0x4D504358; // MPCX
        private const byte ProtocolVersion = 1;
        private const byte PlayerStateType = 1;
        private const byte WorldPartyStateType = 2;

        private static readonly object Sync = new object();
        private static readonly ConcurrentQueue<QueuedState> IncomingStates =
            new ConcurrentQueue<QueuedState>();
        private static readonly Dictionary<string, PlayerSyncState> RemotePlayers =
            new Dictionary<string, PlayerSyncState>();
        private static readonly Dictionary<string, WorldPartySyncState> WorldParties =
            new Dictionary<string, WorldPartySyncState>();

        private static bool _installed;
        private static bool _joinBypass;
        private static bool _slotDialogOpen;
        private static bool _worldBootstrapped;
        private static long _lastWorldRevision;
        private static ulong _localSequence;
        private static int _selectedSlot = -1;
        private static double _targetHostHours;
        private static bool _haveHostTime;
        private static float _hostSpeed = 4f;
        private static int _hostMode = -1;
        private static DateTime _lastHostPacketUtc = DateTime.MinValue;
        private static Hero _clientHero;
        private static MobileParty _clientParty;

        private sealed class QueuedState
        {
            public byte Kind;
            public byte[] Payload;
            public DateTime ReceivedUtc;
        }

        private sealed class PlayerSyncState
        {
            public string Id;
            public string Name;
            public CampaignVec2 Position;
            public CampaignVec2 Target;
            public int PartySize;
            public bool Moving;
            public float Heading;
            public ulong Sequence;
            public DateTime ReceivedUtc;
            public MobileParty Party;
            public Hero Hero;
            public bool IsHost;
        }

        private sealed class WorldPartySyncState
        {
            public string Id;
            public CampaignVec2 Position;
            public CampaignVec2 Target;
            public int Size;
            public bool Moving;
            public ulong Sequence;
            public DateTime ReceivedUtc;
        }

        public static void Install()
        {
            lock (Sync)
            {
                if (_installed)
                    return;

                _installed = true;
                CharacterSlotStore.Load();
            }
        }

        public static void Reset()
        {
            lock (Sync)
            {
                _selectedSlot = -1;
                _slotDialogOpen = false;
                _worldBootstrapped = false;
                _lastWorldRevision = 0;
                _localSequence = 0;
                _targetHostHours = 0;
                _haveHostTime = false;
                _lastHostPacketUtc = DateTime.MinValue;
                _clientHero = null;
                _clientParty = null;
                RemotePlayers.Clear();
                WorldParties.Clear();
            }

            while (IncomingStates.TryDequeue(out _)) { }
        }

        public static bool HasSelectedCharacter()
        {
            return _selectedSlot >= 0 &&
                   _selectedSlot < SlotCount &&
                   CharacterSlotStore.Exists(_selectedSlot);
        }

        public static int SelectedSlot => _selectedSlot;

        public static string SelectedName
        {
            get
            {
                return HasSelectedCharacter()
                    ? CharacterSlotStore.GetName(_selectedSlot)
                    : string.Empty;
            }
        }

        public static void ShowCharacterSelection(MultiplayerCampaignVM vm)
        {
            if (_slotDialogOpen)
                return;

            CharacterSlotStore.Load();
            _slotDialogOpen = true;

            string[] names = new string[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                string name = CharacterSlotStore.GetName(i);
                names[i] = string.IsNullOrWhiteSpace(name)
                    ? "EMPTY"
                    : name;
            }

            ShowSlot1(vm, names);
        }

        private static void ShowSlot1(MultiplayerCampaignVM vm, string[] names)
        {
            InformationManager.ShowInquiry(
                new InquiryData(
                    "CHARACTER SLOT",
                    "Choose a character slot before joining the Host.\n\nSLOT 1: " + names[0] +
                    "\nSLOT 2: " + names[1] + "\nSLOT 3: " + names[2],
                    true,
                    true,
                    "SELECT SLOT 1",
                    "CANCEL",
                    () => SelectSlotAndContinue(vm, 0),
                    () => CloseSlotDialog()),
                true,
                true);
        }

        private static void SelectSlotAndContinue(MultiplayerCampaignVM vm, int slot)
        {
            _slotDialogOpen = false;
            _selectedSlot = slot;

            if (CharacterSlotStore.Exists(slot))
            {
                LocalPlayerState.SetDisplayName(CharacterSlotStore.GetName(slot));
                InvokeOriginalJoin(vm);
                return;
            }

            AskNewCharacterName(vm, slot);
        }

        private static void AskNewCharacterName(MultiplayerCampaignVM vm, int slot)
        {
            InformationManager.ShowTextInquiry(
                new TextInquiryData(
                    "CHARACTER CREATION",
                    "This slot is empty. Create the character for this slot.\n\nEnter the character name:",
                    true,
                    true,
                    "CREATE",
                    "CANCEL",
                    name => FinishCharacterCreation(vm, slot, name),
                    () => CloseSlotDialog(),
                    false,
                    text =>
                    {
                        string clean = SanitizeName(text);
                        if (clean.Length == 0)
                            return new Tuple<bool, string>(false, "Character name is required.");
                        return new Tuple<bool, string>(true, string.Empty);
                    },
                    string.Empty,
                    "Player"),
                true,
                true);
        }

        private static void FinishCharacterCreation(MultiplayerCampaignVM vm, int slot, string name)
        {
            string clean = SanitizeName(name);
            if (clean.Length == 0)
            {
                AskNewCharacterName(vm, slot);
                return;
            }

            CharacterSlotStore.Save(slot, clean);
            _selectedSlot = slot;
            LocalPlayerState.SetDisplayName(clean);
            InvokeOriginalJoin(vm);
        }

        private static void CloseSlotDialog()
        {
            _slotDialogOpen = false;
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string clean = value.Trim();
            clean = clean.Replace("|", string.Empty)
                         .Replace("\r", string.Empty)
                         .Replace("\n", string.Empty);

            if (clean.Length > 32)
                clean = clean.Substring(0, 32);

            return clean;
        }

        private static void InvokeOriginalJoin(MultiplayerCampaignVM vm)
        {
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

        public static bool ConsumeJoinBypass()
        {
            if (!_joinBypass)
                return false;

            _joinBypass = false;
            return true;
        }

        public static void BootstrapClientCharacter()
        {
            if (Campaign.Current == null || !HasSelectedCharacter())
                return;

            string id = CharacterSlotStore.GetId(_selectedSlot);
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (_clientHero != null && _clientParty != null)
                return;

            try
            {
                Hero existing = Hero.Find(id);
                if (existing != null)
                    _clientHero = existing;
            }
            catch
            {
            }

            if (_clientHero == null)
            {
                Hero templateHero = Hero.MainHero;
                if (templateHero == null || templateHero.CharacterObject == null)
                    return;

                Hero created;
                if (!HeroCreator.CreateBasicHero(
                        id,
                        templateHero.CharacterObject,
                        out created,
                        true))
                {
                    try
                    {
                        created = Hero.Find(id);
                    }
                    catch
                    {
                        created = null;
                    }
                }

                if (created == null || created == Hero.MainHero)
                    return;

                created.SetName(
                    new TextObject(CharacterSlotStore.GetName(_selectedSlot)),
                    new TextObject(CharacterSlotStore.GetName(_selectedSlot)));
                _clientHero = created;
            }

            if (_clientParty == null)
            {
                CampaignVec2 spawn = MobileParty.MainParty != null
                    ? MobileParty.MainParty.Position
                    : new CampaignVec2(new Vec2(0f, 0f), true);

                MobileParty party = MobilePartyHelper.SpawnLordParty(
                    _clientHero,
                    spawn,
                    0f);

                if (party == null || party == MobileParty.MainParty)
                {
                    _clientParty = null;
                    return;
                }

                _clientParty = party;

                try
                {
                    _clientParty.MemberRoster.AddToCounts(
                        _clientHero.CharacterObject,
                        5);
                }
                catch
                {
                }

                CharacterSlotStore.SaveRuntimePartyId(
                    _selectedSlot,
                    _clientParty.StringId);
            }

            _worldBootstrapped = true;
        }

        public static void Tick(float dt)
        {
            if (Campaign.Current == null)
                return;

            if (dt < 0f || float.IsNaN(dt) || float.IsInfinity(dt))
                return;

            BootstrapClientCharacter();
            DrainNetworkQueue();
            ApplyHostTime();
            ApplyRemotePlayers(dt);
            ApplyWorldParties(dt);

            if (IsClientSession())
                SendLocalState();
            else if (IsHostSession())
                BroadcastAuthoritativeState();

            CleanupStaleStates();
        }

        private static bool IsClientSession()
        {
            return MultiplayerNetworkClient.Instance.IsConnected;
        }

        private static bool IsHostSession()
        {
            return MultiplayerCampaignSubModule.IsHostRequested() &&
                   MultiplayerCampaignSubModule.GetHost() != null;
        }

        private static void SendLocalState()
        {
            MobileParty party = _clientParty ?? MobileParty.MainParty;
            if (party == null)
                return;

            CampaignVec2 position = party.Position;
            CampaignVec2 target = TryGetTargetPosition(party, position);
            bool moving = TryGetBoolProperty(party, "IsMoving");
            float heading = TryGetFloatProperty(party, "MovementSpeed");
            string playerId = PlayerIdentity.GetLocalId();
            string name = HasSelectedCharacter()
                ? CharacterSlotStore.GetName(_selectedSlot)
                : LocalPlayerState.GetDisplayName();

            byte[] payload = BuildPlayerPayload(
                playerId,
                name,
                position,
                target,
                GetPartySize(party),
                moving,
                heading,
                ++_localSequence,
                CampaignTime.Now.ToHours,
                Campaign.Current.TimeControlMode,
                Campaign.Current.SpeedUpMultiplier,
                false);

            InvokeClientSendPacket(
                NetworkPacketType.PlayerSnapshot,
                payload);
        }

        private static void BroadcastAuthoritativeState()
        {
            MultiplayerCampaignHost host = MultiplayerCampaignSubModule.GetHost();
            if (host == null)
                return;

            MobileParty party = MobileParty.MainParty;
            if (party == null)
                return;

            byte[] payload = BuildPlayerPayload(
                "HOST",
                LocalPlayerState.GetDisplayName(),
                party.Position,
                TryGetTargetPosition(party, party.Position),
                GetPartySize(party),
                TryGetBoolProperty(party, "IsMoving"),
                TryGetFloatProperty(party, "MovementSpeed"),
                ++_localSequence,
                CampaignTime.Now.ToHours,
                Campaign.Current.TimeControlMode,
                Campaign.Current.SpeedUpMultiplier,
                true);

            foreach (HostConnection connection in GetHostConnections(host))
            {
                InvokeHostSendDirect(
                    host,
                    connection,
                    NetworkPacketType.PlayerSnapshot,
                    payload);
            }

            BroadcastWorldPartyState(host);
        }

        private static void BroadcastWorldPartyState(MultiplayerCampaignHost host)
        {
            if (MobileParty.AllLordParties == null)
                return;

            long revision = ++_lastWorldRevision;

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(ProtocolMagic);
                writer.Write(ProtocolVersion);
                writer.Write(WorldPartyStateType);
                writer.Write(revision);

                int count = 0;
                for (int i = 0; i < MobileParty.AllLordParties.Count; i++)
                {
                    MobileParty p = MobileParty.AllLordParties[i];
                    if (IsSerializableWorldParty(p))
                        count++;
                }

                writer.Write(count);

                for (int i = 0; i < MobileParty.AllLordParties.Count; i++)
                {
                    MobileParty p = MobileParty.AllLordParties[i];
                    if (!IsSerializableWorldParty(p))
                        continue;

                    CampaignVec2 position = p.Position;
                    CampaignVec2 target = TryGetTargetPosition(p, position);

                    writer.Write(p.StringId);
                    writer.Write(position.X);
                    writer.Write(position.Y);
                    writer.Write(target.X);
                    writer.Write(target.Y);
                    writer.Write(GetPartySize(p));
                    writer.Write(TryGetBoolProperty(p, "IsMoving"));
                    writer.Write(++_localSequence);
                }

                byte[] payload = stream.ToArray();
                foreach (HostConnection connection in GetHostConnections(host))
                {
                    InvokeHostSendDirect(
                        host,
                        connection,
                        NetworkPacketType.WorldPartySnapshot,
                        payload);
                }
            }
        }

        private static bool IsSerializableWorldParty(MobileParty party)
        {
            if (party == null || string.IsNullOrWhiteSpace(party.StringId))
                return false;

            if (party == MobileParty.MainParty || party == _clientParty)
                return false;

            if (RemotePlayerManager.IsRemoteParty(party))
                return false;

            if (!IsFinite(party.Position.X) || !IsFinite(party.Position.Y))
                return false;

            return true;
        }

        private static byte[] BuildPlayerPayload(
            string playerId,
            string name,
            CampaignVec2 position,
            CampaignVec2 target,
            int partySize,
            bool moving,
            float heading,
            ulong sequence,
            double hostHours,
            CampaignTimeControlMode mode,
            float speed,
            bool host)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(ProtocolMagic);
                writer.Write(ProtocolVersion);
                writer.Write(PlayerStateType);
                writer.Write(sequence);
                writer.Write(hostHours);
                writer.Write(speed);
                writer.Write((int)mode);
                writer.Write(host);
                writer.Write(playerId ?? string.Empty);
                writer.Write(name ?? "Player");
                writer.Write(position.X);
                writer.Write(position.Y);
                writer.Write(target.X);
                writer.Write(target.Y);
                writer.Write(partySize);
                writer.Write(moving);
                writer.Write(heading);
                return stream.ToArray();
            }
        }

        private static void DrainNetworkQueue()
        {
            QueuedState state;
            while (IncomingStates.TryDequeue(out state))
            {
                try
                {
                    if (state == null || state.Payload == null)
                        continue;

                    if (state.Kind == PlayerStateType)
                        ReadPlayerPayload(state.Payload);
                    else if (state.Kind == WorldPartyStateType)
                        ReadWorldPartyPayload(state.Payload);
                }
                catch
                {
                }
            }
        }

        public static bool TryQueueCustomPacket(byte[] payload)
        {
            if (payload == null || payload.Length < 6)
                return false;

            try
            {
                using (MemoryStream stream = new MemoryStream(payload))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    int magic = reader.ReadInt32();
                    byte version = reader.ReadByte();
                    byte kind = reader.ReadByte();
                    if (magic != ProtocolMagic || version != ProtocolVersion)
                        return false;

                    if (kind != PlayerStateType && kind != WorldPartyStateType)
                        return false;

                    IncomingStates.Enqueue(new QueuedState
                    {
                        Kind = kind,
                        Payload = (byte[])payload.Clone(),
                        ReceivedUtc = DateTime.UtcNow
                    });
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ReadPlayerPayload(byte[] payload)
        {
            using (MemoryStream stream = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadInt32() != ProtocolMagic ||
                    reader.ReadByte() != ProtocolVersion ||
                    reader.ReadByte() != PlayerStateType)
                    return;

                ulong sequence = reader.ReadUInt64();
                double hostHours = reader.ReadDouble();
                float speed = reader.ReadSingle();
                int mode = reader.ReadInt32();
                bool host = reader.ReadBoolean();
                string playerId = reader.ReadString();
                string name = reader.ReadString();
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                float tx = reader.ReadSingle();
                float ty = reader.ReadSingle();
                int size = reader.ReadInt32();
                bool moving = reader.ReadBoolean();
                float heading = reader.ReadSingle();

                if (!IsValidNetworkIdentity(playerId) ||
                    !IsFinite(x) || !IsFinite(y) ||
                    !IsFinite(tx) || !IsFinite(ty) ||
                    !IsFinite(speed))
                    return;

                PlayerSyncState current;
                lock (Sync)
                {
                    if (RemotePlayers.TryGetValue(playerId, out current) &&
                        sequence <= current.Sequence)
                        return;

                    current = new PlayerSyncState
                    {
                        Id = playerId,
                        Name = string.IsNullOrWhiteSpace(name) ? "Player" : SanitizeName(name),
                        Position = new CampaignVec2(new Vec2(x, y), true),
                        Target = new CampaignVec2(new Vec2(tx, ty), true),
                        PartySize = Math.Max(1, Math.Min(10000, size)),
                        Moving = moving,
                        Heading = heading,
                        Sequence = sequence,
                        ReceivedUtc = DateTime.UtcNow,
                        IsHost = host
                    };
                    RemotePlayers[playerId] = current;
                }

                if (host)
                {
                    _targetHostHours = hostHours;
                    _hostSpeed = Math.Max(0f, Math.Min(100f, speed));
                    _hostMode = mode;
                    _haveHostTime = true;
                    _lastHostPacketUtc = DateTime.UtcNow;
                }
            }
        }

        private static void ReadWorldPartyPayload(byte[] payload)
        {
            using (MemoryStream stream = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadInt32() != ProtocolMagic ||
                    reader.ReadByte() != ProtocolVersion ||
                    reader.ReadByte() != WorldPartyStateType)
                    return;

                long revision = reader.ReadInt64();
                if (revision < _lastWorldRevision)
                    return;

                _lastWorldRevision = revision;
                int count = reader.ReadInt32();
                if (count < 0 || count > 100000)
                    return;

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

                    lock (Sync)
                    {
                        WorldPartySyncState current;
                        if (WorldParties.TryGetValue(id, out current) &&
                            sequence <= current.Sequence)
                            continue;

                        WorldParties[id] = new WorldPartySyncState
                        {
                            Id = id,
                            Position = new CampaignVec2(new Vec2(x, y), true),
                            Target = new CampaignVec2(new Vec2(tx, ty), true),
                            Size = Math.Max(0, Math.Min(10000, size)),
                            Moving = moving,
                            Sequence = sequence,
                            ReceivedUtc = DateTime.UtcNow
                        };
                    }
                }
            }
        }

        private static void ApplyHostTime()
        {
            if (!IsClientSession() || !_haveHostTime || Campaign.Current == null)
                return;

            try
            {
                double local = CampaignTime.Now.ToHours;
                double difference = _targetHostHours - local;

                // Exact time mutation is private in Bannerlord. For stability we correct
                // the clock by choosing the authoritative mode and multiplier; when drift
                // becomes large, a reflective MapTimeTracker correction is attempted.
                if (_hostMode >= 0 && _hostMode <= 16)
                {
                    Campaign.Current.TimeControlMode =
                        (CampaignTimeControlMode)_hostMode;
                    Campaign.Current.SpeedUpMultiplier = _hostSpeed;
                }

                if (Math.Abs(difference) > 0.5d)
                    TrySetCampaignTimeReflectively(_targetHostHours);
            }
            catch
            {
            }
        }

        private static bool TrySetCampaignTimeReflectively(double hours)
        {
            try
            {
                Campaign campaign = Campaign.Current;
                if (campaign == null)
                    return false;

                Type campaignType = campaign.GetType();
                MethodInfo[] methods = campaignType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                CampaignTime target = CampaignTime.Hours((float)hours);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!string.Equals(method.Name, "SetTime", StringComparison.OrdinalIgnoreCase))
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 ||
                        parameters[0].ParameterType != typeof(CampaignTime))
                        continue;

                    method.Invoke(campaign, new object[] { target });
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ApplyRemotePlayers(float dt)
        {
            PlayerSyncState[] snapshot;
            lock (Sync)
            {
                snapshot = new PlayerSyncState[RemotePlayers.Count];
                RemotePlayers.Values.CopyTo(snapshot, 0);
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                PlayerSyncState state = snapshot[i];
                if (state == null || string.IsNullOrWhiteSpace(state.Id))
                    continue;

                if (state.Id == PlayerIdentity.GetLocalId())
                    continue;

                if (state.Party == null || state.Hero == null)
                {
                    CreateRemoteObjects(state);
                }

                if (state.Party == null)
                    continue;

                try
                {
                    CampaignVec2 current = state.Party.Position;
                    float alpha = Math.Max(0.05f, Math.Min(1f, dt * 10f));
                    float x = current.X + (state.Target.X - current.X) * alpha;
                    float y = current.Y + (state.Target.Y - current.Y) * alpha;
                    state.Party.Position = new CampaignVec2(new Vec2(x, y), true);
                }
                catch
                {
                    state.Party = null;
                }

                try
                {
                    UpdateRemoteRoster(state);
                }
                catch
                {
                }
            }
        }

        private static void CreateRemoteObjects(PlayerSyncState state)
        {
            if (Campaign.Current == null || Hero.MainHero == null ||
                Hero.MainHero.CharacterObject == null)
                return;

            if (state.Hero != null && state.Party != null)
                return;

            try
            {
                string id = "mpc_remote_" + SanitizeId(state.Id);
                Hero hero = Hero.Find(id);

                if (hero == null)
                {
                    Hero created;
                    if (!HeroCreator.CreateBasicHero(
                            id,
                            Hero.MainHero.CharacterObject,
                            out created,
                            true))
                    {
                        hero = Hero.Find(id);
                    }
                    else
                    {
                        hero = created;
                    }
                }

                if (hero == null || hero == Hero.MainHero)
                    return;

                hero.SetName(
                    new TextObject(state.Name),
                    new TextObject(state.Name));

                MobileParty party = MobilePartyHelper.SpawnLordParty(
                    hero,
                    state.Position,
                    0f);

                if (party == null || party == MobileParty.MainParty)
                    return;

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

        private static void UpdateRemoteRoster(PlayerSyncState state)
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
                WorldPartySyncState sync = snapshot[i];
                if (sync == null || string.IsNullOrWhiteSpace(sync.Id))
                    continue;

                MobileParty party = FindParty(sync.Id);
                if (party == null || party == MobileParty.MainParty ||
                    party == _clientParty || RemotePlayerManager.IsRemoteParty(party))
                    continue;

                try
                {
                    CampaignVec2 current = party.Position;
                    float alpha = Math.Max(0.05f, Math.Min(1f, dt * 7f));
                    float x = current.X + (sync.Position.X - current.X) * alpha;
                    float y = current.Y + (sync.Position.Y - current.Y) * alpha;
                    party.Position = new CampaignVec2(new Vec2(x, y), true);
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

        private static void CleanupStaleStates()
        {
            DateTime now = DateTime.UtcNow;
            List<string> removePlayers = new List<string>();
            List<string> removeParties = new List<string>();

            lock (Sync)
            {
                foreach (KeyValuePair<string, PlayerSyncState> item in RemotePlayers)
                {
                    if (now - item.Value.ReceivedUtc > TimeSpan.FromSeconds(10))
                        removePlayers.Add(item.Key);
                }

                foreach (KeyValuePair<string, WorldPartySyncState> item in WorldParties)
                {
                    if (now - item.Value.ReceivedUtc > TimeSpan.FromSeconds(15))
                        removeParties.Add(item.Key);
                }

                for (int i = 0; i < removePlayers.Count; i++)
                {
                    PlayerSyncState state;
                    if (RemotePlayers.TryGetValue(removePlayers[i], out state))
                        DestroyRemote(state);
                    RemotePlayers.Remove(removePlayers[i]);
                }

                for (int i = 0; i < removeParties.Count; i++)
                    WorldParties.Remove(removeParties[i]);
            }
        }

        public static void RemovePlayer(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                return;

            lock (Sync)
            {
                PlayerSyncState state;
                if (RemotePlayers.TryGetValue(playerId, out state))
                    DestroyRemote(state);
                RemotePlayers.Remove(playerId);
            }
        }

        private static void DestroyRemote(PlayerSyncState state)
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

        public static void EnqueueIncomingPlayerPayload(byte[] payload)
        {
            TryQueueCustomPacket(payload);
        }

        public static void EnqueueIncomingWorldPayload(byte[] payload)
        {
            TryQueueCustomPacket(payload);
        }

        private static void InvokeClientSendPacket(NetworkPacketType type, byte[] payload)
        {
            try
            {
                MultiplayerNetworkClient client = MultiplayerNetworkClient.Instance;
                MethodInfo method = AccessTools.Method(
                    typeof(MultiplayerNetworkClient),
                    "SendPacket",
                    new[] { typeof(NetworkPacketType), typeof(byte[]) });
                if (method != null)
                    method.Invoke(client, new object[] { type, payload });
            }
            catch
            {
            }
        }

        private static void InvokeHostSendDirect(
            MultiplayerCampaignHost host,
            HostConnection connection,
            NetworkPacketType type,
            byte[] payload)
        {
            try
            {
                MethodInfo method = AccessTools.Method(
                    typeof(MultiplayerCampaignHost),
                    "SendDirect",
                    new[] { typeof(HostConnection), typeof(NetworkPacketType), typeof(byte[]) });
                if (method != null)
                    method.Invoke(host, new object[] { connection, type, payload });
            }
            catch
            {
            }
        }

        private static IEnumerable<HostConnection> GetHostConnections(MultiplayerCampaignHost host)
        {
            List<HostConnection> result = new List<HostConnection>();
            try
            {
                FieldInfo field = AccessTools.Field(
                    typeof(MultiplayerCampaignHost),
                    "_connections");
                object dictionary = field == null ? null : field.GetValue(host);
                if (dictionary == null)
                    return result;

                PropertyInfo valuesProperty = dictionary.GetType().GetProperty("Values");
                object values = valuesProperty == null ? null : valuesProperty.GetValue(dictionary, null);
                IEnumerable enumerable = values as IEnumerable;
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

        private static CampaignVec2 TryGetTargetPosition(MobileParty party, CampaignVec2 fallback)
        {
            try
            {
                if (party == null)
                    return fallback;

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

        private static object ReadMember(object obj, string name)
        {
            if (obj == null)
                return null;

            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(obj, null);

            FieldInfo field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(obj);
        }

        private static bool TryGetBoolProperty(object obj, string name)
        {
            try
            {
                object value = ReadMember(obj, name);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private static float TryGetFloatProperty(object obj, string name)
        {
            try
            {
                object value = ReadMember(obj, name);
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

        private static bool IsValidNetworkIdentity(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string SanitizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "player";

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    builder.Append(c);
            }

            return builder.Length == 0 ? "player" : builder.ToString();
        }
    }

    internal static class CharacterSlotStore
    {
        private const int SlotCount = 3;
        private static readonly object Sync = new object();
        private static readonly string[] Names = new string[SlotCount];
        private static readonly string[] Ids = new string[SlotCount];
        private static readonly string[] PartyIds = new string[SlotCount];
        private static bool _loaded;

        private static string DirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MultiplayerCampaign");

        private static string FilePath => Path.Combine(DirectoryPath, "character_slots.txt");

        public static void Load()
        {
            lock (Sync)
            {
                if (_loaded)
                    return;

                _loaded = true;
                for (int i = 0; i < SlotCount; i++)
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
                    for (int i = 0; i < lines.Length && i < SlotCount; i++)
                    {
                        string line = lines[i] ?? string.Empty;
                        string[] parts = line.Split(new[] { '|' }, 4);
                        if (parts.Length >= 2)
                        {
                            Names[i] = Sanitize(parts[0]);
                            Ids[i] = Sanitize(parts[1]);
                        }
                        if (parts.Length >= 3)
                            PartyIds[i] = Sanitize(parts[2]);
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
            {
                return slot >= 0 && slot < SlotCount &&
                       !string.IsNullOrWhiteSpace(Names[slot]) &&
                       !string.IsNullOrWhiteSpace(Ids[slot]);
            }
        }

        public static string GetName(int slot)
        {
            Load();
            lock (Sync)
                return slot >= 0 && slot < SlotCount ? Names[slot] : string.Empty;
        }

        public static string GetId(int slot)
        {
            Load();
            lock (Sync)
                return slot >= 0 && slot < SlotCount ? Ids[slot] : string.Empty;
        }

        public static void Save(int slot, string name)
        {
            Load();
            if (slot < 0 || slot >= SlotCount)
                return;

            string clean = Sanitize(name);
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

        public static void SaveRuntimePartyId(int slot, string partyId)
        {
            Load();
            if (slot < 0 || slot >= SlotCount)
                return;

            lock (Sync)
            {
                PartyIds[slot] = Sanitize(partyId);
                PersistLocked();
            }
        }

        private static void PersistLocked()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string[] lines = new string[SlotCount];
                for (int i = 0; i < SlotCount; i++)
                    lines[i] = (Names[i] ?? string.Empty) + "|" +
                               (Ids[i] ?? string.Empty) + "|" +
                               (PartyIds[i] ?? string.Empty);
                File.WriteAllLines(FilePath, lines);
            }
            catch
            {
            }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Trim()
                .Replace("|", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty);
        }
    }

    /* ============================================================
     * HARMONY HOOKS
     * ============================================================ */

    [HarmonyPatch]
    internal static class MpcRuntimeLoadPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MultiplayerCampaignSubModule),
                "OnSubModuleLoad");
        }

        private static void Postfix()
        {
            MpcRebuildRuntime.Install();
        }
    }

    [HarmonyPatch]
    internal static class MpcRuntimeJoinPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MultiplayerCampaignVM),
                "ExecuteJoinHost");
        }

        private static bool Prefix(MultiplayerCampaignVM __instance)
        {
            if (MpcRebuildRuntime.ConsumeJoinBypass())
                return true;

            if (!MpcRebuildRuntime.HasSelectedCharacter())
            {
                MpcRebuildRuntime.ShowCharacterSelection(__instance);
                return false;
            }

            LocalPlayerState.SetDisplayName(
                MpcRebuildRuntime.SelectedName);
            return true;
        }
    }

    [HarmonyPatch]
    internal static class MpcCampaignStartPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MultiplayerCampaignSubModule),
                "OnGameStart");
        }

        private static void Postfix(Game game, IGameStarter gameStarter)
        {
            try
            {
                CampaignGameStarter starter = gameStarter as CampaignGameStarter;
                if (starter != null && game != null && game.GameType is Campaign)
                    starter.AddBehavior(new MpcRebuildCampaignBehavior());
            }
            catch
            {
            }
        }
    }

    internal sealed class MpcRebuildCampaignBehavior : CampaignBehaviorBase
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

                float tick = _timer;
                _timer = 0f;
                MpcRebuildRuntime.Tick(tick);
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
    internal static class MpcClientProcessMessagePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MultiplayerNetworkClient),
                "ProcessMessage");
        }

        private static bool Prefix(NetworkMessage message)
        {
            try
            {
                if (message == null || message.Payload == null)
                    return true;

                if (message.Type == NetworkPacketType.PlayerSnapshot &&
                    MpcRebuildRuntime.TryQueueCustomPacket(message.Payload))
                    return false;

                if (message.Type == NetworkPacketType.WorldPartySnapshot &&
                    MpcRebuildRuntime.TryQueueCustomPacket(message.Payload))
                    return false;
            }
            catch
            {
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class MpcHostPlayerSnapshotPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MultiplayerCampaignHost),
                "ApplyPlayerSnapshot");
        }

        private static bool Prefix(
            HostConnection connection,
            byte[] payload)
        {
            try
            {
                if (payload != null && MpcRebuildRuntime.TryQueueCustomPacket(payload))
                {
                    // Host network thread never mutates Campaign objects.
                    // The custom state is queued by the extension behavior.
                    return false;
                }
            }
            catch
            {
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class MpcHostWorldSnapshotPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MultiplayerCampaignHost),
                "HandleWorldJoinTest");
        }

        private static void Postfix()
        {
            // The regular Join ACK remains the compatibility path.
        }
    }

    [HarmonyPatch]
    internal static class MpcRemoteLeavePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(RemotePlayerManager),
                "QueueLeave");
        }

        private static void Prefix(string playerId)
        {
            MpcRebuildRuntime.RemovePlayer(playerId);
        }
    }

    [HarmonyPatch]
    internal static class MpcGameEndPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MultiplayerCampaignSubModule),
                "OnGameEnd");
        }

        private static void Prefix()
        {
            MpcRebuildRuntime.Reset();
        }
    }

    /*
     * MapInfoVM is intentionally fail-safe in multiplayer.
     * When its selected player object is incomplete, suppressing this
     * presentation update is preferable to allowing a campaign-ending
     * NullReferenceException. Other gameplay systems remain active.
     */
    [HarmonyPatch]
    internal static class MpcMapInfoSafetyPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("SandBox.View.Map.MapInfoVM");
            if (type == null)
                return null;

            return AccessTools.Method(type, "UpdatePlayerInfo");
        }

        private static bool Prefix()
        {
            if (Campaign.Current == null)
                return false;

            if (MultiplayerNetworkClient.Instance.IsConnected ||
                MultiplayerCampaignSubModule.IsHostRequested())
                return false;

            return true;
        }
    }
}