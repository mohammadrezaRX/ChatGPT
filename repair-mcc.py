from pathlib import Path
import re
p = Path('MultiplayerCampaignSubModule.cs')
s = p.read_text(encoding='utf-8-sig').replace('\r\n','\n')

def mb(src, pos):
    depth=0; i=pos; state=0
    while i < len(src):
        c=src[i]
        if state==0:
            if c=='/' and i+1<len(src) and src[i+1]=='/': state=1; i+=2; continue
            if c=='/' and i+1<len(src) and src[i+1]=='*': state=2; i+=2; continue
            if c=='"': state=3; i+=1; continue
            if c=="'": state=4; i+=1; continue
            if c=='{': depth+=1
            elif c=='}':
                depth-=1
                if depth==0: return i
            i+=1
        elif state==1:
            if c=='\n': state=0
            i+=1
        elif state==2:
            if c=='*' and i+1<len(src) and src[i+1]=='/': state=0; i+=2
            else: i+=1
        elif state==3:
            if c=='\\': i+=2; continue
            if c=='"': state=0
            i+=1
        else:
            if c=='\\': i+=2; continue
            if c=="'": state=0
            i+=1
    raise RuntimeError('brace match failed')

def rm(src, sig, body):
    m=re.search(sig,src,re.M)
    if not m: raise RuntimeError('not found '+sig)
    b=src.find('{',m.end()); e=mb(src,b)
    return src[:b+1]+'\n'+body.rstrip()+'\n    '+src[e:]

s=rm(s,r'\bpublic void Connect\(\s*string ip\s*\)',r'''
            lock (_connectionLock)
            {
                if (_connectionRunning)
                {
                    bool active = false;
                    try { active = _tcpClient != null && _tcpClient.Connected; }
                    catch { active = false; }
                    if (active)
                    {
                        _vm?.SetStatus("ALREADY CONNECTED");
                        return;
                    }
                    DisconnectInternal();
                }
                else
                {
                    DisconnectInternal();
                }
                _vm?.SetStatus("CONNECTING...");
                MultiplayerConnectionStatus.Set(
                    MultiplayerConnectionState.Connecting
                );
                _cts = new CancellationTokenSource();
                _connectionRunning = true;
                _ = ConnectAsync(ip, _cts.Token);
            }
''')

old=r'''            finally
            {
                IsConnected =
                    false;
                lock (_connectionLock)
                {
                    _connectionRunning =
                        false;
                }
            }'''
new=r'''            finally
            {
                IsConnected = false;
                lock (_connectionLock)
                {
                    if (_cts == null || _cts.Token == token)
                        _connectionRunning = false;
                }
            }'''
if old in s: s=s.replace(old,new,1)

m=re.search(r'\bprivate async Task AcceptLoopAsync\(\s*CancellationToken token\s*\)',s,re.M)
if not m: raise RuntimeError('accept loop missing')
b=s.find('{',m.end()); e=mb(s,b); block=s[b+1:e]
old=r'''                lock (_sync)
                {
                    /*
                     * The target build is two-player.
                     *
                     * Existing host is player one.
                     * Only one remote client is required.
                     */

                    if (_clients.Count >= 1)
                    {
                        SendErrorAndClose(
                            client,
                            "Server is full."
                        );
                        continue;
                    }
                    _clients.Add(
                        client
                    );
                }'''
new=r'''                lock (_sync)
                {
                    for (int i = _clients.Count - 1; i >= 0; i--)
                    {
                        HostClientConnection stale = _clients[i];
                        if (stale == null || !stale.IsConnected)
                            _clients.RemoveAt(i);
                    }
                    if (_clients.Count >= 1)
                    {
                        SendErrorAndClose(
                            client,
                            "Server is full."
                        );
                        continue;
                    }
                    _clients.Add(
                        client
                    );
                }'''
if old not in block: raise RuntimeError('capacity block missing')
block=block.replace(old,new,1)
s=s[:b+1]+block+s[e:]

