using System;
using System.Reflection;
using HarmonyLib;
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

            GameStateManager manager = Game.Current.GameStateManager;
            Type stateType = typeof(CharacterCreationState);
            object state = null;
            object content = CreateCharacterCreationContent();

            MethodInfo[] methods = typeof(GameStateManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length && state == null; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "CreateState" || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                MethodInfo closed;
                try { closed = method.MakeGenericMethod(stateType); }
                catch { continue; }

                try
                {
                    if (parameters.Length == 0)
                        state = closed.Invoke(manager, null);
                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(object[]))
                        state = closed.Invoke(manager, new object[] { content == null ? new object[0] : new object[] { content } });
                }
                catch { }
            }

            if (state == null)
            {
                ConstructorInfo[] constructors = stateType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < constructors.Length && state == null; i++)
                {
                    ParameterInfo[] parameters = constructors[i].GetParameters();
                    if (parameters.Length == 1 && content != null && parameters[0].ParameterType.IsInstanceOfType(content))
                    {
                        try { state = constructors[i].Invoke(new object[] { content }); }
                        catch { }
                    }
                }
            }

            if (state == null)
                throw new InvalidOperationException("Bannerlord CharacterCreationState could not be created.");

            manager.CleanAndPushState((CharacterCreationState)state, 0);
        }

        private static object CreateCharacterCreationContent()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type preferred = null;

            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    foreach (Type type in assemblies[i].GetTypes())
                    {
                        if (type == null || type.IsAbstract)
                            continue;
                        if (string.Equals(type.Name, "SandboxCharacterCreationContent", StringComparison.OrdinalIgnoreCase))
                            preferred = type;
                    }
                }
                catch { }
            }

            if (preferred != null)
            {
                try { return Activator.CreateInstance(preferred, true); }
                catch { }
            }

            return null;
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
                if (__instance == null || __instance.CharacterCreationManager == null || __instance.CharacterCreationManager.CharacterCreationContent == null)
                    return;
                if (MpcCharacterSlots.SelectedSlot < 0)
                    MpcCharacterSlots.Select(0);
                string name = __instance.CharacterCreationManager.CharacterCreationContent.MainCharacterName;
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
