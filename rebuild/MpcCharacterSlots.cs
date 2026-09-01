using System;
using System.IO;
using System.Text;

namespace MultiplayerCampaign
{
    internal static class MpcCharacterSlots
    {
        private const int SlotCount = 3;
        private static readonly object Sync = new object();
        private static readonly string[] Names = new string[SlotCount];
        private static bool _loaded;
        private static int _selectedSlot = -1;

        private static string FilePath
        {
            get
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string directory = Path.Combine(documents, "Mount and Blade II Bannerlord", "Game Saves");
                return Path.Combine(directory, "MultiplayerCampaignCharacterSlots.txt");
            }
        }

        public static int SelectedSlot
        {
            get
            {
                lock (Sync)
                {
                    EnsureLoaded();
                    return _selectedSlot;
                }
            }
        }

        public static bool HasSelectedCharacter
        {
            get
            {
                lock (Sync)
                {
                    EnsureLoaded();
                    return _selectedSlot >= 0 &&
                           _selectedSlot < SlotCount &&
                           !string.IsNullOrWhiteSpace(Names[_selectedSlot]);
                }
            }
        }

        public static string GetName(int slot)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (slot < 0 || slot >= SlotCount)
                    return "Empty Slot";
                return string.IsNullOrWhiteSpace(Names[slot]) ? "Empty Slot" : Names[slot];
            }
        }

        public static void Select(int slot)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (slot < 0 || slot >= SlotCount)
                    return;
                _selectedSlot = slot;
            }
        }

        public static bool SaveSelected(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            name = name.Trim();
            if (name.Length > 32)
                name = name.Substring(0, 32);

            lock (Sync)
            {
                EnsureLoaded();
                if (_selectedSlot < 0 || _selectedSlot >= SlotCount)
                    return false;

                Names[_selectedSlot] = name;
                SaveToDisk();
                return true;
            }
        }

        public static void EnsureSelectedFromExisting()
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (_selectedSlot >= 0 && _selectedSlot < SlotCount &&
                    !string.IsNullOrWhiteSpace(Names[_selectedSlot]))
                    return;

                for (int i = 0; i < SlotCount; i++)
                {
                    if (!string.IsNullOrWhiteSpace(Names[i]))
                    {
                        _selectedSlot = i;
                        return;
                    }
                }
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                    return;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < SlotCount && i < lines.Length; i++)
                {
                    Names[i] = string.IsNullOrWhiteSpace(lines[i]) ? null : lines[i].Trim();
                }
            }
            catch
            {
                for (int i = 0; i < SlotCount; i++)
                    Names[i] = null;
            }
        }

        private static void SaveToDisk()
        {
            try
            {
                string path = FilePath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllLines(
                    path,
                    new[]
                    {
                        Names[0] ?? string.Empty,
                        Names[1] ?? string.Empty,
                        Names[2] ?? string.Empty
                    },
                    Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