m=re.search(r'\bpublic static void FinishClientLoad\(\)',s,re.M)
if not m: raise RuntimeError('FinishClientLoad missing')
b=s.find('{',m.end()); e=mb(s,b)
body=r'''
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
                    return;
                }
                LoadResult result = MBSaveLoad.LoadSaveGameData("MCC");
                if (result == null || !result.Successful)
                {
                    HostConsole.WriteLine("[!] Client could not load MCC.");
                    MultiplayerCampaignSubModule.EndTransferredWorldLoad();
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
            }
'''
s=s[:b+1]+'\n'+body.rstrip()+'\n        '+s[e:]

cm=re.search(r'\bpublic sealed class MultiplayerNetworkClient\b',s)
cb=s.find('{',cm.end()); ce=mb(s,cb); cl=s[cb+1:ce]
if 'public void MarkWorldLoaded()' not in cl:
    dm=re.search(r'\bpublic void Disconnect\(\)',cl)
    if not dm: raise RuntimeError('client Disconnect missing')
    bridge=r'''
        public void MarkWorldLoaded()
        {
            _worldLoaded = true;
            _worldReady = false;
            MultiplayerConnectionStatus.Set(
                MultiplayerConnectionState.Ready
            );
            _vm?.SetStatus("CONNECTED - MCC LOADED");
            MultiplayerCampaignSubModule.EndTransferredWorldLoad();
        }

'''
    cl=cl[:dm.start()]+bridge+cl[dm.start():]
    s=s[:cb+1]+cl+s[ce:]

needle=r'''            if (
                MultiplayerNetworkClient
                    .Instance
                    .IsWorldLoaded)
            {
                _worldInitialized = true;
            }'''
if 'MpcClientJoinFlow.Update();' not in s:
    if needle not in s: raise RuntimeError('campaign tick block missing')
    s=s.replace(needle,needle+'\n\n            MpcClientJoinFlow.Update();',1)

if 'internal static class MpcClientJoinFlow' not in s:
    at=s.find('// ============================================================\n// END OF REBUILT MULTIPLAYER CAMPAIGN')
    if at<0: at=s.rfind('\n}')
    add=r'''
    internal static class MpcClientJoinFlow
    {
        private static bool _started;
        private static bool _done;

        public static void Update()
        {
            if (_done ||
                !MultiplayerCampaignSubModule.IsLoadingTransferredWorld() ||
                Campaign.Current == null ||
                Game.Current == null)
                return;

            if (_started)
                return;

            try
            {
                if (Game.Current.GameStateManager.ActiveState
                    is TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState)
                {
                    _started = true;
                    return;
                }

                TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState state =
                    Game.Current.GameStateManager.CreateState<
                        TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState>();

                Game.Current.GameStateManager.CleanAndPushState(state, 0);
                _started = true;
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine("[!] Character Creation start failed: " + ex.Message);
            }
        }

        public static void Complete()
        {
            if (_done)
                return;

            try
            {
                MpcSession.SelectOrCreateIdentityFromCurrentPlayer();
                MpcClientParty.Ensure();
                _done = true;
                MultiplayerNetworkClient.Instance.MarkWorldLoaded();
            }
            catch (Exception ex)
            {
                HostConsole.WriteLine("[!] Character Creation complete failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(
        typeof(TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState),
        "FinalizeCharacterCreation"
    )]
    internal static class MpcCharacterCreationFinalizePatch
    {
        private static void Postfix()
        {
            MpcClientJoinFlow.Complete();
        }
    }

'''
    s=s[:at]+add+s[at:]

old=r'''            MobileParty party = MpcClientParty.ActiveParty;
            if (party == null || party == MobileParty.MainParty)
                party = MobileParty.MainParty;

            if (party == null)
                return;'''
new=r'''            MobileParty party = MpcClientParty.ActiveParty;
            if (party == null || party == MobileParty.MainParty)
                return;'''
if old in s: s=s.replace(old,new,1)

s=s.replace(
    'PartySize = Math.Max(1, Math.Min(10000, CampaignWorld.GetMainPartySize())),',
    'PartySize = Math.Max(1, Math.Min(10000, party.MemberRoster != null ? party.MemberRoster.TotalManCount : 1)),',
    1)

if s.count('{') != s.count('}'):
    raise RuntimeError('brace count mismatch')

p.write_text(s,encoding='utf-8',newline='\n')
print('repair complete')
