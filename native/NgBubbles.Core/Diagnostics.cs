using System.Diagnostics;

namespace NgBubbles.Core;

public sealed class Diagnostics
{
    private readonly object gate = new();
    private readonly Queue<object> events = new();
    public void Log(string name, object? details = null)
    {
        lock (gate)
        {
            events.Enqueue(new { time = DateTimeOffset.UtcNow, @event = name, details });
            while (events.Count > 400) events.Dequeue();
        }
    }
    public string Report()
    {
        using var process = Process.GetCurrentProcess();
        lock (gate) return Json.Write(new { app = "NG Bubbles", version = AppInfo.Version, runtime = Environment.Version.ToString(),
            os = Environment.OSVersion.VersionString, memoryMb = process.WorkingSet64 / 1048576, events = events.ToArray() });
    }
}

public sealed class Store(string directory)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public string DirectoryPath { get; } = directory;
    public T Load<T>(string name, Func<T> fallback)
    {
        try { return Json.Read<T>(File.ReadAllText(Path.Combine(DirectoryPath, name))) ?? fallback(); }
        catch { return fallback(); }
    }
    public async Task SaveAsync<T>(string name, T value)
    {
        var text = Json.Write(value);
        await writes.WaitAsync();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var target = Path.Combine(DirectoryPath, name);
            await File.WriteAllTextAsync(target + ".tmp", text);
            File.Move(target + ".tmp", target, true);
        }
        finally { writes.Release(); }
    }
}
