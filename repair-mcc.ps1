$ErrorActionPreference='Stop'
$path='MultiplayerCampaignSubModule.cs'
$source=[IO.File]::ReadAllText($path)
$source=$source -replace "`r`n","`n"

function Replace-Once([string]$text,[string]$pattern,[string]$replacement,[string]$name){
  $result=[regex]::Replace($text,$pattern,$replacement,1)
  if($result -eq $text){ throw "Patch target not found: $name" }
  return $result
}

# Retry stale client connection instead of silently ignoring a new Connect request.
$source=Replace-Once $source '(?s)public void Connect\(\s*string ip\s*\)\s*\{.*?\n\s*\}\s*\n\s*private async Task ConnectAsync' @'
public void Connect(
            string ip)
        {
            lock (_connectionLock)
            {
                if (_connectionRunning)
                {
                    bool alive = false;
                    try { alive = _tcpClient != null && _tcpClient.Connected; } catch { alive = false; }
                    if (alive)
                        return;
                    DisconnectInternal();
                }
                else
                {
                    DisconnectInternal();
                }

                _cts = new CancellationTokenSource();
                _connectionRunning = true;
                _ = ConnectAsync(ip, _cts.Token);
            }
        }

        private async Task ConnectAsync'@ 'Connect retry'

# Do not consume WorldComplete without starting a real save-backed Campaign.
$source=Replace-Once $source '(?s)public static void FinishClientLoad\(\)\s*\{.*?\n\s*\}\s*(?=public static byte\[\] GetReceivedWorld)' @'
public static void FinishClientLoad()
        {
            byte[] world;
            lock (Sync)
            {
                if (!_complete || _worldData == null)
                    return;
                world = _worldData;
                _worldData = null;
                _complete = false;
            }

            MultiplayerCampaignSubModule.BeginTransferredWorldLoad();
            try
            {
                if (!MBSaveLoad.IsSaveGameFileExists("MCC"))
                {
                    HostConsole.WriteLine("[!] MCC save is missing on Client.");
                    MultiplayerCampaignSubModule.EndTransferredWorldLoad();
                    MultiplayerNetworkClient.Instance.Disconnect();
                    return;
                }

                LoadResult result = MBSaveLoad.LoadSaveGameData("MCC");
                if (result == null || !result.Successful)
                {
                    HostConsole.WriteLine("[!] Client could not load MCC.");
                    MultiplayerCampaignSubModule.EndTransferredWorldLoad();
                    MultiplayerNetworkClient.Instance.Disconnect();
                    return;
                }

                SandBoxGameManager manager = new SandBoxGameManager(result);
                MBGameManager.StartNewGame(manager);
                HostConsole.WriteLine("[*] Client MCC Campaign load started.");
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine("[!] Client MCC load error: " + ex.Message);
                MultiplayerCampaignSubModule.EndTransferredWorldLoad();
                MultiplayerNetworkClient.Instance.Disconnect();
            }
        }

        public static byte[] GetReceivedWorld'@ 'FinishClientLoad'

