using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Hooks;
using ExplorerTabUtility.WinAPI;
using ExplorerTabUtility.Managers;

// Opt-in, real Explorer acceptance tests. Uses its own temporary folders/window.
// Stop another running utility before invoking; restore it after the test run.
internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bind, out nint pidl, uint mask, out uint attributes);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint[] items, uint flags);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ExplorerTabUtility-tests-" + Guid.NewGuid().ToString("N"));
    private static dynamic _shell = null!;
    private static ExplorerWatcher? _watcher;
    private static nint _target;
    private static int _failures;
    private static bool _manualTaskbar;
    private static bool _smoke;
    private static string? _expectedPinned;
    private sealed class LaunchCompletionListener(TaskCompletionSource<bool> completion) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message?.StartsWith("Taskbar direct launch returned in ", StringComparison.Ordinal) == true)
                completion.TrySetResult(true);
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        _smoke = args.Contains("--smoke");
        _manualTaskbar = args.Contains("--taskbar");
        var pinnedIndex = Array.IndexOf(args, "--taskbar-pinned");
        if (pinnedIndex >= 0)
        {
            if (pinnedIndex + 1 >= args.Length) { Console.Error.WriteLine("--taskbar-pinned requires the expected folder path."); return 2; }
            _expectedPinned = args[pinnedIndex + 1];
            _manualTaskbar = true;
        }
        if (Process.GetProcessesByName("ExplorerTabUtility").Length != 0)
        {
            Console.Error.WriteLine("Stop the running ExplorerTabUtility before executing these integration tests.");
            return 2;
        }
        Environment.SetEnvironmentVariable("EXPLORER_TAB_UTILITY_DATA_DIR", Path.Combine(Root, "settings"));
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try { Application.Start(_ =>
        {
            UiDispatcher.Initialize(DispatcherQueue.GetForCurrentThread());
            new ExplorerTabUtility.IntegrationTests.TestApp();
        }); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        Console.WriteLine($"Failures: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    internal static async Task RunAll()
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        try { await Run(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); _failures++; }
        finally
        {
            _watcher?.Dispose();
            var fixtures = new System.Collections.Generic.List<object>();
            if (_shell != null)
                foreach (dynamic window in _shell.Windows())
                {
                    try { if (Uri.UnescapeDataString((string)window.LocationURL).Contains(Path.GetFileName(Root))) fixtures.Add(window); }
                    catch (COMException) { }
                }
            foreach (dynamic window in fixtures)
                try { window.Quit(); } catch (COMException) { }
            Application.Current.Exit();
        }
    }

    private static async Task Run()
    {
        _shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        SettingsManager.RestorePreviousWindows = false;
        // Exercise the new single-source profile store without touching user settings.
        var manager = new ProfileManager();
        var profile = manager.Add();
        profile.Name = "Autosave check";
        profile.HotKeys = new[] { H.Hooks.Key.Ctrl, H.Hooks.Key.F12 };
        manager.Save();
        var reloaded = new ProfileManager();
        if (!reloaded.Profiles.Any(item => item.Id == profile.Id && item.Name == profile.Name && item.HotKeys?.Length == 2))
            throw new Exception("Shortcut edits were not persisted immediately.");
        manager.Remove(profile);
        if (new ProfileManager().Profiles.Any(item => item.Id == profile.Id))
            throw new Exception("Shortcut removal was not persisted immediately.");
        Console.WriteLine("PASS shortcut add/edit/remove autosave and stable IDs");
        SettingsManager.ClosedWindows = Array.Empty<ExplorerTabUtility.Models.WindowRecord>();
        var anchor = Path.Combine(Root, "anchor");
        var folder = Path.Combine(Root, "日本語 space # folder");
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "subfolder"));
        File.WriteAllText(Path.Combine(folder, "sample.txt"), "text");
        File.WriteAllText(Path.Combine(folder, "sample.md"), "markdown");
        _shell.ShellExecute(anchor, "", "", "opennewwindow", 1);
        await Until(() => Find(anchor) != null, "anchor window");
        _target = (nint)(int)Find(anchor)!.HWND;

        var initialized = new TaskCompletionSource<bool>();
        _watcher = new ExplorerWatcher();
        _watcher.OnShellInitialized += () => initialized.TrySetResult(true);
        if (await Task.WhenAny(initialized.Task, Task.Delay(5000)) != initialized.Task) throw new TimeoutException("Shell initialization");
        _watcher.SetTargetWindow(_target);
        _watcher.SetReuseTabs(true);
        _watcher.StartHook();

        if (_manualTaskbar)
        {
            await TaskbarLaunch();
            return;
        }

        // A known folder-only request can return as soon as navigation is issued.
        var direct = Path.Combine(Root, "direct folder open");
        Directory.CreateDirectory(direct);
        var directElapsed = Stopwatch.StartNew();
        await _watcher.SwitchTo(direct, _target);
        Console.WriteLine($"Folder-only request returned in {directElapsed.ElapsedMilliseconds} ms");
        await Until(() => IsInTarget(direct) && WinApi.GetForegroundWindow() == _target, "direct folder tab is presented");
        if (TabCount(direct) != 1) throw new Exception("Direct folder open created duplicate tabs.");
        Console.WriteLine("PASS known folder-only request");

        await Reveal("/select new tab", folder, new[] { "sample.txt" }, () => SelectCommand(Path.Combine(folder, "sample.txt")));
        await Reveal("/select reused tab, same basename", folder, new[] { "sample.md" }, () => SelectCommand(Path.Combine(folder, "sample.md")));
        await Reveal("Shell API multi-selection", folder, new[] { "sample.txt", "sample.md" }, () => SelectNative(folder, "sample.txt", "sample.md"));
        if (_smoke) return;
        await Reveal("Shell API folder selection", folder, new[] { "subfolder" }, () => SelectNative(folder, "subfolder"));

        var apiFolder = Path.Combine(Root, "native reveal");
        Directory.CreateDirectory(apiFolder);
        File.WriteAllText(Path.Combine(apiFolder, "native.txt"), "native");
        await Reveal("Shell API new tab", apiFolder, new[] { "native.txt" }, () => SelectNative(apiFolder, "native.txt"));

        var plain = Path.Combine(Root, "ordinary");
        Directory.CreateDirectory(plain);
        var elapsed = Stopwatch.StartNew();
        Process.Start("explorer.exe", "\"" + plain + "\"");
        await Until(() => IsInTarget(plain) && FixtureWindowCount() == 1, "ordinary folder merged");
        Console.WriteLine($"PASS ordinary folder: {elapsed.ElapsedMilliseconds} ms (native navigation included)");
        await Reveal("reuse inactive tab", folder, new[] { "sample.txt" }, () => SelectNative(folder, "sample.txt"));
        await _watcher.SwitchTo(plain, _target);
        await Reveal("same selection in inactive tab", folder, new[] { "sample.txt" }, () => SelectNative(folder, "sample.txt"));

        // Newly completed tabs are reusable immediately, without a two-second exclusion.
        var quick = Path.Combine(Root, "quick reuse");
        Directory.CreateDirectory(quick);
        File.WriteAllText(Path.Combine(quick, "a.txt"), "a");
        File.WriteAllText(Path.Combine(quick, "b.txt"), "b");
        await Reveal("quick first reveal", quick, new[] { "a.txt" }, () => SelectNative(quick, "a.txt"));
        await Reveal("quick second reveal", quick, new[] { "b.txt" }, () => SelectNative(quick, "b.txt"));
        if (TabCount(quick) != 1) throw new Exception("A freshly completed tab was duplicated.");

        _watcher.SetReuseTabs(false);
        var before = TabCount(folder);
        await Reveal("reuse disabled creates tab", folder, new[] { "sample.md" }, () => SelectCommand(Path.Combine(folder, "sample.md")));
        if (TabCount(folder) != before + 1) throw new Exception("Expected an additional tab with reuse disabled.");

        var interrupted = Path.Combine(Root, "shutdown during conversion");
        Directory.CreateDirectory(interrupted);
        File.WriteAllText(Path.Combine(interrupted, "pending.txt"), "pending");
        SelectCommand(Path.Combine(interrupted, "pending.txt"));
        await Until(() => FixtureWindowCount() >= 2, "native source for shutdown test");
        _watcher.Dispose();
        _watcher = null;
        await Until(() =>
        {
            var source = Find(interrupted);
            return source != null && IsWindowVisible((nint)(int)source!.HWND);
        }, "native source is visible after cancellation");
        Console.WriteLine("PASS shutdown during conversion preserves native window");
    }

    private static async Task TaskbarLaunch()
    {
        _watcher!.SetReuseTabs(false);
        var before = ExplorerTabUtility.Helpers.Helper.GetAllExplorerTabs(_target).Count();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LaunchCompletionListener(completion);
        Trace.Listeners.Add(listener);
        using var comparer = new ExplorerTabUtility.Interop.ShellPathComparer();
        var defaultLocation = _expectedPinned ?? ExplorerTabUtility.Helpers.Helper.GetDefaultExplorerLocation();
        bool IsDefaultTabActive()
        {
            foreach (dynamic window in _shell.Windows())
            {
                try
                {
                    if ((nint)(int)window.HWND != _target) continue;
                    var provider = (ExplorerTabUtility.Interop.IServiceProvider)(object)window;
                    var guid = typeof(ExplorerTabUtility.Interop.IShellBrowser).GUID;
                    provider.QueryService(ref guid, ref guid, out var browser);
                    if (browser == null) continue;
                    nint tab;
                    try { browser.GetWindow(out tab); }
                    finally { Marshal.ReleaseComObject(browser); }
                    if (tab != WinApi.FindWindowEx(_target, 0, "ShellTabWindowClass", null)) continue;
                    var location = (string)window.LocationURL;
                    if (string.IsNullOrWhiteSpace(location)) location = (string)window.Document.Folder.Self.Path;
                    return comparer.IsEquivalent(defaultLocation, ExplorerTabUtility.Helpers.Helper.NormalizeLocation(location));
                }
                catch (COMException) { }
            }
            return false;
        }
        var windowsBefore = new System.Collections.Generic.HashSet<nint>(ExplorerTabUtility.Helpers.Helper.GetAllExplorerWindows());
        var created = new System.Collections.Generic.HashSet<nint>();
        ExplorerTabUtility.WinAPI.WinEventDelegate observed = (nint _, uint _, nint window, int obj, int child, uint _, uint _) =>
        {
            if (obj == 0 && child == 0 && !windowsBefore.Contains(window) && WinApi.IsWindowHasClassName(window, "CabinetWClass"))
                created.Add(window);
        };
        var hook = WinApi.SetWinEventHook(0x8000, 0x8000, 0, observed, 0, 0, 2);
        if (hook == 0) throw new Exception("Cannot observe native window creation.");
        try
        {
            Console.WriteLine(_expectedPinned == null
                ? "READY: close any open jump list, right-click the Explorer taskbar icon, then click File Explorer once."
                : "READY: close any open jump list, right-click the Explorer taskbar icon, then click the expected pinned folder once.");
            Console.Out.Flush();
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 120000)
            {
                if (completion.Task.IsCompleted && ExplorerTabUtility.Helpers.Helper.GetAllExplorerTabs(_target).Count() > before && WinApi.GetForegroundWindow() == _target && IsDefaultTabActive())
                {
                    // Let already queued WinEvents arrive before asserting no source was created.
                    var queued = new TaskCompletionSource<bool>();
                    DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => queued.TrySetResult(true));
                    await queued.Task;
                    if (created.Count != 0) throw new Exception("Taskbar launch used a native source window instead of the direct route.");
                    Console.WriteLine(_expectedPinned == null
                        ? "PASS taskbar launch: active target gained the default folder tab, zero native source windows created."
                        : "PASS pinned folder: active target gained the expected folder tab, zero native source windows created.");
                    return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Manual taskbar launch did not arrive.");
        }
        finally
        {
            WinApi.UnhookWinEvent(hook);
            Trace.Listeners.Remove(listener);
            GC.KeepAlive(observed);
        }
    }

    private static void SelectCommand(string file) => Process.Start("explorer.exe", "/select,\"" + file + "\"");

    private static void SelectNative(string folder, params string[] names)
    {
        var pidls = new nint[names.Length];
        nint parent = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(folder, 0, out parent, 0, out _));
            for (var i = 0; i < names.Length; i++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.Combine(folder, names[i]), 0, out pidls[i], 0, out _));
            // Absolute item PIDLs are accepted when selecting multiple items in the folder.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(parent, (uint)pidls.Length, pidls.Select(LastId).ToArray(), 0));
        }
        finally
        {
            if (parent != 0) Marshal.FreeCoTaskMem(parent);
            foreach (var pidl in pidls) if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static nint LastId(nint pidl)
    {
        var current = pidl;
        while (true)
        {
            var next = current + (ushort)Marshal.ReadInt16(current);
            if (Marshal.ReadInt16(next) == 0) return current;
            current = next;
        }
    }

    private static async Task Reveal(string name, string folder, string[] names, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        await Until(() => HasSelection(folder, names) && FixtureWindowCount() == 1, name);
        Console.WriteLine($"PASS {name}: {watch.ElapsedMilliseconds} ms");
    }

    private static async Task Until(Func<bool> predicate, string description)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 15000)
        {
            try { if (predicate()) return; }
            catch (COMException) { }
            await Task.Delay(25); // Test observation only, not selection restoration.
        }
        Dump();
        throw new TimeoutException(description);
    }

    private static dynamic? Find(string folder)
    {
        foreach (dynamic window in _shell.Windows())
            try { if (string.Equals(new Uri((string)window.LocationURL).LocalPath.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)) return window; }
            catch (Exception) { }
        return null;
    }
    private static bool IsInTarget(string folder) => Find(folder) is { } window && (nint)(int)window.HWND == _target;
    private static int FixtureWindowCount()
    {
        var handles = new System.Collections.Generic.HashSet<int>();
        foreach (dynamic w in _shell.Windows())
            try { if (Uri.UnescapeDataString((string)w.LocationURL).Contains(Path.GetFileName(Root))) handles.Add((int)w.HWND); }
            catch (COMException) { }
        return handles.Count;
    }
    private static int TabCount(string folder)
    {
        var count = 0;
        foreach (dynamic w in _shell.Windows())
            try { if (string.Equals(new Uri((string)w.LocationURL).LocalPath.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)) count++; }
            catch (Exception) { }
        return count;
    }
    private static bool HasSelection(string folder, string[] names)
    {
        // Read the active tab's actual selected paths; do not infer success from a Navigate call.
        foreach (dynamic w in _shell.Windows())
        {
            try
            {
                if ((nint)(int)w.HWND != _target) continue;
                if (!Uri.TryCreate((string)w.LocationURL, UriKind.Absolute, out var uri) || !uri.IsFile || !string.Equals(uri.LocalPath.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)) continue;
                var provider = (ExplorerTabUtility.Interop.IServiceProvider)(object)w;
                var guid = typeof(ExplorerTabUtility.Interop.IShellBrowser).GUID;
                provider.QueryService(ref guid, ref guid, out var browser);
                if (browser == null) continue;
                nint tab;
                try { browser.GetWindow(out tab); }
                finally { Marshal.ReleaseComObject(browser); }
                if (tab != WinApi.FindWindowEx(_target, 0, "ShellTabWindowClass", null)) continue;
                dynamic items = w.Document.SelectedItems();
                var paths = new string[(int)items.Count];
                for (var i = 0; i < paths.Length; i++) paths[i] = (string)items.Item(i).Path;
                if (paths.Length == names.Length && names.All(n => paths.Contains(Path.Combine(folder, n), StringComparer.OrdinalIgnoreCase))) return true;
            }
            catch (COMException) { }
        }
        return false;
    }
    private static void Dump()
    {
        foreach (dynamic w in _shell.Windows())
            try { if (Uri.UnescapeDataString((string)w.LocationURL).Contains(Path.GetFileName(Root))) Console.WriteLine($"window={w.HWND} path={w.LocationURL} selected={w.Document.SelectedItems().Count}"); }
            catch (COMException) { }
    }
}
