using System;
using System.Linq;
using System.Collections.Generic;
using ExplorerTabUtility.Interop;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using H.Hooks;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Hooks;

/// <summary>
/// Routes a positively identified Explorer jump-list launch click directly to a
/// tab. An unknown menu/layout is left to Windows and the normal window hook.
/// </summary>
internal sealed class TaskbarLaunchHook : IDisposable
{
    private sealed class LaunchItem(nint menu, ScreenBounds bounds, int generation, string? location = null)
    {
        public nint Menu { get; } = menu;
        public ScreenBounds Bounds { get; } = bounds;
        public int Generation { get; } = generation;
        public string? Location { get; } = location;
    }

    private readonly UiDispatcher _dispatcher;
    private readonly Func<bool> _canLaunch;
    private readonly Func<string?, Task> _launch;
    private readonly LowLevelMouseHook _mouse = new() { Handling = true };
    private readonly WinEventDelegate _foregroundHandler;
    private nint _foregroundHook;
    private LaunchItem[]? _candidates;
    private LaunchItem? _pressed;
    private int _generation;
    private volatile bool _started;
    private int _discovering;
    private bool _disposed;

    public TaskbarLaunchHook(UiDispatcher dispatcher, Func<bool> canLaunch, Func<string?, Task> launch)
    {
        _dispatcher = dispatcher;
        _canLaunch = canLaunch;
        _launch = launch;
        _foregroundHandler = OnForeground;
        _mouse.Down += OnMouseDown;
        _mouse.Up += OnMouseUp;
        _mouse.Wheel += OnMouseWheel;
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _foregroundHook = WinApi.SetWinEventHook(3, 3, 0, _foregroundHandler, 0, 0, 2);
        if (_foregroundHook == 0) return;
        try
        {
            _mouse.Start();
            _started = true;
        }
        catch (Exception ex)
        {
            WinApi.UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
            Trace.WriteLine($"Taskbar launch hook unavailable: {ex.GetType().Name}");
        }
    }

