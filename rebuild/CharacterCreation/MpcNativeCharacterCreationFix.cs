using System;
using System.Reflection;
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
            GameStateManager manager = Game.Current.GameStateManager;
            Type contentType = FindType("TaleWorlds.CampaignSystem.CharacterCreationContent.SandboxCharacterCreationContent");
            if (contentType == null)
                throw new InvalidOperationException("SandboxCharacterCreationContent type was not found in loaded Bannerlord assemblies.");

            object content = Activator.CreateInstance(contentType);
            MethodInfo createState = FindCreateStateWithParameters();
            if (createState == null)
                throw new MissingMethodException("GameStateManager.CreateState<T>(params object[]) was not found.");

            MethodInfo closedCreateState = createState.MakeGenericMethod(typeof(CharacterCreationState));
            CharacterCreationState state = (CharacterCreationState)closedCreateState.Invoke(
                manager,
                new object[] { new object[] { content } });

            manager.CleanAndPushState(state, 0);
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type type = assemblies[i].GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch { }
            }

            return null;
        }

        private static MethodInfo FindCreateStateWithParameters()
        {
            MethodInfo[] methods = typeof(GameStateManager).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!method.IsGenericMethodDefinition || method.Name != "CreateState")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(object[]))
                    return method;
            }

            return null;
        }
    }

    [HarmonyPatch(typeof(MultiplayerCampaignVM), "ExecuteOpenCreate")]
    internal static class MpcCreateCharacterButtonPatch
    {
        private static bool Prefix(MultiplayerCampaignVM __instance)
        {
            try
            {
                if (MpcCharacterSlots.SelectedSlot < 0)
                    MpcCharacterSlots.Select(0);

                var type = __instance.GetType();
                type.GetProperty("ShowMain")?.SetValue(__instance, false, null);
                type.GetProperty("ShowCharacter")?.SetValue(__instance, true, null);
                type.GetProperty("ShowCreate")?.SetValue(__instance, false, null);
                type.GetProperty("ShowJoin")?.SetValue(__instance, false, null);
                __instance.SetStatus("SELECT A SLOT, THEN OPEN BANNERLORD CHARACTER CREATOR");
            }
            catch { }

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
                if (__instance == null ||
                    __instance.CharacterCreationManager == null ||
                    __instance.CharacterCreationManager.CharacterCreationContent == null)
                    return;

                if (MpcCharacterSlots.SelectedSlot < 0)
                    MpcCharacterSlots.Select(0);

                string name = __instance.CharacterCreationManager.CharacterCreationContent.MainCharacterName;
                if (string.IsNullOrWhiteSpace(name))
                    return;

                MpcCharacterSlots.SaveSelected(name);
                LocalPlayerState.SetDisplayName(name);
            }
            catch { }
        }
    }
}
