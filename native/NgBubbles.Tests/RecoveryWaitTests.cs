using System.Diagnostics;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class RecoveryWaitTests
{
    private sealed class Progress(Action<TimeSpan> report) : IProgress<TimeSpan>
    { public void Report(TimeSpan value) => report(value); }
    [Fact]
    public async Task SlowReconnectReportsProgressAndSucceedsWithoutRepeatingTheRestart()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = false; var syncs = 0; var progress = new List<TimeSpan>();
        var waiting = ConnectionRecoveryWait.RunAsync(ready.Task, () => connected, () => syncs++, true,
            TimeSpan.FromSeconds(2), new Progress(progress.Add));
        Assert.False(waiting.IsCompleted);
        connected = true; ready.SetResult();
        Assert.True(await waiting);
        Assert.NotEmpty(progress); Assert.Equal(0, syncs);
    }
    [Fact]
    public async Task OfflineServerQueuesCatchupAndCanRecoverAfterInitialConnectionFinishes()
    {
        var connected = false; var syncs = 0;
        Assert.True(await ConnectionRecoveryWait.RunAsync(Task.CompletedTask, () => connected,
            () => { syncs++; connected = true; }, true, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1, syncs);
        Assert.False(await ConnectionRecoveryWait.RunAsync(Task.CompletedTask, () => false,
            () => throw new InvalidOperationException("Manual retry should not start another retry loop"), false, TimeSpan.FromSeconds(1)));
    }
    [Fact]
    public async Task PendingConnectionHasBoundedWaitAndMayCompleteAfterTheUiIsReleased()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        Assert.False(await ConnectionRecoveryWait.RunAsync(pending.Task, () => false, () => { }, true, TimeSpan.FromMilliseconds(50)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(pending.Task.IsCompleted);
        pending.SetException(new IOException("A late connection failure must be observed"));
        await Task.Yield();
    }
    [Fact]
    public async Task CancellationAndConnectFailuresReleaseRecoveryInsteadOfLeavingProgressStuck()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectionRecoveryWait.RunAsync(Task.CompletedTask,
            () => false, () => { }, true, TimeSpan.FromSeconds(1), token: canceled.Token));
        await Assert.ThrowsAsync<IOException>(() => ConnectionRecoveryWait.RunAsync(Task.FromException(new IOException("Connection failed")),
            () => false, () => { }, true, TimeSpan.FromSeconds(1)));
    }
}