    public void Stop()
    {
        _started = false;
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _candidates, null);
        Interlocked.Exchange(ref _pressed, null);
        if (_foregroundHook != 0) WinApi.UnhookWinEvent(_foregroundHook);
        _foregroundHook = 0;
        _mouse.Stop();
    }

    private void OnForeground(nint hook, uint kind, nint menu, int obj, int child, uint thread, uint time)
    {
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _candidates, null);
        if (!_started || menu == 0 || !WinApi.IsWindowHasClassName(menu, "Windows.UI.Core.CoreWindow")) return;
        // UI Automation may call across processes. Never do that in a mouse hook
        // callback or on the Shell COM owner thread.
        if (Interlocked.CompareExchange(ref _discovering, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await Discover(menu, generation); }
            catch (Exception ex) { Trace.WriteLine($"Taskbar menu discovery unavailable: {ex.GetType().Name}"); }
            finally { Interlocked.Exchange(ref _discovering, 0); }
        });
    }

    private async Task Discover(nint menu, int generation)
    {
        WinApi.GetWindowThreadProcessId(menu, out var processId);
        using var process = Process.GetProcessById((int)processId);
        if (!string.Equals(process.ProcessName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase)) return;

        using var automation = new NativeAutomation();
        using var root = automation.FromHandle(menu);
        using var list = root.ChildById("SystemItemList");
        if (list == null || list.ControlType != 50008) return;
        using var item = list.ChildById("Open");
        if (item == null || item.ControlType != 50007 || !item.IsEnabled || item.IsOffscreen ||
            (item.Name != "File Explorer" && item.Name != "エクスプローラー") || !item.CanInvoke) return;
        var bounds = item.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        if (!_started || generation != Volatile.Read(ref _generation) || WinApi.GetForegroundWindow() != menu) return;
        var candidates = new List<LaunchItem> { new(menu, bounds, generation) };
        Volatile.Write(ref _candidates, candidates.ToArray());
        Trace.WriteLine("Taskbar Explorer launch item identified.");

        // Shell automation has a temporary STA owner, separate from the UI
        // Automation worker and from the main Shell event dispatcher.
        Dictionary<string, HashSet<string>> folders;
        using (var sta = new StaTaskScheduler())
            folders = await Task.Factory.StartNew(PinnedFolderReader.Read, CancellationToken.None, TaskCreationOptions.None, sta);
        if (!_started || generation != Volatile.Read(ref _generation) || WinApi.GetForegroundWindow() != menu) return;
        foreach (var group in root.FindAll(4, 50026))
        {
            using var groupOwner = group;
            if (group.ClassName != "ListViewHeaderItem" || group.Name is not ("Pinned" or "ピン留め済み" or "ピン留め")) continue;
            foreach (var row in group.FindAll(2, 50007))
            {
                using var rowOwner = row;
                if (!row.IsEnabled || row.IsOffscreen ||
                    !folders.TryGetValue(row.Name, out var paths) || paths.Count != 1 || !row.CanInvoke) continue;
                var hit = row.Bounds;
                if (hit.IsEmpty || hit.Height <= 0 || hit.Width <= 2 * hit.Height) continue;
                // The trailing area contains the pin/unpin affordance, which
                // may only materialize on hover. Always leave it to Windows.
                hit.Width -= 2 * hit.Height;
                candidates.Add(new LaunchItem(menu, hit, generation, paths.Single()));
            }
        }
        if (!_started || generation != Volatile.Read(ref _generation) || WinApi.GetForegroundWindow() != menu) return;
        Volatile.Write(ref _candidates, candidates.ToArray());
        Trace.WriteLine($"Taskbar pinned folder items identified: {candidates.Count - 1}.");
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        var candidates = Volatile.Read(ref _candidates);
        var candidate = candidates?.FirstOrDefault(item => item.Bounds.Contains(e.Position.X, e.Position.Y));
        if (e.CurrentKey is not (Key.MouseLeft or Key.LButton) || candidate == null || HasModifier())
        {
            // A pin/unpin or context-menu click can change the remaining rows.
            // Discard the snapshot before allowing such native interaction.
            InvalidateSnapshot();
            return;
        }
        if (!_started || candidate == null || !_canLaunch() ||
            candidate.Generation != Volatile.Read(ref _generation) ||
            WinApi.GetForegroundWindow() != candidate.Menu ||
            !candidate.Bounds.Contains(e.Position.X, e.Position.Y) || HasModifier()) return;
        // Only cached geometry/native state is checked in the input callback.
        Interlocked.Exchange(ref _pressed, candidate);
        e.IsHandled = true;
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.CurrentKey is not (Key.MouseLeft or Key.LButton)) return;
        var pressed = Interlocked.Exchange(ref _pressed, null);
        if (pressed == null) return;
        e.IsHandled = true; // Suppress the matching release even on a canceled drag.
        if (!_started || !_canLaunch() || pressed.Generation != Volatile.Read(ref _generation) ||
            WinApi.GetForegroundWindow() != pressed.Menu ||
            !pressed.Bounds.Contains(e.Position.X, e.Position.Y) || HasModifier()) return;
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!_started || !_canLaunch() || WinApi.GetForegroundWindow() != pressed.Menu) return;
            try
            {
                var elapsed = Stopwatch.StartNew();
                KeyboardSimulator.SendKeyPress(VirtualKey.Escape);
                Trace.WriteLine(pressed.Location == null ? "Taskbar Explorer launch intercepted; opening tab directly." : "Taskbar pinned folder intercepted; opening tab directly.");
                await _launch(pressed.Location);
                Trace.WriteLine($"Taskbar direct launch returned in {elapsed.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex) { Trace.WriteLine($"Taskbar direct launch failed: {ex.GetType().Name}"); }
        }));
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        // Scrolling changes the row coordinates. A subsequent native click is
        // preferable to using a stale location snapshot; reopening rebuilds it.
        InvalidateSnapshot();
    }

    private void InvalidateSnapshot()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _candidates, null);
    }

    private static bool HasModifier()
    {
        foreach (var key in KeyboardSimulator.ModifierKeys)
            if (KeyboardSimulator.IsKeyPressed((int)key)) return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _mouse.Dispose();
    }
}
