using NgBubbles.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NgBubbles.Windows;

internal static class CrashReport
{
    private static int reporting;
    public static void Save(Exception exception, string stage, Diagnostics? log = null)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NG Bubbles");
            Directory.CreateDirectory(directory);
            var causes = new List<object>();
            for (var cause = exception; cause != null && causes.Count < 8; cause = cause.InnerException)
                causes.Add(new { type = cause.GetType().FullName, hresult = $"0x{cause.HResult:X8}", frames = new StackTrace(cause, false).GetFrames()?.Select(frame => frame.GetMethod()).Select(method => method?.DeclaringType?.FullName + "." + method?.Name).ToArray() });
            File.WriteAllText(Path.Combine(directory, "last-error.txt"), Json.Write(new { app = AppInfo.Name, version = AppInfo.Version, time = DateTimeOffset.UtcNow, stage, causes, diagnostics = log?.Report() }));
        }
        catch { }
    }
    public static void Fatal(Exception exception, string stage, Diagnostics? log = null)
    {
        Save(exception, stage, log);
        if (Interlocked.Exchange(ref reporting, 1) != 0) return;
        if (!Environment.GetCommandLineArgs().Contains("--smoke-test"))
            MessageBoxW(0, "NG Bubbles could not start. A technical report was saved to:\n\n%APPDATA%\\NG Bubbles\\last-error.txt\n\nYour saved settings and messages have been kept.", "NG Bubbles startup error", 0x10);
        Environment.Exit(1);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
