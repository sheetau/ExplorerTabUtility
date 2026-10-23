using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.UI;

namespace ExplorerTabUtility;

internal sealed class AppController : IDisposable
{
    private readonly ProfileManager _profiles = new();
    private readonly HookManager _hooks;
    private readonly MainWindow _window;
    private readonly NativeTrayIcon _tray;
    private readonly Action _exit;
    private bool _checking;
    private bool _disposed;

    public AppController(Action exit)
    {
        _exit = exit;
        _hooks = new HookManager(_profiles);
        _window = new MainWindow(_profiles);
        _tray = new NativeTrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Icon.ico"), TrayAction, exit);
        _hooks.OnVisibilityToggled += ShowSettings;
        _hooks.OnWindowHookToggled += ToggleWindowHook;
        _hooks.OnReuseTabsToggled += ToggleReuse;
        SettingsManager.StaticPropertyChanged += SettingsChanged;
        SettingsManager.SaveFailed += SaveFailed;
        if (SettingsManager.AutoUpdate) _ = CheckUpdates(false);
    }
    public void ShowSettings() => _window.ShowSettings();
    private void ToggleWindowHook() => SettingsManager.IsWindowHookActive = !SettingsManager.IsWindowHookActive;
    private void ToggleReuse() => SettingsManager.ReuseTabs = !SettingsManager.ReuseTabs;
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        _hooks.ApplySettings();
        _tray.SetVisible(!SettingsManager.IsTrayIconHidden);
    }
    private void SaveFailed(string message) => UiDispatcher.Current.BeginInvoke(() => _window.ShowStatus("Changes could not be saved", message, InfoBarSeverity.Error));
    private void TrayAction(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Settings: ShowSettings(); break;
            case TrayCommand.WindowHook: ToggleWindowHook(); break;
            case TrayCommand.ReuseTabs: ToggleReuse(); break;
            case TrayCommand.KeyboardHook: SettingsManager.IsKeyboardHookActive = !SettingsManager.IsKeyboardHookActive; break;
            case TrayCommand.MouseHook: SettingsManager.IsMouseHookActive = !SettingsManager.IsMouseHookActive; break;
            case TrayCommand.Startup: RegistryManager.ToggleStartup(); _window.RefreshPreferences(); break;
            case TrayCommand.Updates: _ = CheckUpdates(true); break;
            case TrayCommand.Exit: _exit(); break;
        }
    }
    private async Task CheckUpdates(bool manual)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var result = await UpdateManager.CheckAsync();
            if (_disposed) return;
            if (manual || result.Available)
                _window.ShowStatus("Updates", result.Message, InfoBarSeverity.Informational, result.Link);
            if (manual) ShowSettings();
        }
        catch (Exception ex)
        {
            if (manual && !_disposed) { _window.ShowStatus("Could not check for updates", ex.Message, InfoBarSeverity.Warning); ShowSettings(); }
        }
        finally { _checking = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SettingsManager.StaticPropertyChanged -= SettingsChanged;
        SettingsManager.SaveFailed -= SaveFailed;
        _tray.Dispose();
        _hooks.Dispose();
        _window.ExitWindow();
    }
}
