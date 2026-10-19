using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NgBubbles.Core;
using System.Runtime.InteropServices;

namespace NgBubbles.Windows;

public sealed class WindowStateController
{
    private readonly Window window;
    private readonly LocalData data;
    private readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private WindowSettings settings;
    private bool stopped;
    private bool pendingMaximize;
    public WindowStateController(Window window, LocalData data)
    {
        this.window = window; this.data = data;
        settings = data.Store.Load("window.json", () => new WindowSettings());
        Restore();
        window.Activated += (_, args) =>
        {
            if (!pendingMaximize || args.WindowActivationState == WindowActivationState.Deactivated) return;
            pendingMaximize = false;
            if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        };
        window.AppWindow.Changed += (_, args) =>
        {
            if (stopped) return;
            if (!args.DidSizeChange && !args.DidPresenterChange && !args.DidPositionChange) return;
            Capture(); save.Stop(); save.Start();
        };
        save.Tick += async (_, _) => { save.Stop(); await SaveAsync(); };
    }
    public void Restore()
    {
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = settings.Fit(area.Width, area.Height, Scale());
        window.AppWindow.MoveAndResize(new(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
        pendingMaximize = settings.Maximized;
    }
    public void Capture()
    {
        if (pendingMaximize) return;
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized) return;
        if (presenter.State == OverlappedPresenterState.Maximized) settings = settings with { Maximized = true };
        else
        {
            var size = window.AppWindow.Size;
            if (size.Width > 0 && size.Height > 0) settings = new(size.Width / Scale(), size.Height / Scale(), false);
        }
    }
    public async Task SaveAsync()
    {
        save.Stop();
        try { await data.Store.SaveAsync("window.json", settings); }
        catch (Exception e) { data.Log.Log("window-save-failed", new { type = e.GetType().Name }); }
    }
    public void Stop() { stopped = true; save.Stop(); }
    internal double Scale()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        return dpi > 0 ? dpi / 96d : 1;
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
