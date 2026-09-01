// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

using HarmonyLib;
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.
// Thematic MPC module. Original declarations are preserved and grouped by responsibility.

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
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.ViewModelCollection.InitialMenu;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem.Load;
using TaleWorlds.SaveSystem;
using TaleWorlds.ScreenSystem;

namespace MultiplayerCampaign
{
    public sealed class MultiplayerUIState
    {
        private static readonly object Sync = new object();
        private bool _visible;
        private bool _connected;
        private bool _serverRunning;
        private bool _worldSyncing;
        private bool _worldReady;
        private string _status = "";
        private int _remotePlayers;

        public bool Visible { get { lock (Sync) return _visible; } }
        public bool Connected { get { lock (Sync) return _connected; } }
        public bool ServerRunning { get { lock (Sync) return _serverRunning; } }
        public bool WorldSyncing { get { lock (Sync) return _worldSyncing; } }
        public bool WorldReady { get { lock (Sync) return _worldReady; } }
        public string Status { get { lock (Sync) return _status; } }
        public int RemotePlayers { get { lock (Sync) return _remotePlayers; } }
        public void SetVisible(bool value) { lock (Sync) _visible = value; }
        public void SetConnected(bool value) { lock (Sync) _connected = value; }
        public void SetServerRunning(bool value) { lock (Sync) _serverRunning = value; }
        public void SetWorldSyncing(bool value) { lock (Sync) _worldSyncing = value; }
        public void SetWorldReady(bool value) { lock (Sync) _worldReady = value; }
        public void SetStatus(string value) { lock (Sync) _status = value ?? ""; }
        public void SetRemotePlayers(int count) { lock (Sync) _remotePlayers = System.Math.Max(0, count); }
        public void Reset()
        {
            lock (Sync)
            {
                _visible = false;
                _connected = false;
                _serverRunning = false;
                _worldSyncing = false;
                _worldReady = false;
                _status = "";
                _remotePlayers = 0;
            }
        }
    }









    internal static class MultiplayerUIStateManager
    {
        private static readonly MultiplayerUIState State = new MultiplayerUIState();
        public static MultiplayerUIState Current { get { return State; } }
        public static void Reset() { State.Reset(); }
    }









    public sealed class MultiplayerCampaignVM : ViewModel
    {
        private string _ipAddress = "127.0.0.1";
        private string _playerName = "Player";
        private string _characterName = "";
        private string _statusText = "";
        private bool _showMain = true;
        private bool _showCharacter;
        private bool _showCreate;
        private bool _showJoin;

        public MultiplayerCampaignVM()
        {
            MultiplayerNetworkClient.Instance.SetViewModel(this);
            _playerName = LocalPlayerState.GetDisplayName();
            MpcCharacterSlots.EnsureSelectedFromExisting();
            RefreshCharacterState();
        }

        [DataSourceProperty]
        public string IpAddress
        {
            get { return _ipAddress; }
            set { if (_ipAddress == value) return; _ipAddress = value; OnPropertyChangedWithValue(value, nameof(IpAddress)); }
        }

        [DataSourceProperty]
        public string PlayerName
        {
            get { return _playerName; }
            set { if (_playerName == value) return; _playerName = value; OnPropertyChangedWithValue(value, nameof(PlayerName)); }
        }

        [DataSourceProperty]
        public string CharacterName
        {
            get { return _characterName; }
            set { if (_characterName == value) return; _characterName = value; OnPropertyChangedWithValue(value, nameof(CharacterName)); }
        }

        [DataSourceProperty] public string Slot1Name { get { return MpcCharacterSlots.GetName(0); } }
        [DataSourceProperty] public string Slot2Name { get { return MpcCharacterSlots.GetName(1); } }
        [DataSourceProperty] public string Slot3Name { get { return MpcCharacterSlots.GetName(2); } }
        [DataSourceProperty] public bool HasSelectedCharacter { get { return MpcCharacterSlots.HasSelectedCharacter; } }

        [DataSourceProperty]
        public string StatusText
        {
            get { return _statusText; }
            set { if (_statusText == value) return; _statusText = value; OnPropertyChangedWithValue(value, nameof(StatusText)); }
        }

        public void SetStatus(string text)
        {
            StatusText = text;
        }

