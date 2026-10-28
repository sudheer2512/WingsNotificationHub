using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.WinForms;

/// <summary>
/// Borderless, non-activating toast in the bottom-right corner of the primary screen. Toasts stack upwards (max 3);
/// critical messages stay until closed. Must be shown on the UI thread (InboxClient raises events there).
/// </summary>
public sealed class ToastForm : Form
{
    private const int MaxVisible = 3;
    private static readonly List<ToastForm> OpenToasts = [];
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Action _onOpen;

    private ToastForm(InAppMessageDto message, InboxTheme theme, TimeSpan duration, Action onOpen)
    {
        _onOpen = onOpen;
        Message = message;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(360, 92);
        BackColor = theme.Surface;
        Padding = new Padding(1);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Alert;
        AccessibleName = message.Title;

        var strip = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = message.Priority switch
        {
            NotificationPriority.Critical => theme.Critical,
            NotificationPriority.High => theme.High,
            _ => theme.Primary,
        } };
        var close = new Button { Text = "×", Dock = DockStyle.Right, Width = 32, FlatStyle = FlatStyle.Flat, ForeColor = theme.Muted, Cursor = Cursors.Default, AccessibleName = "Close" };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        var title = new Label { Dock = DockStyle.Top, Height = 26, Text = message.Title, Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold), ForeColor = theme.Text, AutoEllipsis = true, Padding = new Padding(10, 8, 0, 0) };
        var body = new Label { Dock = DockStyle.Fill, Text = message.Body, ForeColor = theme.Muted, AutoEllipsis = true, Padding = new Padding(10, 2, 4, 6) };
        Controls.AddRange([body, title, close, strip]);
        foreach (var c in new Control[] { this, title, body }) c.Click += (_, _) => { _onOpen(); Close(); };

        if (message.Priority != NotificationPriority.Critical)
        {
            _timer.Interval = (int)Math.Clamp(duration.TotalMilliseconds, 1000, int.MaxValue);
            _timer.Tick += (_, _) => Close();
            _timer.Start();
        }
        Paint += (_, e) => { using var pen = new Pen(theme.Border); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); };
    }

    public InAppMessageDto Message { get; }

    /// <summary>Shows a toast for <paramref name="message"/>; <paramref name="onOpen"/> runs when it is clicked.</summary>
    public static void ShowToast(InAppMessageDto message, InboxTheme theme, TimeSpan duration, Action onOpen)
    {
        if (OpenToasts.Any(t => t.Message.Id == message.Id)) return;
        while (OpenToasts.Count >= MaxVisible) OpenToasts[0].Close();
        var toast = new ToastForm(message, theme, duration, onOpen);
        OpenToasts.Add(toast);
        toast.FormClosed += (_, _) => { OpenToasts.Remove(toast); Restack(); };
        Restack();
        toast.Show();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private static void Restack()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var bottom = area.Bottom - 12;
        for (var i = OpenToasts.Count - 1; i >= 0; i--)
        {
            var t = OpenToasts[i];
            bottom -= t.Height;
            t.Location = new Point(area.Right - t.Width - 12, bottom);
            bottom -= 8;
        }
    }
}
