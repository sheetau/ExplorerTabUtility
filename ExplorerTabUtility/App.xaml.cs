using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;

namespace ExplorerTabUtility;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppController? _controller;
    public App()
    {
        var diagnosticPath = Environment.GetEnvironmentVariable("EXPLORER_TAB_UTILITY_DIAGNOSTICS");
        if (!string.IsNullOrEmpty(diagnosticPath))
        {
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(diagnosticPath));
            System.Diagnostics.Trace.AutoFlush = true;
        }
        UnhandledException += (_, args) =>
        {
            System.Diagnostics.Trace.WriteLine(args.Message);
            System.Diagnostics.Trace.WriteLine(args.Exception);
        };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var applyIndex = Array.IndexOf(arguments, "--apply-update");
        if (applyIndex >= 0)
        {
            _ = RunUpdateHelper(arguments.ElementAtOrDefault(applyIndex + 1));
            return;
        }
        _mutex = new Mutex(true, Constants.MutexId, out var firstInstance);
        if (!firstInstance)
        {
            NativeTrayIcon.SendInstanceCommand(arguments.Contains("--exit") ? TrayCommand.Exit : TrayCommand.Settings);
            _mutex.Dispose();
            Exit();
            return;
        }
        if (arguments.Contains("--exit")) { _mutex.Dispose(); Exit(); return; }
        UiDispatcher.Initialize(DispatcherQueue.GetForCurrentThread());
        _controller = new AppController(() => { _controller?.Dispose(); _mutex?.Dispose(); Exit(); });
        var updatedIndex = Array.IndexOf(arguments, "--updated");
        if (updatedIndex >= 0 && arguments.ElementAtOrDefault(updatedIndex + 1) is { } updatedWork)
        {
            _ = _controller.CompleteUpdate(updatedWork);
        }
        var errorIndex = Array.IndexOf(arguments, "--update-error");
        if (errorIndex >= 0 && arguments.ElementAtOrDefault(errorIndex + 1) is { } errorWork)
            _controller.ShowUpdateResult(UpdateInstaller.ReadError(errorWork));
        if (Managers.SettingsManager.IsFirstRun || Environment.GetCommandLineArgs().Contains("--settings"))
        {
            Managers.SettingsManager.IsFirstRun = false;
            _controller.ShowSettings();
        }
    }
    private async System.Threading.Tasks.Task RunUpdateHelper(string? request)
    {
        try { if (request != null) await UpdateInstaller.RunHelperAsync(request); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        finally { Exit(); }
    }
}