        [DataSourceProperty]
        public bool ShowMain
        {
            get { return _showMain; }
            private set { if (_showMain == value) return; _showMain = value; OnPropertyChangedWithValue(value, nameof(ShowMain)); }
        }

        [DataSourceProperty]
        public bool ShowCharacter
        {
            get { return _showCharacter; }
            private set { if (_showCharacter == value) return; _showCharacter = value; OnPropertyChangedWithValue(value, nameof(ShowCharacter)); }
        }

        [DataSourceProperty]
        public bool ShowCreate
        {
            get { return _showCreate; }
            private set { if (_showCreate == value) return; _showCreate = value; OnPropertyChangedWithValue(value, nameof(ShowCreate)); }
        }

        [DataSourceProperty]
        public bool ShowJoin
        {
            get { return _showJoin; }
            private set { if (_showJoin == value) return; _showJoin = value; OnPropertyChangedWithValue(value, nameof(ShowJoin)); }
        }

        public void ExecuteOpenCreate()
        {
            ShowMain = false; ShowCharacter = true; ShowCreate = false; ShowJoin = false;
            StatusText = "CREATE OR SELECT A CHARACTER";
            RefreshCharacterState();
        }

        public void ExecuteOpenJoin()
        {
            MpcCharacterSlots.EnsureSelectedFromExisting();
            RefreshCharacterState();
            if (!MpcCharacterSlots.HasSelectedCharacter)
            {
                ShowMain = false; ShowCharacter = true; ShowCreate = false; ShowJoin = false;
                StatusText = "CREATE OR SELECT A CHARACTER BEFORE JOINING";
                return;
            }
            ShowMain = false; ShowCharacter = false; ShowCreate = false; ShowJoin = true;
            StatusText = "";
        }

        public void ExecuteBackToMain()
        {
            ShowMain = true; ShowCharacter = false; ShowCreate = false; ShowJoin = false; StatusText = "";
        }

        public void ExecuteBackToJoin()
        {
            if (!MpcCharacterSlots.HasSelectedCharacter)
            {
                ShowCharacter = true; ShowJoin = false;
                StatusText = "CREATE OR SELECT A CHARACTER BEFORE JOINING";
                return;
            }
            ShowCharacter = false; ShowJoin = true; StatusText = "";
        }

        public void ExecuteBack()
        {
            MultiplayerNetworkClient.Instance.Disconnect();
            ScreenManager.PopScreen();
        }

        public void ExecuteSelectSlot1() { SelectCharacterSlot(0); }
        public void ExecuteSelectSlot2() { SelectCharacterSlot(1); }
        public void ExecuteSelectSlot3() { SelectCharacterSlot(2); }

        private void SelectCharacterSlot(int slot)
        {
            MpcCharacterSlots.Select(slot);
            CharacterName = MpcCharacterSlots.GetName(slot);
            if (CharacterName == "Empty Slot") CharacterName = "";
            RefreshCharacterBindings();
            StatusText = "CHARACTER SLOT " + (slot + 1) + " SELECTED";
        }

        public void ExecuteCreateCharacter()
        {
            if (MpcCharacterSlots.SelectedSlot < 0)
                MpcCharacterSlots.Select(0);

            string name = string.IsNullOrWhiteSpace(CharacterName) ? "" : CharacterName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                StatusText = "CHARACTER NAME REQUIRED";
                return;
            }

            if (!MpcCharacterSlots.SaveSelected(name))
            {
                StatusText = "CHARACTER COULD NOT BE SAVED";
                return;
            }

            LocalPlayerState.SetDisplayName(name);
            PlayerName = name;
            RefreshCharacterBindings();
            StatusText = "CHARACTER CREATED - CONTINUE TO JOIN";
        }

        public void ExecuteStartHost()
        {
            string name = string.IsNullOrWhiteSpace(PlayerName) ? "Host" : PlayerName.Trim();
            LocalPlayerState.SetDisplayName(name);
            StatusText = "LOADING MCC...";
            MultiplayerCampaignSubModule.RequestHost();
            if (!MultiplayerCampaignSubModule.LoadHostCampaign())
            {
                StatusText = "MCC LOAD FAILED";
                MultiplayerCampaignSubModule.StopHost();
                return;
            }
            StatusText = "MCC LOADED";
        }

