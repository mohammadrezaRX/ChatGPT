using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MultiplayerCampaign
{
    internal static class MpcNativeCharacterCreationFix
    {
        public static void OpenNativeCharacterCreation()
        {
            if (Game.Current == null || Game.Current.GameStateManager == null)
                throw new InvalidOperationException("Bannerlord GameStateManager is not available.");

            CharacterCreationState state =
                Game.Current.GameStateManager.CreateState<CharacterCreationState>();

            if (state == null)
                throw new InvalidOperationException("Bannerlord returned a null CharacterCreationState.");

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
                var type = __instance.GetType();
                type.GetProperty("ShowMain")?.SetValue(__instance, false, null);
                type.GetProperty("ShowCharacter")?.SetValue(__instance, false, null);
                type.GetProperty("ShowCreate")?.SetValue(__instance, true, null);
                type.GetProperty("ShowJoin")?.SetValue(__instance, false, null);
                __instance.SetStatus("CREATE HOST");
            }
            catch (Exception ex)
            {
                try { HostConsole.WriteLine("[!] Host page: " + ex.Message); } catch { }
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
                try { HostConsole.WriteLine("[!] Character Creator: " + ex); } catch { }
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
                if (__instance == null ||
                    __instance.CharacterCreationManager == null ||
                    __instance.CharacterCreationManager.CharacterCreationContent == null)
                    return;

                if (MpcCharacterSlots.SelectedSlot < 0)
                    MpcCharacterSlots.Select(0);

                string name =
                    __instance.CharacterCreationManager.CharacterCreationContent.MainCharacterName;

                if (string.IsNullOrWhiteSpace(name))
                    return;

                MpcCharacterSlots.SaveSelected(name);
                LocalPlayerState.SetDisplayName(name);
            }
            catch (Exception ex)
            {
                try { HostConsole.WriteLine("[!] Character save: " + ex.Message); } catch { }
            }
        }
    }
}
