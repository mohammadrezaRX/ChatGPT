// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;

using TaleWorlds.CampaignSystem;
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
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.ViewModelCollection.InitialMenu;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem.Load;
using TaleWorlds.SaveSystem;
using TaleWorlds.ScreenSystem;



// ============================================================
// SAFE CAMPAIGN SAVE GUARD
// ============================================================

internal static class SafeCampaignSaveGuard
{
    public static bool CanSave()
    {
        try
        {
            if (
                Campaign.Current == null)
            {
                return false;
            }

            if (
                Hero.MainHero == null)
            {
                return false;
            }

            if (
                MobileParty.MainParty == null)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Prepare()
    {
        /*
         * IMPORTANT:
         *
         * There is intentionally no remote Hero or
         * remote MobileParty registered in Campaign.
         *
         * Therefore Bannerlord's normal save system
         * only serializes the actual local Campaign.
         */
    }
}


namespace MultiplayerCampaign
{
    internal static class MpcRecoveryRuntime
    {
        private const string TransferSaveName = "MCC_Transfer";
        private const int LoadTimeoutSeconds = 90;
        private static readonly object Sync = new object();
        private static bool _loading;
        private static bool _loadStarted;
        private static DateTime _loadDeadlineUtc;

        public static bool Loading { get { lock (Sync) return _loading; } }

        public static void BeginLoad()
        {
            lock (Sync)
            {
                _loading = true;
                _loadStarted = false;
                _loadDeadlineUtc = DateTime.UtcNow.AddSeconds(LoadTimeoutSeconds);
            }

            ResetTransferReceivers();
            MultiplayerCampaignSubModule.BeginTransferredWorldLoad();
        }

        public static void MarkLoadStarted()
        {
            lock (Sync)
            {
                if (!_loading)
                    return;
                _loadStarted = true;
                _loadDeadlineUtc = DateTime.UtcNow.AddSeconds(LoadTimeoutSeconds);
            }
        }

        public static bool TryFinishLoad()
        {
            lock (Sync)
            {
                if (!_loading || !_loadStarted)
                    return false;
                _loading = false;
                _loadStarted = false;
                _loadDeadlineUtc = DateTime.MinValue;
            }

            MultiplayerCampaignSubModule.EndTransferredWorldLoad();
            return true;
        }

        public static void Tick()
        {
            bool timedOut;
            lock (Sync)
            {
                timedOut = _loading &&
                           _loadDeadlineUtc != DateTime.MinValue &&
                           DateTime.UtcNow >= _loadDeadlineUtc;
            }

            if (timedOut)
                AbortLoad("Timed out while loading the transferred MCC world.");
        }

        public static void AbortLoad(string message)
        {
            lock (Sync)
            {
                _loading = false;
                _loadStarted = false;
                _loadDeadlineUtc = DateTime.MinValue;
            }

            try { MultiplayerCampaignSubModule.EndTransferredWorldLoad(); } catch { }
            try { SetClientWorldLoaded(false); SetClientWorldReady(false); } catch { }
            ResetTransferReceivers();
            try { MultiplayerSessionState.SetWorldReady(false); } catch { }
            try { MultiplayerCampaignGameState.SetCampaignReady(false); } catch { }
            try { MultiplayerConnectionStatus.Set(MultiplayerConnectionState.Connecting); } catch { }
            try { MultiplayerNetworkClient.Instance.Disconnect(); } catch { }
            try { HostConsole.WriteLine("[!] MCC transfer aborted: " + message); } catch { }
        }

        public static byte[] ReadHostSave()
        {
            string path = FindSave(MultiplayerCampaignSubModule.HostSaveName);
            return path == null ? null : File.ReadAllBytes(path);
        }

        public static string WriteTransferSave(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            string dir = GetSaveDirectory();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, TransferSaveName + ".sav");
            File.WriteAllBytes(path, data);
            return File.Exists(path) && new FileInfo(path).Length == data.Length ? path : null;
        }

        private static string FindSave(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string wanted = name.Trim();
            string[] roots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mount and Blade II Bannerlord", "Game Saves"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Mount and Blade II Bannerlord", "Game Saves")
            };

            for (int r = 0; r < roots.Length; r++)
            {
                if (!Directory.Exists(roots[r]))
                    continue;

                string exact = Path.Combine(roots[r], wanted + ".sav");
                if (File.Exists(exact))
                    return exact;

                string[] files = Directory.GetFiles(roots[r], "*.sav", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(files[i]), wanted, StringComparison.OrdinalIgnoreCase))
                        return files[i];
                }
            }

            return null;
        }

        private static string GetSaveDirectory()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string dir = Path.Combine(documents, "Mount and Blade II Bannerlord", "Game Saves");
            if (Directory.Exists(dir))
                return dir;

            string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "Mount and Blade II Bannerlord", "Game Saves");
            return Path.GetFullPath(localLow);
        }

        private static void ResetTransferReceivers()
        {
            try { WorldTransferService.Reset(); } catch { }
            try { MultiplayerWorldTransfer.Clear(); } catch { }
        }

        public static void SetClientWorldLoaded(bool value)
        {
            try
            {
                FieldInfo field = typeof(MultiplayerNetworkClient).GetField("_worldLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                    field.SetValue(MultiplayerNetworkClient.Instance, value);
            }
            catch { }
        }

        public static void SetClientWorldReady(bool value)
        {
            try
            {
                FieldInfo field = typeof(MultiplayerNetworkClient).GetField("_worldReady", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                    field.SetValue(MultiplayerNetworkClient.Instance, value);
            }
            catch { }
        }

        public static void ResetConnectionState()
        {
            lock (Sync)
            {
                _loading = false;
                _loadStarted = false;
                _loadDeadlineUtc = DateTime.MinValue;
            }

            try { MultiplayerCampaignSubModule.EndTransferredWorldLoad(); } catch { }
            try { SetClientWorldLoaded(false); SetClientWorldReady(false); } catch { }
            ResetTransferReceivers();
            try { MultiplayerSessionState.SetWorldReady(false); } catch { }
            try { MultiplayerCampaignGameState.SetCampaignReady(false); } catch { }
        }
    }


    internal static class MpcSaveTransferPatch
    {
        [HarmonyPatch(typeof(MultiplayerCampaignHost), "SendWorldToClientAsync")]
        private static class HostWorldPatch
        {
            private static bool Prefix(HostClientConnection client, ref Task __result)
            {
                __result = SendSaveAsync(client);
                return false;
            }

            private static async Task SendSaveAsync(HostClientConnection client)
            {
                try
                {
                    byte[] save = MpcRecoveryRuntime.ReadHostSave();
                    if (save == null || save.Length == 0)
                    {
                        client.SendError("MCC save file was not found on the Host.");
                        return;
                    }

                    MpcRecoveryRuntime.BeginLoad();
                    const int chunkSize = 48 * 1024;
                    client.Send(new NetworkMessageData(
                        NetworkPacketType.WorldBegin,
                        NetworkProtocol.CreatePayload(w => w.Write((long)save.Length))));

                    int offset = 0;
                    while (offset < save.Length)
                    {
                        int count = Math.Min(chunkSize, save.Length - offset);
                        byte[] chunk = new byte[count];
                        Buffer.BlockCopy(save, offset, chunk, 0, count);
                        client.Send(new NetworkMessageData(NetworkPacketType.WorldChunk, chunk));
                        offset += count;
                        await Task.Yield();
                    }

                    client.Send(new NetworkMessageData(NetworkPacketType.WorldComplete, Array.Empty<byte>()));
                    client.Send(new NetworkMessageData(
                        NetworkPacketType.WorldJoinAck,
                        NetworkProtocol.CreatePayload(w => w.Write("MCC save received. Loading Host world..."))));
                }
                catch (Exception ex)
                {
                    try { client.SendError("World transfer failed: " + ex.Message); } catch { }
                }
            }
        }

        [HarmonyPatch(typeof(MultiplayerWorldTransfer), "FinishClientLoad")]
        private static class ClientWorldPatch
        {
            private static bool Prefix()
            {
                try
                {
                    byte[] world = MultiplayerWorldTransfer.GetReceivedWorld();
                    if (world == null || world.Length == 0)
                    {
                        MpcRecoveryRuntime.AbortLoad("No transferred world bytes were received.");
                        return false;
                    }

                    string path = MpcRecoveryRuntime.WriteTransferSave(world);
                    if (path == null || !File.Exists(path))
                    {
                        MpcRecoveryRuntime.AbortLoad("Transferred save could not be written.");
                        return false;
                    }

                    MpcRecoveryRuntime.BeginLoad();
                    LoadResult result = MBSaveLoad.LoadSaveGameData("MCC_Transfer");
                    if (result == null || !result.Successful)
                    {
                        MpcRecoveryRuntime.AbortLoad("MCC transfer save could not be loaded.");
                        return false;
                    }

                    MpcRecoveryRuntime.MarkLoadStarted();
                    MpcRecoveryRuntime.SetClientWorldReady(false);
                    MpcRecoveryRuntime.SetClientWorldLoaded(false);
                    MBGameManager.StartNewGame(new SandBoxGameManager(result));
                    return false;
                }
                catch (Exception ex)
                {
                    MpcRecoveryRuntime.AbortLoad(ex.Message);
                    return false;
                }
            }
        }

        [HarmonyPatch(typeof(SandBoxGameManager), "OnLoadFinished")]
        private static class ClientLoadFinishedPatch
        {
            private static void Postfix()
            {
                try
                {
                    if (!MpcRecoveryRuntime.Loading)
                        return;

                    MpcRecoveryRuntime.SetClientWorldLoaded(true);
                    MpcRecoveryRuntime.SetClientWorldReady(false);
                    MultiplayerConnectionStatus.Set(MultiplayerConnectionState.Ready);
                    MultiplayerCampaignGameState.SetCampaignReady(true);
                    MultiplayerSessionState.SetWorldReady(true);
                    MultiplayerNetworkClient.Instance.SendPlayerReady();
                    MpcRecoveryRuntime.TryFinishLoad();
                    HostConsole.WriteLine("[*] MCC Host world loaded on Client.");
                }
                catch (Exception ex)
                {
                    MpcRecoveryRuntime.AbortLoad(ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(MultiplayerNetworkClient), "DisconnectInternal")]
        private static class DisconnectPatch
        {
            private static void Prefix()
            {
                try { MpcRecoveryRuntime.ResetConnectionState(); } catch { }
            }
        }

        [HarmonyPatch(typeof(MultiplayerNetworkClient), "Update")]
        private static class UpdatePatch
        {
            private static void Postfix()
            {
                try { MpcRecoveryRuntime.Tick(); } catch { }
            }
        }
    }

}

