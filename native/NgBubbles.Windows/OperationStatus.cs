using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NgBubbles.Windows;

public sealed class OperationStatus : StackPanel
{
    public OperationStatus() { Spacing = 6; Visibility = Visibility.Collapsed; }
    public OperationNotice Begin(string message, Action? cancel = null)
    {
        var notice = new OperationNotice(message, cancel, item =>
        {
            Children.Remove(item);
            if (Children.Count == 0) Visibility = Visibility.Collapsed;
        });
        Children.Add(notice); Visibility = Visibility.Visible; return notice;
    }
}

public sealed class OperationNotice : UserControl
{
    public static TimeSpan SuccessDuration => TimeSpan.FromSeconds(5);
    private readonly InfoBar bar = new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly DispatcherTimer expiry = new() { Interval = SuccessDuration };
    private readonly Action<OperationNotice> remove;
    private bool dismissed;
    public ProgressBar Progress { get; } = new() { IsIndeterminate = true, Height = 4, Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 103, 192)) };
    public Button Stop { get; } = new() { Content = "Stop remaining", HorizontalAlignment = HorizontalAlignment.Left };
    public bool IsRunning { get; private set; } = true;
    public bool IsError { get; private set; }
    public string Message => bar.Message;
    public OperationNotice(string message, Action? cancel, Action<OperationNotice> remove)
    {
        this.remove = remove; bar.Message = message;
        var content = new StackPanel { Spacing = 6 }; content.Children.Add(Progress);
        Stop.Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;
        Stop.Click += (_, _) => { Stop.IsEnabled = false; Update("Finishing the current request, then stopping…"); cancel?.Invoke(); };
        content.Children.Add(Stop); bar.Content = content; Content = bar;
        bar.Closed += (_, _) => Dismiss();
        expiry.Tick += (_, _) => Dismiss();
        Unloaded += (_, _) => expiry.Stop();
    }
    public void Update(string message, int? completed = null, int? total = null)
    {
        if (!IsRunning || dismissed) return;
        bar.Message = message;
        if (completed.HasValue && total.HasValue)
        {
            Progress.Maximum = Math.Max(1, total.Value); Progress.Value = completed.Value;
            Progress.IsIndeterminate = completed.Value == 0;
        }
    }
    public void Complete(string message) => Finish(message, false);
    public void Fail(string message) => Finish(message, true);
    private void Finish(string message, bool failed)
    {
        if (!IsRunning || dismissed) return;
        IsRunning = false; IsError = failed; bar.Message = message;
        bar.Severity = failed ? InfoBarSeverity.Error : InfoBarSeverity.Success; bar.IsClosable = true;
        Progress.IsIndeterminate = false; Progress.Visibility = Visibility.Collapsed; Stop.Visibility = Visibility.Collapsed;
        if (!failed) expiry.Start();
    }
    public void Dismiss()
    {
        if (dismissed) return;
        dismissed = true; expiry.Stop(); bar.IsOpen = false; remove(this);
    }
}
