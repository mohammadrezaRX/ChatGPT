using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MultiplayerCampaign
{
    internal static class MpcNativeCharacterCreationFix
    {
        private static readonly object Sync = new object();
        private static bool _pending;
        private static bool _opening;
        private static DateTime _requestUtc;

        public static void RequestNativeCharacterCreation()
        {
            lock (Sync)
            {
                _pending = true;
                _opening = false;
                _requestUtc = DateTime.UtcNow;
            }
        }

        public static void ProcessPending()
        {
            lock (Sync)
            {
                if (!_pending || _opening)
                    return;

                if ((DateTime.UtcNow - _requestUtc).TotalSeconds > 15.0)
                {
                    _pending = false;
                    return;
                }
            }

            Game game = Game.Current;
            if (game == null)
                return;

            GameStateManager manager = game.GameStateManager;
            if (manager == null)
                return;

            lock (Sync)
            {
                if (!_pending || _opening)
                    return;

                _opening = true;
                _pending = false;
            }

            try
            {
                CharacterCreationState state =
                    manager.CreateState<CharacterCreationState>();

                if (state == null)
                    throw new InvalidOperationException("Bannerlord CharacterCreationState could not be created.");

                manager.CleanAndPushState(state, 0);
                HostConsole.WriteLine("[*] Native Bannerlord Character Creation opened.");
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    _opening = false;
                }

                try
                {
                    HostConsole.WriteLine("[!] Native Character Creation failed: " + ex);
                }
                catch
                {
                }
            }
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
                MpcNativeCharacterCreationFix.RequestNativeCharacterCreation();
                return false;
            }
            catch (Exception ex)
            {
                try { __instance.SetStatus("CHARACTER CREATION FAILED"); } catch { }
                try { HostConsole.WriteLine("[!] Character Creator request: " + ex); } catch { }
                return false;
            }
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
                HostConsole.WriteLine("[*] Character saved to MPC slot: " + name);
            }
            catch (Exception ex)
            {
                try { HostConsole.WriteLine("[!] Character save: " + ex.Message); } catch { }
            }
        }
    }
}
