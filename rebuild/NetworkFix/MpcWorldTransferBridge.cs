using System;
using HarmonyLib;

namespace MultiplayerCampaign
{
    /// <summary>
    /// Keeps the active client world-transfer receiver and the
    /// recovery/save-loading path connected.
    ///
    /// MultiplayerNetworkClient routes WorldBegin/WorldChunk/
    /// WorldComplete directly to MultiplayerWorldTransfer.
    /// The previous bridge patched WorldTransferService instead,
    /// so the active client never reached FinishClientLoad().
    /// </summary>
    internal static class MpcWorldTransferBridge
    {
        [HarmonyPatch(typeof(MultiplayerWorldTransfer), "HandleWorldBegin")]
        private static class BeginPatch
        {
            private static void Prefix()
            {
                try
                {
                    // Start the client-side recovery timeout before receiving data.
                    if (!MpcRecoveryRuntime.Loading)
                    {
                        MpcRecoveryRuntime.BeginLoad();
                    }

                    MultiplayerConnectionStatus.Set(
                        MultiplayerConnectionState.SynchronizingWorld
                    );
                }
                catch (Exception ex)
                {
                    try
                    {
                        HostConsole.WriteLine(
                            "[!] World transfer initialization failed: " + ex.Message
                        );
                    }
                    catch { }
                }
            }
        }

        [HarmonyPatch(typeof(MultiplayerWorldTransfer), "HandleWorldComplete")]
        private static class CompletePatch
        {
            private static void Postfix()
            {
                try
                {
                    // The active MultiplayerNetworkClient does not call
                    // FinishClientLoad() after HandleWorldComplete().
                    // Trigger it here so MpcSaveTransferPatch can take over
                    // and load MCC_Transfer through Bannerlord's save system.
                    MultiplayerWorldTransfer.FinishClientLoad();
                }
                catch (Exception ex)
                {
                    try
                    {
                        MpcRecoveryRuntime.AbortLoad(
                            "World transfer completion failed: " + ex.Message
                        );
                    }
                    catch { }
                }
            }
        }
    }
}
