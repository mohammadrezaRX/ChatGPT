using System;
using System.Reflection;
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

            Type stateType = FindCharacterCreationStateType();
            if (stateType == null)
            {
                lock (Sync)
                {
                    _opening = false;
                }

                HostConsole.WriteLine("[!] Bannerlord CharacterCreationState type was not found.");
                return;
            }

            lock (Sync)
            {
                if (!_pending || _opening)
                    return;

                _opening = true;
                _pending = false;
            }

            try
            {
                MethodInfo createState = FindCreateStateMethod(manager.GetType());
                if (createState == null)
                    throw new MissingMethodException("GameStateManager.CreateState<T> was not found.");

                MethodInfo closedCreateState =
                    createState.MakeGenericMethod(stateType);

                object state =
                    closedCreateState.Invoke(
                        manager,
                        null
                    );

                if (state == null)
                    throw new InvalidOperationException("Bannerlord CharacterCreationState could not be created.");

                MethodInfo cleanAndPush =
                    AccessTools.Method(
                        manager.GetType(),
                        "CleanAndPushState",
                        new[] { stateType, typeof(int) }
                    );

                if (cleanAndPush == null)
                    throw new MissingMethodException("GameStateManager.CleanAndPushState was not found.");

                cleanAndPush.Invoke(
                    manager,
                    new object[] { state, 0 }
                );

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
                    HostConsole.WriteLine("[!] Native Character Creation failed: " + ex.Message);
                }
                catch
                {
                }
            }
        }

        internal static Type FindCharacterCreationStateType()
        {
            Type type = AccessTools.TypeByName("CharacterCreationState");
            if (type != null)
                return type;

            string[] names =
            {
                "TaleWorlds.CampaignSystem.CharacterCreationState",
                "TaleWorlds.CampaignSystem.CharacterCreation.CharacterCreationState",
                "TaleWorlds.MountAndBlade.CharacterCreationState",
                "SandBox.CharacterCreationState"
            };

            for (int i = 0; i < names.Length; i++)
            {
                type = AccessTools.TypeByName(names[i]);
                if (type != null)
                    return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type[] types = assemblies[i].GetTypes();
                    for (int j = 0; j < types.Length; j++)
                    {
                        if (types[j] != null && types[j].Name == "CharacterCreationState")
                            return types[j];
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static MethodInfo FindCreateStateMethod(Type managerType)
        {
            MethodInfo[] methods = managerType.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic
            );

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null ||
                    method.Name != "CreateState" ||
                    !method.IsGenericMethodDefinition)
                {
                    continue;
                }

                if (method.GetGenericArguments().Length != 1 ||
                    method.GetParameters().Length != 0)
                {
                    continue;
                }

                return method;
            }

            return null;
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

    [HarmonyPatch]
    internal static class MpcNativeCharacterCreationSavePatch
    {
        private static MethodBase TargetMethod()
        {
            Type stateType =
                MpcNativeCharacterCreationFix.FindCharacterCreationStateType();

            if (stateType == null)
                return null;

            return AccessTools.Method(
                stateType,
                "FinalizeCharacterCreationState"
            );
        }

        private static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null)
                    return;

                Type stateType = __instance.GetType();
                PropertyInfo managerProperty =
                    stateType.GetProperty(
                        "CharacterCreationManager",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                object manager =
                    managerProperty?.GetValue(
                        __instance,
                        null
                    );

                if (manager == null)
                    return;

                PropertyInfo contentProperty =
                    manager.GetType().GetProperty(
                        "CharacterCreationContent",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                object content =
                    contentProperty?.GetValue(
                        manager,
                        null
                    );

                if (content == null)
                    return;

                PropertyInfo nameProperty =
                    content.GetType().GetProperty(
                        "MainCharacterName",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                string name =
                    nameProperty?.GetValue(
                        content,
                        null
                    ) as string;

                if (string.IsNullOrWhiteSpace(name))
                    return;

                if (MpcCharacterSlots.SelectedSlot < 0)
                    MpcCharacterSlots.Select(0);

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
