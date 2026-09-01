using System;
using System.Reflection;
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
            GameStateManager manager =
                Game.Current != null && Game.Current.GameStateManager != null
                    ? Game.Current.GameStateManager
                    : GameStateManager.Current;

            if (manager == null)
                throw new InvalidOperationException("Bannerlord GameStateManager is not available yet.");

            object content = CreateSandboxCharacterCreationContent();
            if (content == null)
                throw new InvalidOperationException("SandboxCharacterCreationContent could not be created.");

            CharacterCreationState state = CreateCharacterCreationState(manager, content);
            if (state == null)
                throw new InvalidOperationException("Bannerlord CharacterCreationState could not be created.");

            manager.CleanAndPushState(state, 0);
        }

        private static CharacterCreationState CreateCharacterCreationState(GameStateManager manager, object content)
        {
            Type stateType = typeof(CharacterCreationState);
            MethodInfo[] methods = typeof(GameStateManager).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "CreateState" ||
                    !method.IsGenericMethodDefinition ||
                    method.GetGenericArguments().Length != 1)
                    continue;

                MethodInfo closed;
                try
                {
                    closed = method.MakeGenericMethod(stateType);
                }
                catch
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(object[]))
                    {
                        object result = closed.Invoke(
                            manager,
                            new object[] { new object[] { content } });

                        CharacterCreationState state = result as CharacterCreationState;
                        if (state != null)
                            return state;
                    }
                }
                catch
                {
                }
            }

            try
            {
                return manager.CreateState<CharacterCreationState>(content);
            }
            catch
            {
                return null;
            }
        }

        private static object CreateSandboxCharacterCreationContent()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int a = 0; a < assemblies.Length; a++)
            {
                Type type = FindTypeByName(
                    assemblies[a],
                    "SandboxCharacterCreationContent");

                if (type == null || type.IsAbstract)
                    continue;

                try
                {
                    return Activator.CreateInstance(type, true);
                }
                catch
                {
                }
            }

            return null;
        }

        private static Type FindTypeByName(
            Assembly assembly,
            string typeName)
        {
            try
            {
                Type direct = assembly.GetType(typeName, false, true);
                if (direct != null)
                    return direct;

                Type[] types = assembly.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i] != null &&
                        string.Equals(
                            types[i].Name,
                            typeName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return types[i];
                    }
                }
            }
            catch
            {
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
                __instance.SetStatus("CREATE OR SELECT CHARACTER");
            }
            catch (Exception ex)
            {
                try { HostConsole.WriteLine("[!] Character page: " + ex); } catch { }
            }
            return true;
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
                try { __instance.SetStatus("CHARACTER CREATION FAILED"); } catch { }
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
