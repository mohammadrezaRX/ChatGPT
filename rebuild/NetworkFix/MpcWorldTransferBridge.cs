using System;
using HarmonyLib;

namespace MultiplayerCampaign
{
    /// <summary>
    /// Unifies the active WorldTransferService receive path with the
    /// recovery/save-loading path. The old code had two independent
    /// world receivers; packets were accumulated by WorldTransferService
    /// while the actual load patch listened to MultiplayerWorldTransfer.
    /// </summary>
    internal static class MpcWorldTransferBridge
    {
        [HarmonyPatch(typeof(WorldTransferService), "ReceiveBegin")]
        private static class ReceiveBeginPatch
        {
            private static bool Prefix(byte[] payload)
            {
                try
                {
                    MultiplayerWorldTransfer.HandleWorldBegin(payload);
                    return false;
                }
                catch (Exception ex)
                {
                    try { HostConsole.WriteLine("[!] World begin bridge error: " + ex.Message); } catch { }
                    return false;
                }
            }
        }

        [HarmonyPatch(typeof(WorldTransferService), "ReceiveChunk")]
        private static class ReceiveChunkPatch
        {
            private static bool Prefix(byte[] payload)
            {
                try
                {
                    MultiplayerWorldTransfer.HandleWorldChunk(payload);
                    return false;
                }
                catch (Exception ex)
                {
                    try { HostConsole.WriteLine("[!] World chunk bridge error: " + ex.Message); } catch { }
                    return false;
                }
            }
        }

        [HarmonyPatch(typeof(WorldTransferService), "ReceiveComplete")]
        private static class ReceiveCompletePatch
        {
            private static bool Prefix(byte[] payload)
            {
                try
                {
                    MultiplayerWorldTransfer.HandleWorldComplete(payload);
                    MultiplayerWorldTransfer.FinishClientLoad();
                    return false;
                }
                catch (Exception ex)
                {
                    try { MpcRecoveryRuntime.AbortLoad(ex.Message); } catch { }
                    return false;
                }
            }
        }

        // ReceiveComplete is followed by WorldReadyHandler.Handle() in the
        // network dispatcher. During the transfer load that is too early:
        // the SaveGame has only just been handed to Bannerlord. Let the
        // OnLoadFinished recovery patch publish Ready instead.
        [HarmonyPatch(typeof(WorldReadyHandler), "Handle")]
        private static class WorldReadyPatch
        {
            private static bool Prefix()
            {
                try
                {
                    return !MpcRecoveryRuntime.Loading;
                }
                catch
                {
                    return true;
                }
            }
        }
    }
}
