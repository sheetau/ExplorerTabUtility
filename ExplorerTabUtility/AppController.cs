using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
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
    private bool _installing;
    private string? _notified;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicTimer _updateTimer = new(TimeSpan.FromHours(12));

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
        _ = WatchUpdates();
    }
    public void ShowSettings() => _window.ShowSettings();
    private void ToggleWindowHook() => SettingsManager.IsWindowHookActive = !SettingsManager.IsWindowHookActive;
    private void ToggleReuse() => SettingsManager.ReuseTabs = !SettingsManager.ReuseTabs;
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        _hooks.ApplySettings();
        _tray.SetVisible(!SettingsManager.IsTrayIconHidden);
        if (args.PropertyName == nameof(SettingsManager.AutoUpdate) && SettingsManager.AutoUpdate) _ = CheckUpdates(false);
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
        if (_checking || _installing) return;
        _checking = true;
        try
        {
            var result = await UpdateManager.CheckAsync(_lifetime.Token);
            if (_disposed) return;
            if (result.Update is { } update)
            {
                if (update.CanInstall) _window.ShowUpdate(result.Message, () => _ = InstallUpdate(update));
                else _window.ShowStatus("Updates", result.Message, InfoBarSeverity.Informational, update.Page);
                if (!manual && _notified != update.Version.ToString())
                {
                    _notified = update.Version.ToString();
                    _tray.NotifyUpdate(_notified);
                }
            }
            else if (manual) _window.ShowStatus("Updates", result.Message);
            if (manual) ShowSettings();
        }
        catch (Exception ex)
        {
            if (manual && !_disposed) { _window.ShowStatus("Could not check for updates", ex.Message, InfoBarSeverity.Warning); ShowSettings(); }
        }
        finally { _checking = false; }
    }
    private async Task WatchUpdates()
    {
        try
        {
            while (await _updateTimer.WaitForNextTickAsync(_lifetime.Token))
                if (SettingsManager.AutoUpdate) await CheckUpdates(false);
        }
        catch (OperationCanceledException) { }
    }
    private async Task InstallUpdate(ReleaseUpdate release)
    {
        if (_installing || _disposed) return;
        _installing = true;
        try
        {
            _window.ShowStatus("Updating", "Downloading the update…");
            var percentage = -1;
            var progress = new Progress<double>(value =>
            {
                var next = (int)(value * 100);
                if (!_disposed && next != percentage) { percentage = next; _window.ShowStatus("Updating", $"Downloading the update… {next}%"); }
            });
            var update = await UpdateManager.DownloadAsync(release, progress, _lifetime.Token);
            _window.ShowStatus("Updating", "Applying the update and restarting…");
            await UpdateInstaller.StartHelperAsync(update);
            _exit();
        }
        catch (Exception ex)
        {
            if (!_disposed) _window.ShowStatus("Update could not be installed", ex.Message + " You can also download the release manually.", InfoBarSeverity.Error, release.Page);
        }
        finally { _installing = false; }
    }
    public void ShowUpdateResult(string? error = null)
    {
        _window.ShowStatus(error == null ? "Updated" : "Update could not be installed", error ?? $"Version {UpdateManager.CurrentVersion.ToString(3)} is ready.", error == null ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        ShowSettings();
    }
    public async Task CompleteUpdate(string work)
    {
        ShowUpdateResult();
        await _window.Ready;
        UpdateInstaller.Acknowledge(work);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _updateTimer.Dispose();
        SettingsManager.StaticPropertyChanged -= SettingsChanged;
        SettingsManager.SaveFailed -= SaveFailed;
        _tray.Dispose();
        _hooks.Dispose();
        _window.ExitWindow();
    }
}