        public void ExecuteJoinHost()
        {
            MpcCharacterSlots.EnsureSelectedFromExisting();
            if (!MpcCharacterSlots.HasSelectedCharacter)
            {
                ShowMain = false; ShowCharacter = true; ShowJoin = false;
                StatusText = "CREATE OR SELECT A CHARACTER BEFORE JOINING";
                RefreshCharacterState();
                return;
            }

            if (string.IsNullOrWhiteSpace(IpAddress))
            {
                StatusText = "HOST IP REQUIRED";
                return;
            }

            string character = MpcCharacterSlots.GetName(MpcCharacterSlots.SelectedSlot);
            if (string.IsNullOrWhiteSpace(character) || character == "Empty Slot")
            {
                ShowCharacter = true; ShowJoin = false;
                StatusText = "CREATE OR SELECT A CHARACTER BEFORE JOINING";
                return;
            }

            LocalPlayerState.SetDisplayName(character);
            PlayerName = character;
            StatusText = "CONNECTING...";
            MultiplayerNetworkClient.Instance.Connect(IpAddress.Trim());
        }

        public void UpdateNetwork()
        {
            MultiplayerNetworkClient.Instance.Update();
            if (MultiplayerNetworkClient.Instance.ConsumeWorldReady())
            {
                StatusText = "LOADING HOST WORLD...";
                MultiplayerWorldTransfer.FinishClientLoad();
            }
        }

        private void RefreshCharacterState()
        {
            int selected = MpcCharacterSlots.SelectedSlot;
            CharacterName = selected >= 0 ? MpcCharacterSlots.GetName(selected) : "";
            if (CharacterName == "Empty Slot") CharacterName = "";
            RefreshCharacterBindings();
        }

        private void RefreshCharacterBindings()
        {
            OnPropertyChanged(nameof(Slot1Name));
            OnPropertyChanged(nameof(Slot2Name));
            OnPropertyChanged(nameof(Slot3Name));
            OnPropertyChanged(nameof(HasSelectedCharacter));
        }
    }









    [HarmonyPatch(typeof(InitialMenuVM), "RefreshMenuOptions")]
    public static class InitialMenuPatch
    {
        private const string OptionId = "MultiplayerCampaign";

        public static void Postfix(InitialMenuVM __instance)
        {
            for (int i = 0; i < __instance.MenuOptions.Count; i++)
            {
                InitialMenuOptionVM option = __instance.MenuOptions[i];
                if (option == null || option.InitialStateOption == null) continue;
                if (option.InitialStateOption.Id == OptionId) return;
            }

            InitialStateOption optionToAdd = new InitialStateOption(
                OptionId,
                new TextObject("Multiplayer Campaign"),
                90,
                OpenMultiplayerCampaign,
                () => (false, new TextObject("")),
                new TextObject(""),
                () => false);

            __instance.MenuOptions.Add(new InitialMenuOptionVM(optionToAdd));
        }

        private static void OpenMultiplayerCampaign()
        {
            ScreenManager.PushScreen(ViewCreatorManager.CreateScreenView<MultiplayerCampaignScreen>());
        }
    }









    public sealed class MultiplayerCampaignScreen : ScreenBase
    {
        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;
        private MultiplayerCampaignVM _vm;

        protected override void OnInitialize()
        {
            base.OnInitialize();
            _vm = new MultiplayerCampaignVM();
            _layer = new GauntletLayer("MultiplayerCampaign", 100, true);
            _layer.IsFocusLayer = true;
            AddLayer(_layer);
            _layer.InputRestrictions.SetInputRestrictions();
            _movie = _layer.LoadMovie("MultiplayerCampaign", _vm);
        }

        protected override void OnActivate()
        {
            base.OnActivate();
            if (_layer != null)
            {
                _layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(_layer);
            }
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            if (_vm != null) _vm.UpdateNetwork();
        }

        protected override void OnDeactivate()
        {
            if (_layer != null)
            {
                _layer.IsFocusLayer = false;
                ScreenManager.TryLoseFocus(_layer);
            }
            base.OnDeactivate();
        }

        protected override void OnFinalize()
        {
            if (_layer != null && _movie != null)
            {
                _layer.ReleaseMovie(_movie);
                _movie = null;
            }
            if (_layer != null)
            {
                _layer.InputRestrictions.ResetInputRestrictions();
                RemoveLayer(_layer);
                _layer = null;
            }
            _vm = null;
            base.OnFinalize();
        }
    }

}

