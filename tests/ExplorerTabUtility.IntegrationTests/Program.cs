using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplorerTabUtility", "settings.json");

    [STAThread]
    private static int Main()
    {
        if (Process.GetProcessesByName("ExplorerTabUtility").Length != 0)
        {
            Console.Error.WriteLine("Stop the running ExplorerTabUtility before executing these integration tests.");
            return 2;
        }
        var settingsBackup = File.Exists(SettingsPath) ? File.ReadAllBytes(SettingsPath) : null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Trace.Listeners.Add(new ConsoleTraceListener());
        app.Startup += async (_, _) =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            try { await Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); _failures++; }
            finally
            {
                try
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
                        try { window.Quit(); }
                        catch (COMException) { }
                }
                finally
                {
                    if (settingsBackup != null) File.WriteAllBytes(SettingsPath, settingsBackup);
                    else if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                    app.Shutdown();
                }
            }
        };
        app.Run();
        Console.WriteLine($"Failures: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task Run()
    {
        _shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        SettingsManager.RestorePreviousWindows = false;
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

        await Reveal("/select new tab", folder, new[] { "sample.txt" }, () => SelectCommand(Path.Combine(folder, "sample.txt")));
        await Reveal("/select reused tab, same basename", folder, new[] { "sample.md" }, () => SelectCommand(Path.Combine(folder, "sample.md")));
        await Reveal("Shell API multi-selection", folder, new[] { "sample.txt", "sample.md" }, () => SelectNative(folder, "sample.txt", "sample.md"));
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
