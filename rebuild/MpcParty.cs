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


namespace MultiplayerCampaignRebuildLayer
{

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

}

