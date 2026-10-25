using Shell32;
using SHDocVw;
using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Hooks;

using WindowEntry = DualKeyEntry<InternetExplorer, nint?, WindowInfo>;

public class ExplorerWatcher : IHook
{
    private readonly UiDispatcher _dispatcher = UiDispatcher.Current;
    private CancellationTokenSource _shellLifetime = new();
    private bool _disposed;
    private static bool _instanceRunning;
    private static Guid _shellBrowserGuid = typeof(IShellBrowser).GUID;

    private ShellWindows _shellWindows = null!;
    private ShellPathComparer _shellPathComparer = null!;
    private nint _mainWindowHandle;
    private readonly ConcurrentDictionary<nint, byte> _processedHWnds = new();
    private readonly DualKeyDictionary<InternetExplorer, nint?, WindowInfo> _windowEntryDict = [];
    private readonly List<WindowRecord> _closedWindows = new();
    private readonly object _windowEntryDictLock = new(), _closedWindowsLock = new(), _processLock = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1);
    private readonly ProcessWatcher _processWatcher;
    private int _mainExplorerProcessId;
    private Timer? _explorerCheckTimer;

    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private DShellWindowsEvents_WindowRegisteredEventHandler? _windowRegisteredHandler;

    private string _defaultLocation = null!;
    private bool _reuseTabs = true;
    private bool _isForcingTabs;
    private TaskbarLaunchHook? _taskbarLaunchHook;
    public bool IsHookActive => _isForcingTabs;
    public event Action? OnShellInitialized;

    public ExplorerWatcher()
    {
        if (_instanceRunning)
            throw new InvalidOperationException("Only one instance of ExplorerWatcher is allowed at a time.");
        _instanceRunning = true;

        _processWatcher = new ProcessWatcher("explorer");
        _processWatcher.ProcessTerminated += OnExplorerProcessTerminated;
        StartExplorerProcessCheck();
    }

    public void StartHook()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.Invoke(StartHook); return; }
        if (_isForcingTabs) return;
        _isForcingTabs = true;
        _taskbarLaunchHook?.Start();
    }

    public void StopHook()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.Invoke(StopHook); return; }
        if (!_isForcingTabs) return;
        _isForcingTabs = false;
        _taskbarLaunchHook?.Stop();
    }
    public void SetReuseTabs(bool reuseTabs) => _reuseTabs = reuseTabs;

    public void ClearClosedWindows()
    {
        lock (_closedWindowsLock)
            _closedWindows.Clear();
    }

    public IReadOnlyCollection<WindowRecord> GetWindows()
    {
        if (!_dispatcher.CheckAccess()) return _dispatcher.Invoke(() => GetWindows());
        var result = new List<WindowRecord>();
        
        // Add open windows
        lock (_windowEntryDictLock)
            result.AddRange(
                _windowEntryDict.Keys.Select(ie => new WindowRecord(GetLocation(ie), new IntPtr(ie.HWND), GetSelectedItems(ie), ie.LocationName)));
        
        // Add closed windows in reverse order (last closed on top)
        lock (_closedWindowsLock)
            result.AddRange(_closedWindows.AsEnumerable().Reverse());
        
        return result.GroupBy(w => w.Location).Select(g => g.First()).ToList();
    }

    public async Task SwitchTo(string location, nint windowHandle = 0, string[]? selectedItems = null, bool asTab = true, bool duplicate = false)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => SwitchTo(location, windowHandle, selectedItems, asTab, duplicate)).Task.Unwrap();
            return;
        }
        var windowToOpen = new WindowRecord(location, windowHandle, selectedItems);
        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowToOpen);
            return;
        }

        await OpenTabNavigateWithSelection(windowToOpen, windowHandle, duplicate, true);
    }
    
    public nint SearchForTab(string targetPath)
    {
        if (!_dispatcher.CheckAccess()) return _dispatcher.Invoke(() => SearchForTab(targetPath));
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            foreach (var (window, windowInfo, tabHandle) in _windowEntryDict)
            {
                // Make sure it is not the newly created window
                if (windowInfo.IsConversionSource || !tabHandle.HasValue || tabHandle.Value == 0)
                    continue;

                var comparePath = GetLocation(window);

                if (_shellPathComparer.IsEquivalent(targetPath, comparePath, targetPidl))
                    return tabHandle.Value;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    public async Task SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => SelectTabByHandle(windowHandle, tabHandle)).Task.Unwrap();
            return;
        }
        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length == 0) return;

        var activeTab = tabs[0];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            SelectTabByIndex(windowHandle, i);

            // ReSharper disable once AccessToModifiedClosure
            activeTab = await Helper.DoUntilConditionAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != activeTab);
        }
    }
    public void SelectLastTab(nint windowHandle)
    {
        var count = Helper.GetAllExplorerTabs(windowHandle).Count();
        SelectTabByIndex(windowHandle, count - 1);
    }
    public void SelectTabByIndex(nint windowHandle, int index)
    {
        // Send 0xA221 magic command (CTRL + 1...n)
        WinApi.SendMessage(windowHandle, WinApi.WM_COMMAND, 0xA221, index + 1);
    }
    public async Task RequestToOpenNewTab(nint windowHandle, bool bringToFront = false, bool lockToOpenWindows = true)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => RequestToOpenNewTab(windowHandle, bringToFront, lockToOpenWindows)).Task.Unwrap();
            return;
        }
        if (bringToFront && windowHandle == 0)
            windowHandle = GetMainWindowHWnd(0);

        if (windowHandle == 0)
        {
            await OpenNewWindowWithSelection(new WindowRecord(string.Empty), lockToOpenWindows);
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // Send 0xA21B magic command (CTRL + T)
        WinApi.PostMessage(tabHandle, WinApi.WM_COMMAND, 0xA21B, 0);

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }
    public async Task Open(string? location, bool asTab, nint windowHandle, int delay = 0)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => Open(location, asTab, windowHandle, delay)).Task.Unwrap();
            return;
        }
        if (delay > 0)
            await Task.Delay(delay);

        var normalizedPath = Helper.NormalizeLocation(location ?? string.Empty);
        
        if (normalizedPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            System.IO.File.Exists(normalizedPath))
        {
            try
            {
                Helper.BypassWinForegroundRestrictions();
                Process.Start(new ProcessStartInfo(normalizedPath) { UseShellExecute = true });
                return;
            }
            catch
            {
                //
            }
        }
        
        if (!asTab)
        {
            await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedPath) && !_reuseTabs)
        {
            await RequestToOpenNewTab(windowHandle, bringToFront: true);
            return;
        }

        if (_windowEntryDict.Count > 0)
        {
            OpenNewTab(windowHandle, normalizedPath);
            return;
        }

        await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
    }
    public void OpenNewTab(nint windowHandle, string location)
    {
        _ = OpenTabNavigateWithSelection(new WindowRecord(location, windowHandle), windowHandle);
    }
    public async Task DuplicateActiveTab(nint windowHandle, bool asTab)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => DuplicateActiveTab(windowHandle, asTab)).Task.Unwrap();
            return;
        }
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowRecord);
            return;
        }

        await OpenTabNavigateWithSelection(windowRecord, windowHandle, isDuplicate: true);
    }
    public async Task ReopenClosedTab(bool asTab, nint windowHandle = 0)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => ReopenClosedTab(asTab, windowHandle)).Task.Unwrap();
            return;
        }
        WindowRecord? closedWindow;
        lock (_closedWindowsLock)
        {
            closedWindow = _closedWindows.LastOrDefault(w => w.Location != _defaultLocation);
            if (closedWindow == null) return;
            _closedWindows.Remove(closedWindow);
        }

        if (!asTab)
        {
            closedWindow.CreatedAt = Environment.TickCount;
            await OpenNewWindowWithSelection(closedWindow);
            return;
        }

        await OpenTabNavigateWithSelection(closedWindow, windowHandle);
    }
    public async Task DetachCurrentTab(nint windowHandle)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => DetachCurrentTab(windowHandle)).Task.Unwrap();
            return;
        }
        if (Helper.GetAllExplorerTabs(windowHandle).Take(2).Count() < 2)
            return;

        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        // Send 0xA021 magic command (CTRL + W)
        WinApi.SendMessage(activeTabHandle, WinApi.WM_COMMAND, 0xA021, 1);

        await OpenNewWindowWithSelection(windowRecord);
    }
    public void SetTargetWindow(nint windowHandle)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.Invoke(() => SetTargetWindow(windowHandle)); return; }
        if (Helper.IsFileExplorerWindow(windowHandle))
            _mainWindowHandle = windowHandle;
    }
    public void NavigateBackForward(nint windowHandle, bool isForward)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.Invoke(() => NavigateBackForward(windowHandle, isForward)); return; }
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        try
        {
            if (isForward) window?.GoForward();
            else window?.GoBack();
        }
        catch
        {
            // Will throw if there is no further history
        }
    }

    private void PreventWindowHiding(nint hWnd)
    {
        if (_processedHWnds.TryAdd(hWnd, 0))
        {
            // Schedule removal after a short delay
            _ = Task.Delay(7_000).ContinueWith(t => _processedHWnds.TryRemove(hWnd, out _), TaskScheduler.Default);
        }
    }
    private void OnWindowShown(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dWmsEventTime)
    {
        if (!_isForcingTabs || idObject != 0 || idChild != 0) return;
        
        // Check if the hWnd was processed by OnShellWindowRegistered
        if (_processedHWnds.TryRemove(hWnd, out _)) return;
        
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        if (_windowEntryDict.Count < 2 || Helper.IsCtrlShiftDown()) return;
        Helper.HideWindow(hWnd);
    }
    private InternetExplorer? GetRecentlyCreatedWindow(out WindowInfo? windowInfo)
    {
        // When a new window is registered, it's typically the last in the collection
        var count = _shellWindows.Count;
        for (var i = count - 1; i >= 0; i--)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window) continue;

            lock (_windowEntryDictLock)
            {
                if (_windowEntryDict.Keys.Contains(window)) continue;
                if (window.GetProperty("seenBefore") is not null) continue;
                window.PutProperty("seenBefore", true);

                windowInfo = new WindowInfo { InitialViewReady = new ShellViewReady(window) };
                _windowEntryDict.Add(window, windowInfo);

                if (_windowEntryDict.Count == 1)
                {
                    _mainWindowHandle = new IntPtr(window.HWND);

                    if (SettingsManager.RestorePreviousWindows && _closedWindows.Any(w => w.Restore))
                        _ = RestorePreviousWindows();
                }
                
                return window;
            }
        }

        windowInfo = null;
        return null;
    }
    private async void OnShellWindowRegistered(int __)
    {
        if (_disposed) return;
        var cancellationToken = _shellLifetime.Token;
        var showAgain = true;
        nint hWnd = 0;
        InternetExplorer? sourceWindow = null;
        WindowInfo windowInfo = null!;
        try
        {
            var shouldOpenAsWindow = Helper.IsCtrlShiftDown();

            var window = await Helper.DoUntilNotDefaultAsync(() => cancellationToken.IsCancellationRequested ? null : GetRecentlyCreatedWindow(out windowInfo!), 2_500, 70, cancellationToken);
            if (window == null) return;
            sourceWindow = window;

            using var initialView = windowInfo.InitialViewReady;
            await GetTabHandle(window);
            cancellationToken.ThrowIfCancellationRequested();

            hWnd = new IntPtr(window.HWND);
            
            if (shouldOpenAsWindow)
            {
                PreventWindowHiding(hWnd);
                HookWindowEvents(window, windowInfo);
                return;
            }
            
            var location = GetLocation(window);

            //Control Panel
            if (location.StartsWith("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"))
            {
                PreventWindowHiding(hWnd);
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // Check if this is a single tab window and there are other windows
            var shouldReopenAsTab = (_isForcingTabs || _reuseTabs) &&
                                    _windowEntryDict.Count > 1 &&
                                    hWnd != _mainWindowHandle &&
                                    Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1;

            if (shouldReopenAsTab)
                Helper.HideWindow(hWnd);
            else
                PreventWindowHiding(hWnd);

            // Check if it is a detached tab
            cancellationToken.ThrowIfCancellationRequested();
            var isRecentlyClosed = TryGetRecentlyClosedWindow(location, out var closedWindow);
            if (isRecentlyClosed)
                SelectItems(window, closedWindow!.SelectedItems);

            shouldReopenAsTab = shouldReopenAsTab && !isRecentlyClosed;

            if (shouldReopenAsTab)
            {
                windowInfo.IsConversionSource = true;
                // /select and SHOpenFolderAndSelectItems are applied during the
                // source view's initial enumeration, after DocumentComplete.
                var request = new WindowRecord(location, hWnd);
                async Task CaptureSelection()
                {
                    await initialView!.WaitAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsAtLocation(window, request.Location))
                        throw new InvalidOperationException("The source navigated during tab conversion.");
                    request.SelectedItems = GetSelectedItems(window);
                }
                var capture = CaptureSelection();
                var opening = OpenTabNavigateWithSelection(request, _mainWindowHandle, selectionReady: capture);
                // Prepare the destination while the native source enumerates.
                await Task.WhenAll(capture, opening);
                cancellationToken.ThrowIfCancellationRequested();
                if (!await opening)
                {
                    windowInfo.IsConversionSource = false;
                    HookWindowEvents(window, windowInfo);
                    return;
                }

                // Keep the native request alive until its destination is ready.
                window.Quit();
                showAgain = false;
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // OnQuit might fire after ShellWindowRegistered in case of reattached tab (and there were selected files)
            if (!isRecentlyClosed)
            {
                isRecentlyClosed = await Helper.DoUntilNotDefaultAsync(() => !cancellationToken.IsCancellationRequested && TryGetRecentlyClosedWindow(location, out closedWindow), 700, 50, cancellationToken);
                if (isRecentlyClosed)
                    SelectItems(window, closedWindow!.SelectedItems);
            }

            cancellationToken.ThrowIfCancellationRequested();
            HookWindowEvents(window, windowInfo);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"Explorer tab conversion failed: {ex}");
            if (!_disposed && sourceWindow != null && windowInfo != null && _windowEntryDict.ContainsKey(sourceWindow))
            {
                windowInfo.IsConversionSource = false;
                HookWindowEvents(sourceWindow, windowInfo);
            }
            // The finally block reveals the original window on failure.
        }
        finally
        {
            if (showAgain && hWnd != 0)
            {
                PreventWindowHiding(hWnd);
                await Helper.DoUntilNotDefaultAsync(() => Helper.ShowWindow(hWnd, removeCache: false), 1_500, 200);

                Helper.UpdateWindowLayered(hWnd, remove: true);

                // OnWindowShown might fire after ShellWindowRegistered and hide it again, keep the cache, wait a bit, then remove it.
                _ = Task.Delay(3000).ContinueWith(t => Helper.HiddenWindows.TryRemove(hWnd, out _), TaskScheduler.Default);
            }
        }
    }
    private void HookWindowEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        // Create strongly-typed handlers so we can remove them later
        windowInfo.OnQuitHandler = () =>
        {
            var location = windowInfo.Location ?? GetLocation(window);
            var locationName = windowInfo.Name ?? window.LocationName;
            var windowRecord = new WindowRecord(location, new IntPtr(window.HWND), name: locationName);
            lock (_closedWindowsLock) _closedWindows.Add(windowRecord);

            // Home, This PC, etc
            if (location == _defaultLocation)
            {
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            windowRecord.SelectedItems = GetSelectedItems(window);
            RemoveWindowAndUnhookEvents(window, windowInfo);
        };

        windowInfo.OnNavigateHandler = (object _, ref object _) =>
            {
                windowInfo.Location = GetLocation(window);
                windowInfo.Name = window.LocationName;
                AttachSelectionEvents(window, windowInfo);
            };

        try
        {
            // Subscribe
            window.OnQuit += windowInfo.OnQuitHandler;
            windowInfo.Location = GetLocation(window);
            windowInfo.Name = window.LocationName;
            window.NavigateComplete2 += windowInfo.OnNavigateHandler;
            AttachSelectionEvents(window, windowInfo);

            // Make sure the window is still alive (User might have closed it immediately after opening it)
            _ = window.HWND;
        }
        catch
        {
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);
        }
    }
    private void AttachSelectionEvents(InternetExplorer window, WindowInfo info)
    {
        var view = window.Document as DShellFolderViewEvents_Event;
        if (view == null || ReferenceEquals(view, info.SelectionView)) return;
        DetachSelectionEvents(info);
        info.SelectionView = view;
        info.OnSelectionChanged = () =>
        {
            // SelectionChanged also fires when an inactive folder refreshes or
            // restores its selection after a save. It is not an open request.
            // Native reveal requests foreground Explorer themselves; only help
            // select their inactive tab in an already-foreground window.
            if (_disposed || !_reuseTabs || info.IsConversionSource || info.SelectionActivationPending ||
                !_windowEntryDict.TryGetValue(window, out WindowEntry entry) || entry.OptionalKey is not { } tab || tab == 0) return;
            var parent = WinApi.GetParent(tab);
            if (!CanHandleInactiveSelection(parent, tab)) return;
            info.SelectionActivationPending = true;
            _dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // Recheck after dispatch: focus, the active tab, and the
                    // document can all change before this queued callback runs.
                    if (_disposed || !_reuseTabs || !_windowEntryDict.ContainsKey(window) ||
                        !ReferenceEquals(info.SelectionView, view) || !CanHandleInactiveSelection(parent, tab) ||
                        GetSelectedItems(window)?.Length is not > 0) return;
                    await SelectTabByHandle(parent, tab);
                }
                catch (COMException) { }
                finally { info.SelectionActivationPending = false; }
            }));
        };
        view.SelectionChanged += info.OnSelectionChanged;
    }

    private static bool CanHandleInactiveSelection(nint parent, nint tab) =>
        parent != 0 && !WinApi.IsIconic(parent) && WinApi.GetForegroundWindow() == parent && tab != GetActiveTabHandle(parent);

    private static void DetachSelectionEvents(WindowInfo info)
    {
        try
        {
            if (info.SelectionView != null && info.OnSelectionChanged != null)
                info.SelectionView.SelectionChanged -= info.OnSelectionChanged;
        }
        catch (COMException) { }
        info.SelectionView = null;
        info.OnSelectionChanged = null;
    }

    private void RemoveWindowAndUnhookEvents(InternetExplorer window, WindowInfo windowInfo, bool useLock = true)
    {
        windowInfo.InitialViewReady?.Dispose();
        DetachSelectionEvents(windowInfo);
        // Unsubscribe
        if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
        if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;

        // Remove from dictionary
        if (useLock)
        {
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);
        }
        else
            _windowEntryDict.Remove(window);

        // Finally, release the COM reference for this InternetExplorer instance
        Marshal.ReleaseComObject(window);
    }

    private async Task RestorePreviousWindows()
    {
        foreach (var record in _closedWindows.Where(record => record.Restore).ToArray())
        {
            record.Restore = false;
            
            await OpenTabNavigateWithSelection(record);
        }
    }
    private async Task OpenNewWindowWithSelection(WindowRecord windowToOpen, bool duplicate = true, bool lockToOpenWindows = true)
    {
        if (lockToOpenWindows)
            await _toOpenWindowsLock.WaitAsync();

        try
        {
            lock (_closedWindowsLock)
                _closedWindows.Add(windowToOpen);

            var hasSelection = windowToOpen.SelectedItems?.Length > 0;

            nint[]? currentWindows = null;
            if (hasSelection)
                currentWindows = Helper.GetAllExplorerWindows().ToArray();

            Helper.BypassWinForegroundRestrictions();

            var location = string.IsNullOrWhiteSpace(windowToOpen.Location) ? _defaultLocation : windowToOpen.Location;
            await RunInStaThread(() =>
            {
                Shell? shell = null;
                try
                {
                    shell = new Shell();
                    shell.ShellExecute(location, "", "", duplicate ? "opennewwindow" : "open");
                }
                finally
                {
                    if (shell != null)
                        Marshal.ReleaseComObject(shell);
                }
            });

            if (!hasSelection) return;

            var newWindowHandle = await Helper.ListenForNewExplorerWindowAsync(currentWindows ?? []);
            if (newWindowHandle == 0) return;

            var window = _windowEntryDict.Keys.FirstOrDefault(w => w.HWND == newWindowHandle);
            if (window == null) return;

            SelectItems(window, windowToOpen.SelectedItems);
        }
        finally
        {
            if (lockToOpenWindows)
                _toOpenWindowsLock.Release();
        }
    }
    private async Task<bool> OpenTabNavigateWithSelection(WindowRecord windowToOpen, nint windowHandle = 0, bool isDuplicate = false, bool forceTabReuse = false, Task? selectionReady = null)
    {
        if (_disposed) return false;
        if (string.IsNullOrWhiteSpace(windowToOpen.Location)) windowToOpen.Location = _defaultLocation;
        if (!_dispatcher.CheckAccess())
            return await _dispatcher.InvokeAsync(() => OpenTabNavigateWithSelection(windowToOpen, windowHandle, isDuplicate, forceTabReuse, selectionReady)).Task.Unwrap();

        await _toOpenWindowsLock.WaitAsync(_shellLifetime.Token);
        try
        {
            if ((_reuseTabs || forceTabReuse) && !isDuplicate && _windowEntryDict.Count > 0)
            {
                var existingTab = SearchForTab(windowToOpen.Location);
                if (existingTab != 0)
                {
                    windowHandle = WinApi.GetParent(existingTab);
                    await SelectTabByHandle(windowHandle, existingTab);
                    // Present the destination without waiting for a late reveal.
                    WinApi.RestoreWindowToForeground(windowHandle);
                    if (selectionReady != null) await selectionReady;
                    var existingWindow = GetWindowByTabHandle(existingTab);
                    if (existingWindow == null || !IsAtLocation(existingWindow, windowToOpen.Location)) return false;
                    if (!SelectItems(existingWindow, windowToOpen.SelectedItems)) return false;
                    WinApi.RestoreWindowToForeground(windowHandle);
                    return true;
                }
            }

            var mainWindowHWnd = Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(windowToOpen.Handle);
            if (mainWindowHWnd == 0)
            {
                if (selectionReady != null) await selectionReady;
                await OpenNewWindowWithSelection(windowToOpen, lockToOpenWindows: false);
                return true;
            }

            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();
            await RequestToOpenNewTab(mainWindowHWnd, lockToOpenWindows: false);
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs, 2_000);
            if (newTabHandle == 0) return false;

            var window = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 2_000, 50);
            if (window == null) return false;

            // Subscribe before navigation, and always detach (failure, timeout,
            // Explorer shutdown, or success). Continuations run outside COM callbacks.
            using var ready = new ShellViewReady(window, () => IsAtLocation(window, windowToOpen.Location));
            await Navigate(window, windowToOpen.Location);
            WinApi.RestoreWindowToForeground(mainWindowHWnd);

            // An explicit utility request without a selection has nothing to
            // capture or restore. External windows always supply selectionReady:
            // an empty selection at registration does not rule out a late /select.
            if (selectionReady == null && windowToOpen.SelectedItems?.Length is not > 0)
                return true;

            await ready.WaitAsync(_shellLifetime.Token);
            if (selectionReady != null) await selectionReady;
            if (!IsAtLocation(window, windowToOpen.Location)) { Trace.WriteLine($"Unexpected location: {GetLocation(window)} expected {windowToOpen.Location}"); return false; }
            if (!SelectItems(window, windowToOpen.SelectedItems)) return false;
            WinApi.RestoreWindowToForeground(mainWindowHWnd);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Opening Explorer tab failed: {ex}");
            return false;
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }
    }

    private bool IsAtLocation(InternetExplorer window, string location) =>
        _shellPathComparer.IsEquivalent(GetLocation(window), location);

    private bool TryGetRecentlyClosedWindow(string location, out WindowRecord? closedWindow, int maxAge = 2_000)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(location);
            lock (_closedWindowsLock)
            {
                for (var i = _closedWindows.Count - 1; i >= 0; i--)
                {
                    var record = _closedWindows[i];
                    if (Environment.TickCount - record.CreatedAt > maxAge) break;
                    if (!_shellPathComparer.IsEquivalent(location, record.Location, targetPidl)) continue;
                    _closedWindows.RemoveAt(i);
                    closedWindow = record;
                    return true;
                }
            }
            closedWindow = null;
            return false;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");

        // Get another handle other than the newly created one. (In case if it is still alive.)
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Reverse() // To get the last one in the z-index (the oldest)
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count()) // The one with the most tabs first
            .FirstOrDefault();

        if (_mainWindowHandle != 0) return _mainWindowHandle;

        return Helper.IsFileExplorerWindow(otherThan) ? otherThan : 0;
    }
    private Task<nint> GetTabHandle(InternetExplorer window)
    {
        if (_windowEntryDict.TryGetValue(window, out WindowEntry entry) && entry.OptionalKey is { } handle and > 0)
            return Task.FromResult(handle);

        // Schedule the operation on STA
        return RunInStaThread(() =>
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            if (window is not Interop.IServiceProvider sp) return 0;

            sp.QueryService(ref _shellBrowserGuid, ref _shellBrowserGuid, out var shellBrowser);
            if (shellBrowser == null) return 0;

            try
            {
                shellBrowser.GetWindow(out var hWnd);

                if (hWnd != 0)
                    _windowEntryDict.UpdateOptionalKey(window, hWnd);

                return hWnd;
            }
            finally
            {
                Marshal.ReleaseComObject(shellBrowser);
            }
        });
    }
    private static nint GetActiveTabHandle(nint windowHandle)
    {
        // Active tab always at the top of the z-index
        return WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
    }
    private InternetExplorer? GetWindowByTabHandle(nint tabHandle)
    {
        if (tabHandle == 0) return null;
        return _windowEntryDict.TryGetValue(tabHandle, out InternetExplorer? foundWindow) ? foundWindow : null;
    }
    private static string[]? GetSelectedItems(InternetExplorer window)
    {
        var selectedItems = (window.Document as ShellFolderView)!.SelectedItems();
        var count = selectedItems.Count;
        if (count == 0) return null;

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = selectedItems.Item(i).Path;
        }

        return result;
    }
    private static bool SelectItems(InternetExplorer window, string[]? names)
    {
        if (names == null || names.Length == 0) return true;
        if (window.Document is not ShellFolderView document) return false;

        var first = true;
        foreach (var name in names)
        {
            // Full parsing paths preserve hidden extensions and virtual items.
            // Relative names remain supported for history from older versions.
            object item = document.Folder.ParseName(name) ?? document.Folder.ParseName(System.IO.Path.GetFileName(name));
            if (item == null) { Trace.WriteLine($"Cannot resolve selected item: {name}"); return false; }
            document.SelectItem(ref item, first ? 1 | 4 | 8 | 16 : 1);
            first = false;
        }

        var actual = GetSelectedItems(window);
        return actual != null && names.All(name => actual.Any(path =>
            string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ||
            (!System.IO.Path.IsPathRooted(name) && string.Equals(System.IO.Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))));
    }
    private static string GetLocation(InternetExplorer window)
    {
        var path = window.LocationURL;
        if (!string.IsNullOrWhiteSpace(path)) return Helper.NormalizeLocation(path);

        // Recycle Bin, This PC, etc
        path = ((window.Document as ShellFolderView)!.Folder as Folder2)!.Self.Path;
        return Helper.NormalizeLocation(path);
    }
    private async Task Navigate(InternetExplorer window, string path)
    {
        if (!path.Contains("#") && !path.Contains("%23"))
        {
            window.Navigate2(path);
            return;
        }

        var folder = await RunInStaThread(() =>
        {
            Shell? shell = null;
            Folder? folder;
            try
            {
                shell = new Shell();
                folder = shell.NameSpace(path);
            }
            finally
            {
                if (shell != null)
                    Marshal.ReleaseComObject(shell);
            }
            return folder;
        });

        try
        {
            window.Navigate2(folder);
        }
        finally
        {
            if (folder != null)
                Marshal.ReleaseComObject(folder);
        }
    }
    // Shell COM objects and their event sinks have one message-pumping STA owner.
    // In particular, do not put COM calls on a worker that is joined by this STA.
    private Task RunInStaThread(Action action) => _dispatcher.InvokeAsync(action).Task;
    private Task<T?> RunInStaThread<T>(Func<T?> action) => _dispatcher.InvokeAsync(action).Task;

    private void StartExplorerProcessCheck() => _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 0, 1000);
    private void CheckForMainExplorer(object? state)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => CheckForMainExplorer(state)));
            return;
        }
        var process = Helper.GetMainExplorerProcess();
        if (process == null) return;
        
        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;
        
        lock (_processLock)
        {
            if (_mainExplorerProcessId != 0) return;
            
            _mainExplorerProcessId = process.Id;
            InitializeShellObjects();
            OnShellInitialized?.Invoke();
        }
    }
    private void OnExplorerProcessTerminated(object? s, ProcessEventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnExplorerProcessTerminated(s, e)));
            return;
        }
        // Main explorer.exe process (_shellWindows must be restarted)
        lock (_processLock)
        {
            if (e.ProcessId == _mainExplorerProcessId)
            {
                _mainExplorerProcessId = 0;
                DisposeShellObjects();
                StartExplorerProcessCheck();
                return;
            }
        }
        
        // Other explorer.exe processes
        lock (_windowEntryDictLock)
        {
            if (_windowEntryDict.Count == 0) return;
            var crashCount = 0;
            for (var i = _windowEntryDict.Count - 1; i >= 0; i--)
            {
                var (window, info) = _windowEntryDict.ElementAt<WindowEntry>(i);
                try
                {
                    _ = window.HWND;
                }
                catch
                {
                    if (info.OnNavigateHandler != null)
                    {
                        crashCount++;
                        lock (_closedWindowsLock)
                            _closedWindows.Add(new WindowRecord(info.Location!, name: info.Name!));
                    }
                    
                    RemoveWindowAndUnhookEvents(window, info, useLock: false);
                }
            }
            if (!SettingsManager.RestorePreviousWindows || _windowEntryDict.Count > 0) return;
            lock (_closedWindowsLock)
            {
                for (var i = 1; i <= crashCount; i++)
                    _closedWindows[_closedWindows.Count - i].Restore = true;
            }
        }
    }

    private void InitializeShellObjects()
    {
        _shellPathComparer = new ShellPathComparer();
        _shellWindows = new ShellWindows();

        _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);
        _taskbarLaunchHook = new TaskbarLaunchHook(_dispatcher,
            () => !_disposed && _isForcingTabs && Helper.IsFileExplorerWindow(_mainWindowHandle),
            async location =>
            {
                var destination = location?.StartsWith("::", StringComparison.Ordinal) == true ? "shell:" + location : location ?? _defaultLocation;
                var request = new WindowRecord(destination);
                if (!await OpenTabNavigateWithSelection(request, _mainWindowHandle) && !_disposed)
                    await OpenNewWindowWithSelection(request);
            });
        if (_isForcingTabs) _taskbarLaunchHook.Start();
        
        if (SettingsManager.ClosedWindows != null)
            lock (_closedWindowsLock) _closedWindows.AddRange(SettingsManager.ClosedWindows);

        // Hook the global "WindowRegistered" event
        _windowRegisteredHandler = OnShellWindowRegistered;
        _shellWindows.WindowRegistered += _windowRegisteredHandler;

        // Hook the global "OBJECT_SHOW" event
        _eventObjectShowHookCallback = OnWindowShown;
        _eventObjectShowHookId = WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_SHOW, 0, _eventObjectShowHookCallback, 0, 0, 0);

        // Hook the event handlers for already-open windows
        var hasOpen = false;
        var count = _shellWindows.Count;
        for (var i = 0; i < count; i++)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window)
                continue;
            hasOpen = true;

            var windowInfo = new WindowInfo();
            _windowEntryDict.Add(window, windowInfo);
            window.PutProperty("seenBefore", true);

            _ = GetTabHandle(window);
            HookWindowEvents(window, windowInfo);
        }

        if (!hasOpen) return;
        _mainWindowHandle = GetMainWindowHWnd(0);
        lock (_closedWindowsLock)
            foreach (var window in _closedWindows) window.Restore = false;
    }
    private void DisposeShellObjects()
    {
        _taskbarLaunchHook?.Dispose();
        _taskbarLaunchHook = null;
        _shellLifetime.Cancel();
        if (!_disposed) _shellLifetime = new CancellationTokenSource();
        foreach (var (_, info) in _windowEntryDict)
            info.InitialViewReady?.Dispose();
        // A pending conversion must never leave a hidden native window behind.
        foreach (var handle in Helper.HiddenWindows.Keys.ToArray())
        {
            Helper.ShowWindow(handle, removeCache: true);
            Helper.UpdateWindowLayered(handle, remove: true);
        }
        PersistWindows();

        // Unhook global event
        if (_windowRegisteredHandler != null)
        {
            _shellWindows.WindowRegistered -= _windowRegisteredHandler;
            _windowRegisteredHandler = null;
        }
        if (_eventObjectShowHookCallback != null)
        {
            WinApi.UnhookWinEvent(_eventObjectShowHookId);
            _eventObjectShowHookCallback = null;
        }

        // Unsubscribe from each InternetExplorer instance's events
        foreach (var (window, windowInfo) in _windowEntryDict)
        {
            // Unsubscribe
            if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
            if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;

            windowInfo.InitialViewReady?.Dispose();
            DetachSelectionEvents(windowInfo);
            // Release the COM object
            Marshal.ReleaseComObject(window);
        }
        _windowEntryDict.Clear();

        // Release the ShellWindows COM object
        Marshal.ReleaseComObject(_shellWindows);

        _shellPathComparer.Dispose();
    }

    private void PersistWindows()
    {
        var store = new List<WindowRecord>();
        lock (_closedWindowsLock)
        {
            if (SettingsManager.SaveClosedHistory) store.AddRange(_closedWindows);
            _closedWindows.Clear();
        }

        // Save currently open windows (explorer crash / system restart, logoff / AppExit)
        if (SettingsManager.RestorePreviousWindows)
            lock (_windowEntryDictLock)
            {
                store.AddRange(_windowEntryDict.Values
                    .Where(w => w.OnNavigateHandler != null)
                    .Select(w => new WindowRecord(w.Location!, name: w.Name!, restore: true)));
            }
        
        // DistinctBy location
        var distinctItems = store
            .GroupBy(w => w.Location)
            .Select(g => g.Last())
            .ToArray();
        
        // TakeLast 100
        SettingsManager.ClosedWindows = distinctItems.Skip(Math.Max(0, distinctItems.Length - 100)).ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _explorerCheckTimer?.Dispose();
        _shellLifetime.Cancel();
        if (_shellWindows != null) DisposeShellObjects();
        _instanceRunning = false;
        _processWatcher.Dispose();
        GC.SuppressFinalize(this);
    }
}