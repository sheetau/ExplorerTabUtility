using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;

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
        _mutex = new Mutex(true, Constants.MutexId, out var firstInstance);
        if (!firstInstance)
        {
            NativeTrayIcon.ShowExistingSettings();
            _mutex.Dispose();
            Exit();
            return;
        }
        UiDispatcher.Initialize(DispatcherQueue.GetForCurrentThread());
        _controller = new AppController(() => { _controller?.Dispose(); _mutex?.Dispose(); Exit(); });
        if (Managers.SettingsManager.IsFirstRun || Environment.GetCommandLineArgs().Contains("--settings"))
        {
            Managers.SettingsManager.IsFirstRun = false;
            _controller.ShowSettings();
        }
    }
}
