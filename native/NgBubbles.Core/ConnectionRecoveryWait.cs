using System.Diagnostics;

namespace NgBubbles.Core;

public static class ConnectionRecoveryWait
{
    public static async Task<bool> RunAsync(Task connecting, Func<bool> connected, Action requestSync,
        bool retry, TimeSpan timeout, IProgress<TimeSpan>? progress = null, CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        var nextSync = TimeSpan.Zero;
        _ = connecting.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (connecting.IsCompleted)
            {
                await connecting;
                if (connected()) return true;
                if (!retry) return false;
            }
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            progress?.Report(timer.Elapsed);
            if (connecting.IsCompleted && timer.Elapsed >= nextSync)
            {
                requestSync(); nextSync = timer.Elapsed + TimeSpan.FromSeconds(3);
            }
            var delay = Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), token);
            if (!connecting.IsCompleted) await Task.WhenAny(connecting, delay);
            else await delay;
        }
    }
}
