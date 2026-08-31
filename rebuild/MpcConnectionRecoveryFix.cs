using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using SandBox;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MultiplayerCampaign
{
    internal static class MpcRecoveryRuntime
    {
        private const string TransferSaveName = "MCC_Transfer";
        private static readonly object Sync = new object();
        private static bool _loading;
        private static bool _loadStarted;

        public static bool Loading { get { lock (Sync) return _loading; } }

        public static void BeginLoad()
        {
            lock (Sync)
            {
                _loading = true;
                _loadStarted = false;
            }
            MultiplayerCampaignSubModule.BeginTransferredWorldLoad();
        }

        public static void MarkLoadStarted()
        {
            lock (Sync) _loadStarted = true;
        }

        public static bool TryFinishLoad()
        {
            lock (Sync)
            {
                if (!_loading || !_loadStarted)
                    return false;
                _loading = false;
                _loadStarted = false;
            }

            MultiplayerCampaignSubModule.EndTransferredWorldLoad();
            return true;
        }

        public static void AbortLoad(string message)
        {
            lock (Sync)
            {
                _loading = false;
                _loadStarted = false;
            }

            try { MultiplayerCampaignSubModule.EndTransferredWorldLoad(); } catch { }
            try { SetClientWorldLoaded(false); SetClientWorldReady(false); } catch { }
            try { MultiplayerWorldTransfer.Clear(); } catch { }
            try { MultiplayerConnectionStatus.Set(MultiplayerConnectionState.Connecting); } catch { }
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
            return path;
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
            try
            {
                lock (Sync)
                {
                    _loading = false;
                    _loadStarted = false;
                }
                SetClientWorldLoaded(false);
                SetClientWorldReady(false);
                MultiplayerWorldTransfer.Clear();
                MultiplayerSessionState.SetWorldReady(false);
                MultiplayerCampaignGameState.SetCampaignReady(false);
            }
            catch { }
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

                    LoadResult result = MBSaveLoad.LoadSaveGameData("MCC_Transfer");
                    if (result == null || !result.Successful)
                    {
                        MpcRecoveryRuntime.AbortLoad("MCC transfer save could not be loaded.");
                        return false;
                    }

                    MpcRecoveryRuntime.BeginLoad();
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
                try
                {
                    if (!MpcRecoveryRuntime.Loading)
                        MpcRecoveryRuntime.ResetConnectionState();
                }
                catch { }
            }
        }
    }
}