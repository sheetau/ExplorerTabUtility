using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public static class SettingsManager
{
    private static readonly object SaveLock = new();
    private static readonly string DirectoryPath = Environment.GetEnvironmentVariable("EXPLORER_TAB_UTILITY_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Constants.AppName);
    private static readonly string SettingsFilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly AppSettings Settings = Load();
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;
    public static event Action<string>? SaveFailed;

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return new();
            var json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            using var legacy = JsonDocument.Parse(json);
            if (!legacy.RootElement.TryGetProperty("WindowWidth", out _) && legacy.RootElement.TryGetProperty("FormSize", out var size))
            {
                if (size.TryGetProperty("Width", out var width) && width.TryGetInt32(out var w)) settings.WindowWidth = Math.Max(680, w);
                if (size.TryGetProperty("Height", out var height) && height.TryGetInt32(out var h)) settings.WindowHeight = Math.Max(600, h);
            }
            return settings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Settings could not be loaded: {ex.Message}");
            return new();
        }
    }

    private static void Changed([CallerMemberName] string name = "")
    {
        SaveSettings();
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(name));
    }
    public static bool IsMouseHookActive { get => Settings.MouseHook; set { if (Settings.MouseHook == value) return; Settings.MouseHook = value; Changed(); } }
    public static bool IsKeyboardHookActive { get => Settings.KeyboardHook; set { if (Settings.KeyboardHook == value) return; Settings.KeyboardHook = value; Changed(); } }
    public static bool IsWindowHookActive { get => Settings.WindowHook; set { if (Settings.WindowHook == value) return; Settings.WindowHook = value; Changed(); } }
    public static bool ReuseTabs { get => Settings.ReuseTabs; set { if (Settings.ReuseTabs == value) return; Settings.ReuseTabs = value; Changed(); } }
    public static string HotKeyProfiles { get => Settings.HotKeyProfiles; set { Settings.HotKeyProfiles = value; Changed(); } }
    public static int WindowWidth => Settings.WindowWidth;
    public static int WindowHeight => Settings.WindowHeight;
    public static void SetWindowSize(int width, int height)
    {
        if (Settings.WindowWidth == width && Settings.WindowHeight == height) return;
        Settings.WindowWidth = width;
        Settings.WindowHeight = height;
        SaveSettings();
    }
    public static bool IsFirstRun { get => Settings.IsFirstRun; set { Settings.IsFirstRun = value; Changed(); } }
    public static bool IsTrayIconHidden { get => Settings.IsTrayIconHidden; set { if (Settings.IsTrayIconHidden == value) return; Settings.IsTrayIconHidden = value; Changed(); } }
    public static bool AutoUpdate { get => Settings.AutoUpdate; set { if (Settings.AutoUpdate == value) return; Settings.AutoUpdate = value; Changed(); } }
    public static bool SaveClosedHistory { get => Settings.SaveClosedWindows; set { if (Settings.SaveClosedWindows == value) return; Settings.SaveClosedWindows = value; Changed(); } }
    public static bool RestorePreviousWindows { get => Settings.RestorePreviousWindows; set { if (Settings.RestorePreviousWindows == value) return; Settings.RestorePreviousWindows = value; Changed(); } }
    public static WindowRecord[]? ClosedWindows { get => Settings.ClosedWindows; set { Settings.ClosedWindows = value; Changed(); } }

    public static void SaveSettings()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var pending = SettingsFilePath + ".tmp";
                File.WriteAllText(pending, JsonSerializer.Serialize(Settings), new UTF8Encoding(false));
                File.Move(pending, SettingsFilePath, overwrite: true);
            }
            catch (Exception ex) { SaveFailed?.Invoke(ex.Message); }
        }
    }
}

internal sealed class AppSettings
{
    public bool MouseHook { get; set; }
    public bool KeyboardHook { get; set; } = true;
    public bool WindowHook { get; set; } = true;
    public bool ReuseTabs { get; set; } = true;
    public int WindowWidth { get; set; } = 900;
    public int WindowHeight { get; set; } = 820;
    public bool IsFirstRun { get; set; } = true;
    public bool IsTrayIconHidden { get; set; }
    public bool AutoUpdate { get; set; }
    public string HotKeyProfiles { get; set; } = Constants.DefaultHotKeyProfiles;
    public bool SaveClosedWindows { get; set; }
    public bool RestorePreviousWindows { get; set; }
    public WindowRecord[]? ClosedWindows { get; set; }
}
