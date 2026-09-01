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
            object content = CreateCompatibleContent(stateType);

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
                    if (parameters.Length == 0 && content == null)
                        state = closed.Invoke(manager, null);
                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(object[]))
                        state = closed.Invoke(manager, new object[] { content == null ? new object[0] : new object[] { content } });
                }
                catch { }
            }

            if (state == null && content != null)
            {
                ConstructorInfo[] constructors = stateType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < constructors.Length && state == null; i++)
                {
                    ParameterInfo[] parameters = constructors[i].GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(content))
                        continue;
                    try { state = constructors[i].Invoke(new object[] { content }); }
                    catch { }
                }
            }

            if (state == null)
                throw new InvalidOperationException("Bannerlord CharacterCreationState could not be created with a compatible character-creation content object.");

            manager.CleanAndPushState((CharacterCreationState)state, 0);
        }

        private static object CreateCompatibleContent(Type stateType)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            ConstructorInfo[] constructors = stateType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int c = 0; c < constructors.Length; c++)
            {
                ParameterInfo[] parameters = constructors[c].GetParameters();
                if (parameters.Length != 1)
                    continue;

                Type required = parameters[0].ParameterType;
                for (int a = 0; a < assemblies.Length; a++)
                {
                    try
                    {
                        Type[] types = assemblies[a].GetTypes();
                        for (int t = 0; t < types.Length; t++)
                        {
                            Type candidate = types[t];
                            if (candidate == null || candidate.IsAbstract || candidate == required)
                                continue;
                            if (!required.IsAssignableFrom(candidate))
                                continue;

                            try
                            {
                                object value = Activator.CreateInstance(candidate, true);
                                if (value != null)
                                    return value;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
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
