using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace ExplorerTabUtility.Helpers;

// Shell COM callbacks and UI actions share the WinUI message-pumping STA.
public sealed class UiDispatcher
{
    private readonly DispatcherQueue _queue;
    public static UiDispatcher Current { get; private set; } = null!;
    public static void Initialize(DispatcherQueue queue)
    {
        Current = new UiDispatcher(queue);
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
    }
    private UiDispatcher(DispatcherQueue queue) => _queue = queue;
    public bool CheckAccess() => _queue.HasThreadAccess;
    public void BeginInvoke(Action action)
    {
        if (!_queue.TryEnqueue(() => action())) throw new OperationCanceledException("The UI dispatcher is shutting down.");
    }
    public UiOperation<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        if (CheckAccess()) Run();
        else if (!_queue.TryEnqueue(Run)) completion.TrySetCanceled();
        return new UiOperation<T>(completion.Task);
    }
    public UiOperation<bool> InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });
    public T Invoke<T>(Func<T> action) => CheckAccess() ? action() : InvokeAsync(action).Task.GetAwaiter().GetResult();
    public void Invoke(Action action)
    {
        if (CheckAccess()) action();
        else InvokeAsync(action).Task.GetAwaiter().GetResult();
    }
}

public readonly struct UiOperation<T>(Task<T> task)
{
    public Task<T> Task { get; } = task;
    public TaskAwaiter<T> GetAwaiter() => Task.GetAwaiter();
}
