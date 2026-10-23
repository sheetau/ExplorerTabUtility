using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Shell32;
using SHDocVw;

namespace ExplorerTabUtility.Interop;

/// <summary>
/// Observes the actual folder enumeration, rather than guessing how long a reveal
/// request takes. DocumentComplete precedes Explorer's initial selection.
/// Construct before starting navigation (or as soon as a window is registered).
/// </summary>
internal sealed class ShellViewReady : IDisposable
{
    private readonly InternetExplorer _window;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DWebBrowserEvents2_NavigateComplete2EventHandler _navigate;
    private readonly DWebBrowserEvents2_DocumentCompleteEventHandler _document;
    private readonly DWebBrowserEvents2_OnQuitEventHandler _quit;
    private readonly DShellFolderViewEvents_EnumDoneEventHandler _enumerated;
    private DShellFolderViewEvents_Event? _view;
    private bool _disposed;
    private readonly Func<bool>? _isExpectedLocation;
    private bool _navigationObserved;

    public ShellViewReady(InternetExplorer window, Func<bool>? isExpectedLocation = null)
    {
        _window = window;
        _isExpectedLocation = isExpectedLocation;
        _navigationObserved = isExpectedLocation == null;
        _enumerated = () =>
        {
            if (!_disposed && _navigationObserved && MatchesLocation())
                _ready.TrySetResult(true);
        };
        _navigate = (object _, ref object _) => OnNavigation();
        _document = (object _, ref object _) => OnNavigation();
        _quit = () => _ready.TrySetCanceled();
        window.NavigateComplete2 += _navigate;
        window.DocumentComplete += _document;
        window.OnQuit += _quit;
        AttachView();
    }

    private void OnNavigation()
    {
        if (_disposed) return;
        if (MatchesLocation()) _navigationObserved = true;
        AttachView();
    }

    private bool MatchesLocation()
    {
        try { return _isExpectedLocation == null || _isExpectedLocation(); }
        catch (COMException) { return false; }
        catch (InvalidComObjectException) { return false; }
    }

    private void AttachView()
    {
        if (_disposed) return;
        try
        {
            var view = _window.Document as DShellFolderViewEvents_Event;
            if (view == null || ReferenceEquals(view, _view)) return;
            if (_view != null) _view.EnumDone -= _enumerated;
            _view = view;
            _view.EnumDone += _enumerated;
        }
        catch (COMException) { /* NavigateComplete2 will retry when the view exists. */ }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        // This is a failure deadline, not a selection capture delay. Successful
        // requests finish on EnumDone, including ordinary opens with no selection.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var expired = Task.Delay(TimeSpan.FromSeconds(10), deadline.Token);
        if (await Task.WhenAny(_ready.Task, expired) != _ready.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Explorer did not finish enumerating the folder.");
        }
        deadline.Cancel();
        await _ready.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetCanceled();
        // A disconnected Explorer must not prevent the remaining sinks being removed.
        TryUnsubscribe(() => _window.NavigateComplete2 -= _navigate);
        TryUnsubscribe(() => _window.DocumentComplete -= _document);
        TryUnsubscribe(() => _window.OnQuit -= _quit);
        if (_view != null) TryUnsubscribe(() => _view.EnumDone -= _enumerated);
        _view = null;
    }

    private static void TryUnsubscribe(Action action)
    {
        try { action(); }
        catch (COMException) { }
        catch (InvalidComObjectException) { }
    }
}
