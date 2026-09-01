using System;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.MountAndBlade;

namespace MultiplayerCampaign
{
    internal static class MpcNativeCharacterCreationFix
    {
        public static void OpenNativeCharacterCreation()
        {
            CharacterCreationState state =
                Game.Current.GameStateManager.CreateState<CharacterCreationState>();

            Game.Current.GameStateManager.CleanAndPushState(state, 0);
        }
    }

    [HarmonyPatch(typeof(MultiplayerCampaignVM), "ExecuteOpenCreate")]
    internal static class MpcCreateHostButtonPatch
    {
        private static bool Prefix(MultiplayerCampaignVM __instance)
        {
            try
            {
                __instance.SetStatus("CREATE HOST");

                var type = __instance.GetType();
                var showMain = type.GetProperty("ShowMain");
                var showCharacter = type.GetProperty("ShowCharacter");
                var showCreate = type.GetProperty("ShowCreate");
                var showJoin = type.GetProperty("ShowJoin");

                showMain?.SetValue(__instance, false, null);
                showCharacter?.SetValue(__instance, false, null);
                showCreate?.SetValue(__instance, true, null);
                showJoin?.SetValue(__instance, false, null);
            }
            catch
            {
                try { __instance.ExecuteStartHost(); } catch { }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(MultiplayerCampaignVM), "ExecuteCreateCharacter")]
    internal static class MpcNativeCreateCharacterButtonPatch
    {
        private static bool Prefix(MultiplayerCampaignVM __instance)
        {
            try
            {
                if (MpcCharacterSlots.SelectedSlot < 0)
                    MpcCharacterSlots.Select(0);

                __instance.SetStatus("OPENING BANNERLORD CHARACTER CREATION...");
                MpcNativeCharacterCreationFix.OpenNativeCharacterCreation();
            }
            catch (Exception ex)
            {
                try { __instance.SetStatus("CHARACTER CREATION FAILED: " + ex.Message); } catch { }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterCreationState), "FinalizeCharacterCreationState")]
    internal static class MpcNativeCharacterCreationSavePatch
    {
        private static void Postfix(CharacterCreationState __instance)
        {
            try
            {
                int slot = MpcCharacterSlots.SelectedSlot;
                if (slot < 0)
                {
                    MpcCharacterSlots.Select(0);
                    slot = 0;
                }

                if (__instance == null ||
                    __instance.CharacterCreationManager == null ||
                    __instance.CharacterCreationManager.CharacterCreationContent == null)
                    return;

                string name = __instance.CharacterCreationManager.CharacterCreationContent.MainCharacterName;
                if (string.IsNullOrWhiteSpace(name))
                    return;

                MpcCharacterSlots.SaveSelected(name);
                LocalPlayerState.SetDisplayName(name);
            }
            catch
            {
            }
        }
    }
}
