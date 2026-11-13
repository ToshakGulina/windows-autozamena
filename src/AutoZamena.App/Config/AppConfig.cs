using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Config;

public enum WindowFilterMode
{
    Blacklist = 0,
    Whitelist = 1
}

public sealed class HotkeyBinding
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public int VirtualKey { get; set; }

    public HotkeyBinding()
    {
    }

    public HotkeyBinding(bool ctrl, bool alt, bool shift, int virtualKey)
    {
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
        VirtualKey = virtualKey;
    }

    public bool IsEmpty => VirtualKey <= 0;

    public bool Matches(int vk, bool ctrlDown, bool altDown, bool shiftDown)
    {
        if (VirtualKey <= 0)
        {
            return false;
        }

        return vk == VirtualKey && Ctrl == ctrlDown && Alt == altDown && Shift == shiftDown;
    }

    public string ToDisplayString()
    {
        if (VirtualKey <= 0)
        {
            return "Не назначено";
        }

        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(FormatVkName(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string FormatVkName(int vk)
    {
        return vk switch
        {
            0x13 => "Pause/Break",
            0x14 => "CapsLock",
            0x20 => "Space",
            0xC0 => "~ (`)",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x1B => "Esc",
            0x2D => "Insert",
            0x2E => "Delete",
            0x91 => "ScrollLock",
            >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            _ => ((System.Windows.Forms.Keys)vk).ToString()
        };
    }
}

public sealed class PersonalDictionaryBundle
{
    public List<string> IgnoredWords { get; set; } = new();
    public Dictionary<string, string> CustomReplacements { get; set; } = new();
}

public sealed class AppConfig
{
    private static readonly object SaveLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public int ConfigSchemaVersion { get; set; } = 2;

    public EngineSettings Engine { get; set; } = new();

    public WindowFilterMode FilterMode { get; set; } = WindowFilterMode.Blacklist;
    public bool AutoDisableInFullscreenGames { get; set; } = true;
    public bool AutoDisableInPasswordFields { get; set; } = true;

    public List<string> BlacklistedProcesses { get; set; } = CreateDefaultBlacklist();
    public List<string> WhitelistedProcesses { get; set; } = CreateDefaultWhitelist();

    // Caramba Switcher style modifier gestures (enabled out-of-the-box, no Pause/Break needed!)
    public bool UseDoubleShiftConvert { get; set; } = true;       // 2x Shift -> convert last word / cancel auto-switch / convert selected text
    public bool UseBothShiftsPause { get; set; } = true;          // Left Shift + Right Shift -> toggle Pause / Resume
    public bool UseSingleShiftLayoutSwitch { get; set; } = false; // 1x Shift -> switch active OS layout (optional Caramba toggle)
    public bool UseDoubleCtrlConvertLine { get; set; } = true;    // 2x Ctrl -> convert whole typed line without selection

    // Optional regular key bindings (no Pause/Break by default!)
    public HotkeyBinding PauseToggleHotkey { get; set; } = new(ctrl: true, alt: true, shift: false, virtualKey: 0x5A); // Ctrl+Alt+Z
    public HotkeyBinding ConvertLayoutHotkey { get; set; } = new(); // Empty by default (Double Shift is primary)
    public HotkeyBinding CycleCaseHotkey { get; set; } = new(ctrl: true, alt: true, shift: false, virtualKey: 0x4B);   // Ctrl+Alt+K
    public HotkeyBinding TransliterateHotkey { get; set; } = new(ctrl: true, alt: true, shift: false, virtualKey: 0x54); // Ctrl+Alt+T

    public List<string> IgnoredWords { get; set; } = new();
    public Dictionary<string, string> CustomReplacements { get; set; } = new();

    public bool ShowTrayNotifications { get; set; } = true;
    public bool PlaySoundOnAutoSwitch { get; set; } = false;
    public bool ShowLanguageBadgeInTray { get; set; } = true;

    public static List<string> CreateDefaultBlacklist() => new()
    {
        // Terminals & Shells
        "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost", "mintty", "alacritty", "wezterm-gui", "putty",
        // IDEs & Code Editors
        "Code", "Cursor", "Windsurf", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "goland64",
        "clion64", "phpstorm64", "sublime_text", "notepad++", "nvim-qt",
        // Password Managers & Remote Desktop
        "KeePass", "KeePassXC", "Bitwarden", "1Password", "mstsc", "AnyDesk", "TeamViewer", "regedit"
    };

    public static List<string> CreateDefaultWhitelist() => new()
    {
        "Telegram", "Discord", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "browser",
        "WINWORD", "Notion", "Obsidian", "Slack", "WhatsApp", "notepad"
    };

    public static string GetConfigFilePath()
    {
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string portablePath = Path.Combine(exeDir, "config.json");
            string portableFlag = Path.Combine(exeDir, "portable.flag");
            if (File.Exists(portablePath) || File.Exists(portableFlag))
            {
                return portablePath;
            }
        }
        catch
        {
            // Fallback to AppData
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "AutoZamena");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static AppConfig Load()
    {
        string path = GetConfigFilePath();
        AppConfig? loaded = TryReadSingleConfigFile(path) ?? TryReadSingleConfigFile(path + ".bak");
        if (loaded is not null)
        {
            return loaded;
        }

        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    private static AppConfig? TryReadSingleConfigFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath);
            AppConfig? loaded = ParseAndMigrateJson(json, out bool migrated);
            if (loaded is not null && migrated)
            {
                loaded.Save();
            }

            return loaded;
        }
        catch
        {
            return null;
        }
    }

    public static AppConfig? ParseAndMigrateJson(string json, out bool migrated)
    {
        migrated = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        if (loaded is null)
        {
            return null;
        }

        loaded.Engine ??= new EngineSettings();
        loaded.BlacklistedProcesses ??= CreateDefaultBlacklist();
        loaded.WhitelistedProcesses ??= CreateDefaultWhitelist();
        loaded.PauseToggleHotkey ??= new HotkeyBinding(true, true, false, 0x5A);
        loaded.ConvertLayoutHotkey ??= new HotkeyBinding();
        loaded.CycleCaseHotkey ??= new HotkeyBinding(true, true, false, 0x4B);
        loaded.TransliterateHotkey ??= new HotkeyBinding(true, true, false, 0x54);
        loaded.IgnoredWords ??= new List<string>();
        loaded.CustomReplacements ??= new Dictionary<string, string>();

        // Migrate pre-Caramba v1 config automatically: drop legacy Pause/Break (0x13) default
        if (!json.Contains("\"ConfigSchemaVersion\"", StringComparison.Ordinal))
        {
            migrated = true;
            loaded.ConfigSchemaVersion = 2;
            loaded.UseDoubleShiftConvert = true;
            loaded.UseBothShiftsPause = true;
            loaded.UseDoubleCtrlConvertLine = true;
            if (loaded.ConvertLayoutHotkey.VirtualKey == 0x13 &&
                !loaded.ConvertLayoutHotkey.Ctrl &&
                !loaded.ConvertLayoutHotkey.Alt &&
                !loaded.ConvertLayoutHotkey.Shift)
            {
                loaded.ConvertLayoutHotkey = new HotkeyBinding();
            }
        }

        return loaded;
    }

    /// <summary>
    /// Crash-safe atomic save via temporary file + backup + NTFS atomic rename.
    /// </summary>
    public void Save()
    {
        lock (SaveLock)
        {
            try
            {
                string path = GetConfigFilePath();
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmpPath = path + ".tmp";
                string bakPath = path + ".bak";
                string json = JsonSerializer.Serialize(this, JsonOptions);

                File.WriteAllText(tmpPath, json);
                if (File.Exists(path))
                {
                    File.Copy(path, bakPath, overwrite: true);
                }
                File.Move(tmpPath, path, overwrite: true);
            }
            catch
            {
                // Ignore transient IO failures
            }
        }
    }

    public static void ExportPersonalDictionary(string filePath, UserDictionary dictionary)
    {
        var bundle = new PersonalDictionaryBundle
        {
            IgnoredWords = dictionary.IgnoredWords.ToList(),
            CustomReplacements = new Dictionary<string, string>(dictionary.CustomReplacements, StringComparer.OrdinalIgnoreCase)
        };
        string json = JsonSerializer.Serialize(bundle, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public static (int AddedIgnored, int AddedSnippets) ImportPersonalDictionary(string filePath, UserDictionary dictionary)
    {
        string json = File.ReadAllText(filePath);
        PersonalDictionaryBundle? bundle = JsonSerializer.Deserialize<PersonalDictionaryBundle>(json, JsonOptions);
        if (bundle is null)
        {
            return (0, 0);
        }

        int addedIgnored = 0;
        if (bundle.IgnoredWords is not null)
        {
            foreach (string w in bundle.IgnoredWords)
            {
                if (dictionary.AddIgnoredWord(w))
                {
                    addedIgnored++;
                }
            }
        }

        int addedSnippets = 0;
        if (bundle.CustomReplacements is not null)
        {
            foreach (var kv in bundle.CustomReplacements)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    dictionary.SetCustomReplacement(kv.Key, kv.Value);
                    addedSnippets++;
                }
            }
        }

        return (addedIgnored, addedSnippets);
    }
}
