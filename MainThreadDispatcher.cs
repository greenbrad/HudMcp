using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HudMcp;

//Game memory wrappers aren't built for concurrent use, so requests run on the HUD's own thread when it's ticking
public sealed class MainThreadDispatcher
{
    private sealed class WorkItem
    {
        public Func<object> Work;
        public readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Claimed;
    }

    private readonly ConcurrentQueue<WorkItem> _queue = new();

    public void RunPending(int budgetMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs && _queue.TryDequeue(out var item))
        {
            Execute(item);
        }
    }

    public T Invoke<T>(Func<T> work, TimeSpan waitForMainThread)
    {
        var item = new WorkItem { Work = () => work() };
        _queue.Enqueue(item);
        if (!((IAsyncResult)item.Completion.Task).AsyncWaitHandle.WaitOne(waitForMainThread))
        {
            //The HUD isn't calling Tick/Render (loading screen, plugin disabled...), run it here instead
            Execute(item);
        }

        return (T)item.Completion.Task.GetAwaiter().GetResult();
    }

    private static void Execute(WorkItem item)
    {
        if (Interlocked.Exchange(ref item.Claimed, 1) != 0)
        {
            return;
        }

        try
        {
            item.Completion.SetResult(item.Work());
        }
        catch (Exception ex)
        {
            item.Completion.SetException(ex);
        }
    }
}