# Mark client network world loaded only after SandBox reports the save is completely loaded.
if(-not $source.Contains('public void MarkWorldLoaded()')){
  $source=Replace-Once $source '(?s)public void Disconnect\(\)\s*\{' @'
public void MarkWorldLoaded()
        {
            _worldLoaded = true;
            _worldReady = false;
            MultiplayerConnectionStatus.Set(MultiplayerConnectionState.Ready);
            _vm?.SetStatus("CONNECTED - MCC LOADED");
            MultiplayerCampaignSubModule.EndTransferredWorldLoad();
            try { SendPlayerReady(); } catch { }
        }

        public void Disconnect() {'@ 'MarkWorldLoaded'
}

# Locate MpcNetworkRuntime section and remove the MainParty fallback.
$rtStart=$source.IndexOf('internal static class MpcNetworkRuntime')
$rtEnd=$source.IndexOf('internal static class MpcRebuildPatches',$rtStart)
if($rtStart -lt 0 -or $rtEnd -lt 0){ throw 'MpcNetworkRuntime section not found' }
$rt=$source.Substring($rtStart,$rtEnd-$rtStart)
$rt=Replace-Once $rt '(?s)MobileParty party = MpcClientParty\.ActiveParty;\s*if \(party == null \|\| party == MobileParty\.MainParty\)\s*party = MobileParty\.MainParty;\s*if \(party == null\)\s*return;' 'MobileParty party = MpcClientParty.ActiveParty;\n            if (party == null || party == MobileParty.MainParty)\n                return;' 'Client Party fallback'
$rt=$rt.Replace('PartySize = Math.Max(1, Math.Min(10000, CampaignWorld.GetMainPartySize())),','PartySize = Math.Max(1, Math.Min(10000, party.MemberRoster != null ? party.MemberRoster.TotalManCount : 1)),')
$source=$source.Substring(0,$rtStart)+$rt+$source.Substring($rtEnd)

# Prune disconnected host entries before enforcing the single remote-client limit.
$hostStart=$source.IndexOf('private async Task AcceptLoopAsync')
$hostEnd=$source.IndexOf('internal void RemoveClient',$hostStart)
if($hostStart -lt 0 -or $hostEnd -lt 0){ throw 'AcceptLoop section not found' }
$host=$source.Substring($hostStart,$hostEnd-$hostStart)
$host=Replace-Once $host '(?s)lock \(_sync\)\s*\{.*?if \(_clients\.Count >= 1\)\s*\{.*?continue;\s*\}\s*_clients\.Add\(\s*client\s*\);\s*\}' @'
lock (_sync)
                {
                    for (int i = _clients.Count - 1; i >= 0; i--)
                    {
                        HostClientConnection stale = _clients[i];
                        if (stale == null || !stale.IsConnected)
                            _clients.RemoveAt(i);
                    }

                    if (_clients.Count >= 1)
                    {
                        SendErrorAndClose(client, "Server is full.");
                        continue;
                    }

                    _clients.Add(client);
                }
'@ 'Host stale connection prune'
$source=$source.Substring(0,$hostStart)+$host+$source.Substring($hostEnd)

# Replace the old final character gate: connection must not be blocked before MCC can load.
$gateStart=$source.IndexOf('internal static class MpcFinalCharacterGateV2')
if($gateStart -ge 0){
  $gateEnd=$source.IndexOf('    [HarmonyLib.HarmonyPatch', $gateStart+20)
  if($gateEnd -gt $gateStart){
    $gate=$source.Substring($gateStart,$gateEnd-$gateStart)
    $gate=[regex]::Replace($gate,'(?s)private static bool Prefix\(\)\s*\{.*?\n\s*\}', 'private static bool Prefix()\n        {\n            return true;\n        }',1)
    $source=$source.Substring(0,$gateStart)+$gate+$source.Substring($gateEnd)
  }
}

# Add MCC/Character bridge once. It launches vanilla Character Creation for a new Client character,
# and resumes the network session after FinalizeCharacterCreation.
if(-not $source.Contains('internal static class MpcFinalMccCharacterBridge')){
$bridge=@'

namespace MultiplayerCampaign
{
    internal static class MpcFinalMccCharacterBridge
    {
        [HarmonyLib.HarmonyPatch(typeof(SandBox.SandBoxGameManager), "OnLoadFinished")]
        private static class LoadFinishedPatch
        {
            private static void Postfix()
            {
                try
                {
                    if (!MultiplayerCampaignSubModule.IsLoadingTransferredWorld())
                        return;

                    bool hasCharacter = false;
                    try
                    {
                        hasCharacter = MpcFinalCharacterSystemV2.HasSelection;
                    }
                    catch
                    {
                        hasCharacter = false;
                    }

                    if (!hasCharacter)
                    {
                        Game.Current.GameStateManager.CleanAndPushState(
                            Game.Current.GameStateManager.CreateState<
                                TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState>(),
                            0
                        );
                        return;
                    }

                    MultiplayerNetworkClient.Instance.MarkWorldLoaded();
                    MultiplayerCampaignRebuildLayer.MpcClientParty.Ensure();
                }
                catch (Exception ex)
                {
                    HostConsole.WriteLine("[!] MCC completion error: " + ex.Message);
                }
            }
        }

        [HarmonyLib.HarmonyPatch(
            typeof(TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState),
            "FinalizeCharacterCreation"
        )]
        private static class CharacterCreationFinishedPatch
        {
            private static void Postfix()
            {
                try
                {
                    MultiplayerCampaignRebuildLayer.MpcSession.SelectOrCreateIdentityFromCurrentPlayer();
                    MpcFinalCharacterSystemV2.CreateSlot(
                        0,
                        MultiplayerCampaignRebuildLayer.MpcSession.Name,
                        ""
                    );
                    MultiplayerNetworkClient.Instance.MarkWorldLoaded();
                    MultiplayerCampaignRebuildLayer.MpcClientParty.Ensure();
                }
                catch (Exception ex)
                {
                    HostConsole.WriteLine("[!] Character creation completion error: " + ex.Message);
                }
            }
        }
    }
}
'@
$source += $bridge
}

[IO.File]::WriteAllText($path,$source,(New-Object Text.UTF8Encoding($false)))
Write-Host 'MCC repair source prepared.'
