using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MultiplayerCampaign
{
    /// <summary>
    /// Non-destructive extension layer for the existing MultiplayerCampaign core.
    /// This file deliberately does not replace or duplicate the existing Host,
    /// Client, WorldTransfer, or RemotePlayer implementations.
    /// </summary>
    internal static class MultiplayerCampaignEnhancements
    {
        private const string HarmonyId = "MultiplayerCampaign.Enhancements";
        private static Harmony _harmony;
        private static bool _installed;
        private static readonly object Sync = new object();

        public static void Install()
        {
            lock (Sync)
            {
                if (_installed)
                {
                    return;
                }

                try
                {
                    _harmony = new Harmony(HarmonyId);
                    _harmony.PatchAll(typeof(MultiplayerCampaignEnhancements).Assembly);
                    _installed = true;
                }
                catch (Exception ex)
                {
                    HostConsole.WriteLine("[!] Enhancement install failed: " + ex.Message);
                }
            }
        }

        public static void Uninstall()
        {
            lock (Sync)
            {
                if (!_installed || _harmony == null)
                {
                    return;
                }

                try
                {
                    _harmony.UnpatchAll(HarmonyId);
                }
                catch
                {
                }

                _harmony = null;
                _installed = false;
            }
        }
    }

    // ------------------------------------------------------------
    // Persistent three-slot character profile store
    // ------------------------------------------------------------

    internal sealed class MultiplayerCharacterSlot
    {
        public int Index;
        public string Name;
        public bool Created;

        public MultiplayerCharacterSlot(int index)
        {
            Index = index;
            Name = string.Empty;
            Created = false;
        }
    }

    internal static class MultiplayerCharacterSlots
    {
        private const int SlotCount = 3;
        private static readonly object Sync = new object();
        private static readonly MultiplayerCharacterSlot[] Slots =
        {
            new MultiplayerCharacterSlot(0),
            new MultiplayerCharacterSlot(1),
            new MultiplayerCharacterSlot(2)
        };

        private static int _selectedSlot = -1;
        private static bool _loaded;

        public static int SelectedSlot
        {
            get
            {
                lock (Sync)
                {
                    return _selectedSlot;
                }
            }
        }

        public static void Load()
        {
            lock (Sync)
            {
                if (_loaded)
                {
                    return;
                }

                _loaded = true;

                try
                {
                    string directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MultiplayerCampaign");

                    string file = Path.Combine(directory, "characters.dat");

                    if (!File.Exists(file))
                    {
                        return;
                    }

                    string[] lines = File.ReadAllLines(file);

                    for (int i = 0; i < lines.Length && i < SlotCount; i++)
                    {
                        string line = lines[i] ?? string.Empty;
                        int separator = line.IndexOf('|');

                        if (separator < 0)
                        {
                            continue;
                        }

                        Slots[i].Created = line.Substring(0, separator) == "1";
                        Slots[i].Name = SanitizeName(line.Substring(separator + 1));
                    }
                }
                catch
                {
                    for (int i = 0; i < SlotCount; i++)
                    {
                        Slots[i].Created = false;
                        Slots[i].Name = string.Empty;
                    }
                }
            }
        }

        public static bool Select(int slot)
        {
            Load();

            if (slot < 0 || slot >= SlotCount)
            {
                return false;
            }

            lock (Sync)
            {
                _selectedSlot = slot;
                return true;
            }
        }

        public static MultiplayerCharacterSlot Get(int slot)
        {
            Load();

            if (slot < 0 || slot >= SlotCount)
            {
                return null;
            }

            lock (Sync)
            {
                return new MultiplayerCharacterSlot(slot)
                {
                    Name = Slots[slot].Name,
                    Created = Slots[slot].Created
                };
            }
        }

        public static bool Save(int slot, string name)
        {
            Load();

            if (slot < 0 || slot >= SlotCount)
            {
                return false;
            }

            string clean = SanitizeName(name);

            if (string.IsNullOrWhiteSpace(clean))
            {
                return false;
            }

            lock (Sync)
            {
                Slots[slot].Name = clean;
                Slots[slot].Created = true;
                _selectedSlot = slot;
                PersistLocked();
                return true;
            }
        }

        public static bool HasSelectedCharacter()
        {
            Load();

            lock (Sync)
            {
                return _selectedSlot >= 0 &&
                       _selectedSlot < SlotCount &&
                       Slots[_selectedSlot].Created &&
                       !string.IsNullOrWhiteSpace(Slots[_selectedSlot].Name);
            }
        }

        public static string GetSelectedName()
        {
            Load();

            lock (Sync)
            {
                if (_selectedSlot < 0 || _selectedSlot >= SlotCount)
                {
                    return "";
                }

                return Slots[_selectedSlot].Name ?? string.Empty;
            }
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string clean = value.Trim();

            if (clean.Length > 32)
            {
                clean = clean.Substring(0, 32);
            }

            return clean.Replace("|", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        private static void PersistLocked()
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MultiplayerCampaign");

                Directory.CreateDirectory(directory);

                string file = Path.Combine(directory, "characters.dat");
                string[] lines = new string[SlotCount];

                for (int i = 0; i < SlotCount; i++)
                {
                    lines[i] = (Slots[i].Created ? "1" : "0") + "|" + (Slots[i].Name ?? string.Empty);
                }

                File.WriteAllLines(file, lines);
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------
    // Remote player names and last received state
    // ------------------------------------------------------------

    internal sealed class EnhancedRemoteSnapshot
    {
        public string PlayerId;
        public string Name;
        public CampaignVec2 Position;
        public int PartySize;
        public DateTime ReceivedUtc;
    }

    internal static class EnhancedRemoteState
    {
        private static readonly ConcurrentDictionary<string, EnhancedRemoteSnapshot> States =
            new ConcurrentDictionary<string, EnhancedRemoteSnapshot>();

        public static void Remember(string playerId, string name, CampaignVec2 position, int partySize)
        {
            if (string.IsNullOrWhiteSpace(playerId))
            {
                return;
            }

            if (!IsFinite(position.X) || !IsFinite(position.Y))
            {
                return;
            }

            States[playerId] = new EnhancedRemoteSnapshot
            {
                PlayerId = playerId,
                Name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim(),
                Position = position,
                PartySize = Math.Max(1, Math.Min(10000, partySize)),
                ReceivedUtc = DateTime.UtcNow
            };
        }

        public static bool TryGet(string playerId, out EnhancedRemoteSnapshot snapshot)
        {
            return States.TryGetValue(playerId, out snapshot);
        }

        public static void Remove(string playerId)
        {
            if (!string.IsNullOrWhiteSpace(playerId))
            {
                EnhancedRemoteSnapshot ignored;
                States.TryRemove(playerId, out ignored);
            }
        }

        public static void Clear()
        {
            States.Clear();
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    // ------------------------------------------------------------
    // Crash guards around the existing Campaign tick and client UI
    // ------------------------------------------------------------

    [HarmonyPatch(typeof(MultiplayerCampaignSubModule), "OnSubModuleLoad")]
    internal static class EnhancementLoadPatch
    {
        private static void Postfix()
        {
            MultiplayerCharacterSlots.Load();
            MultiplayerCampaignEnhancements.Install();
        }
    }

    [HarmonyPatch(typeof(MultiplayerCampaignSubModule), "OnGameEnd")]
    internal static class EnhancementGameEndPatch
    {
        private static void Prefix()
        {
            EnhancedRemoteState.Clear();
        }
    }

    [HarmonyPatch(typeof(MultiplayerCampaignSubModule), "OnSubModuleUnloaded")]
    internal static class EnhancementUnloadPatch
    {
        private static void Prefix()
        {
            EnhancedRemoteState.Clear();
            MultiplayerCampaignEnhancements.Uninstall();
        }
    }

    [HarmonyPatch(typeof(MultiplayerCampaignBehavior), "OnCampaignTick")]
    internal static class EnhancementCampaignTickPatch
    {
        private static void Prefix(float dt)
        {
            if (Campaign.Current == null || dt < 0f || float.IsNaN(dt) || float.IsInfinity(dt))
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------
    // Capture real remote names instead of allowing the legacy
    // test name to become the permanent visible name.
    // ------------------------------------------------------------

    [HarmonyPatch(typeof(RemotePlayerManager), "ApplySnapshot")]
    internal static class EnhancedApplySnapshotPatch
    {
        private static void Prefix(
            string playerId,
            string name,
            CampaignVec2 position,
            int partySize)
        {
            EnhancedRemoteState.Remember(
                playerId,
                name,
                position,
                partySize);
        }
    }

    [HarmonyPatch(typeof(RemotePlayerManager), "QueueLeave")]
    internal static class EnhancedLeavePatch
    {
        private static void Prefix(string playerId)
        {
            EnhancedRemoteState.Remove(playerId);
        }
    }

    // ------------------------------------------------------------
    // Local join safety: a saved character slot must be selected
    // before a network join is allowed.
    // ------------------------------------------------------------

    [HarmonyPatch(typeof(MultiplayerCampaignVM), "ExecuteJoinHost")]
    internal static class EnhancedJoinCharacterPatch
    {
        private static bool Prefix(MultiplayerCampaignVM __instance)
        {
            MultiplayerCharacterSlots.Load();

            if (!MultiplayerCharacterSlots.HasSelectedCharacter())
            {
                __instance.SetStatus(
                    "SELECT A CHARACTER SLOT BEFORE JOINING");

                return false;
            }

            string name = MultiplayerCharacterSlots.GetSelectedName();

            if (string.IsNullOrWhiteSpace(name))
            {
                __instance.SetStatus(
                    "CHARACTER DATA IS INVALID");

                return false;
            }

            LocalPlayerState.SetDisplayName(name);
            return true;
        }
    }

    // ------------------------------------------------------------
    // Use the selected persistent character name whenever the
    // current VM is initialized.
    // ------------------------------------------------------------

    [HarmonyPatch(typeof(MultiplayerCampaignVM), MethodType.Constructor)]
    internal static class EnhancedVmConstructorPatch
    {
        private static void Postfix(MultiplayerCampaignVM __instance)
        {
            MultiplayerCharacterSlots.Load();

            string name = MultiplayerCharacterSlots.GetSelectedName();

            if (!string.IsNullOrWhiteSpace(name))
            {
                __instance.PlayerName = name;
            }
        }
    }

    // ------------------------------------------------------------
    // Remote party safety. Never allow remote parties to become
    // the local MainParty or to be used as a local movement target.
    // ------------------------------------------------------------

    [HarmonyPatch(typeof(RemotePlayerManager), "Update")]
    internal static class EnhancedRemotePartySafetyPatch
    {
        private static void Postfix()
        {
            try
            {
                if (Campaign.Current == null || MobileParty.MainParty == null)
                {
                    return;
                }

                Type managerType = typeof(RemotePlayerManager);
                FieldInfo playersField = managerType.GetField(
                    "Players",
                    BindingFlags.Static | BindingFlags.NonPublic);

                if (playersField == null)
                {
                    return;
                }

                object raw = playersField.GetValue(null);

                System.Collections.IDictionary dictionary =
                    raw as System.Collections.IDictionary;

                if (dictionary == null)
                {
                    return;
                }

                foreach (object value in dictionary.Values)
                {
                    if (value == null)
                    {
                        continue;
                    }

                    Type stateType = value.GetType();
                    FieldInfo partyField = stateType.GetField(
                        "Party",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    FieldInfo heroField = stateType.GetField(
                        "Hero",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    FieldInfo idField = stateType.GetField(
                        "PlayerId",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    MobileParty party = partyField == null ? null : partyField.GetValue(value) as MobileParty;
                    Hero hero = heroField == null ? null : heroField.GetValue(value) as Hero;
                    string id = idField == null ? null : idField.GetValue(value) as string;

                    if (party != null && party == MobileParty.MainParty)
                    {
                        partyField?.SetValue(value, null);
                        heroField?.SetValue(value, null);
                        continue;
                    }

                    if (hero != null && hero == Hero.MainHero)
                    {
                        heroField?.SetValue(value, null);
                    }

                    if (!string.IsNullOrWhiteSpace(id) &&
                        EnhancedRemoteState.TryGet(id, out EnhancedRemoteSnapshot snapshot) &&
                        hero != null)
                    {
                        try
                        {
                            hero.SetName(
                                new TextObject(snapshot.Name),
                                new TextObject(snapshot.Name));
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------
    // Public helper API for a future/updated UI layer.
    // ------------------------------------------------------------

    public static class MultiplayerCharacterApi
    {
        public static bool SelectSlot(int slot)
        {
            return MultiplayerCharacterSlots.Select(slot);
        }

        public static bool SaveCurrentCharacter(string name)
        {
            int slot = MultiplayerCharacterSlots.SelectedSlot;
            return slot >= 0 && MultiplayerCharacterSlots.Save(slot, name);
        }

        public static bool HasCharacter(int slot)
        {
            MultiplayerCharacterSlot data = MultiplayerCharacterSlots.Get(slot);
            return data != null && data.Created;
        }

        public static string GetCharacterName(int slot)
        {
            MultiplayerCharacterSlot data = MultiplayerCharacterSlots.Get(slot);
            return data == null ? string.Empty : data.Name;
        }

        public static string GetSelectedCharacterName()
        {
            return MultiplayerCharacterSlots.GetSelectedName();
        }

        public static int GetSelectedSlot()
        {
            return MultiplayerCharacterSlots.SelectedSlot;
        }
    }
}
