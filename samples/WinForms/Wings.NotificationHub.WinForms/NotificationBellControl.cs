using System.ComponentModel;
using System.Drawing.Drawing2D;
using Microsoft.AspNetCore.SignalR.Client;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.WinForms;

/// <summary>
/// Notification bell with an unread badge. Click (or Enter/Space) opens the <see cref="InboxPanel"/> as a dropdown;
/// new realtime messages pop up as toasts. Assign <see cref="Client"/> and call <see cref="StartAsync"/> once.
/// </summary>
[DefaultEvent(nameof(MessageOpened))]
public sealed class NotificationBellControl : Control
{
    private readonly InboxPanel _panel = new();
    private readonly ToolStripDropDown _dropDown;
    private readonly ToolStripControlHost _host;
    private InboxClient? _client;
    private int _unread;
    private bool _reconnecting;
    private bool _panelLoaded;

    public NotificationBellControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        ForeColor = Color.FromArgb(0x3C, 0x40, 0x43);
        Size = new Size(40, 40);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Notifications";

        _host = new ToolStripControlHost(_panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = PanelSize };
        _dropDown = new ToolStripDropDown { AutoClose = true, Padding = Padding.Empty, DropShadowEnabled = true };
        _dropDown.Items.Add(_host);
        _dropDown.Closed += (_, _) => Invalidate();
        _panel.MessageOpened += (_, m) =>
        {
            MessageOpened?.Invoke(this, m);
            if (CloseOnOpen) _dropDown.Close();
        };
        _panel.ActionInvoked += (_, e) => ActionInvoked?.Invoke(this, e);
        _panel.DeepLinkRequested += (_, e) => DeepLinkRequested?.Invoke(this, e);
    }

    /// <summary>The inbox client shared by the bell, its panel and toasts.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InboxClient? Client
    {
        get => _client;
        set
        {
            if (_client is not null)
            {
                _client.UnreadCountChanged -= OnUnreadChanged;
                _client.NotificationReceived -= OnNotification;
                _client.ConnectionStateChanged -= OnConnectionChanged;
            }
            _client = value;
            _panel.Client = value;
            if (_client is not null)
            {
                _client.UnreadCountChanged += OnUnreadChanged;
                _client.NotificationReceived += OnNotification;
                _client.ConnectionStateChanged += OnConnectionChanged;
            }
        }
    }

    /// <summary>The dropdown panel (to set Title, PageSize, WebBaseAddress or subscribe to its events).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InboxPanel Panel => _panel;

    /// <summary>Base address of your web app for relative deep links.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Uri? WebBaseAddress { get => _panel.WebBaseAddress; set => _panel.WebBaseAddress = value; }

    [Category("Layout"), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public Size PanelSize { get => _host.Size; set => _host.Size = _panel.Size = value; }

    [Category("Behavior"), DefaultValue(true), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public bool ShowToasts { get; set; } = true;

    [Category("Behavior"), DefaultValue(true), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public bool CloseOnOpen { get; set; } = true;

    /// <summary>How long non-critical toasts stay visible (critical toasts stay until closed).</summary>
    [Category("Behavior"), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public TimeSpan ToastDuration { get; set; } = TimeSpan.FromSeconds(6);

    [Category("Appearance"), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public Color BadgeColor { get; set; } = Color.FromArgb(0xD3, 0x2F, 0x2F);

    [Browsable(false)]
    public int UnreadCount => _unread;

    public event EventHandler<InAppMessageDto>? MessageOpened;
    public event EventHandler<InboxActionEventArgs>? ActionInvoked;
    public event EventHandler<DeepLinkEventArgs>? DeepLinkRequested;

    /// <summary>Loads branding and the unread count, then connects realtime. Realtime failures are reported via InboxClient.BackgroundError.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Set Client before calling StartAsync.");
        try { _panel.ApplyTheme(InboxTheme.From(await _client.GetBrandingAsync(ct: ct))); } catch (InboxClientException) { /* default theme */ }
        await _client.RefreshUnreadCountAsync(ct);
        try { await _client.ConnectAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _reconnecting = true; Invalidate(); throw; }
    }

    /// <summary>Opens or closes the dropdown.</summary>
    public async void ToggleDropDown()
    {
        if (_dropDown.Visible) { _dropDown.Close(); return; }
        var screen = PointToScreen(new Point(Width - PanelSize.Width, Height));
        var area = Screen.FromControl(this).WorkingArea;
        var x = Math.Clamp(screen.X, area.Left, Math.Max(area.Left, area.Right - PanelSize.Width));
        _dropDown.Show(new Point(x, screen.Y));
        _panel.Focus();
        if (!_panelLoaded) { _panelLoaded = true; await _panel.ReloadAsync(); }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        ToggleDropDown();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { ToggleDropDown(); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Min(Width, Height) * 0.62f;
        var bell = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);
        if (_dropDown.Visible)
            using (var hover = new SolidBrush(Color.FromArgb(30, ForeColor))) g.FillEllipse(hover, 1, 1, Width - 3, Height - 3);
        using (var path = BellPath(bell))
        using (var brush = new SolidBrush(ForeColor))
            g.FillPath(brush, path);

        var text = InboxFormatting.Badge(_unread);
        if (text.Length > 0)
        {
            using var font = new Font(Font.FontFamily, Math.Max(6.5f, Height * 0.19f), FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font);
            var h = Math.Max(measured.Height, Height * 0.42f);
            var w = Math.Max(h, measured.Width + 6);
            var badge = new RectangleF(Width - w - 1, 1, w, h);
            using var badgePath = Rounded(badge, h / 2f);
            using var badgeBrush = new SolidBrush(BadgeColor);
            g.FillPath(badgeBrush, badgePath);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, badge, sf);
        }
        if (_reconnecting)
            using (var dot = new SolidBrush(Color.FromArgb(0xF9, 0xA8, 0x25))) g.FillEllipse(dot, Width - 11, Height - 11, 8, 8);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Client = null;
            _dropDown.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnUnreadChanged(object? sender, int count)
    {
        _unread = count;
        AccessibleName = count > 0 ? $"Notifications, {count} unread" : "Notifications";
        Invalidate();
    }

    private void OnConnectionChanged(object? sender, HubConnectionState state)
    {
        _reconnecting = state is HubConnectionState.Reconnecting or HubConnectionState.Disconnected && _client is not null;
        Invalidate();
    }

    private void OnNotification(object? sender, InAppMessageDto m)
    {
        if (!ShowToasts || m.ReadAt is not null || _dropDown.Visible) return;
        ToastForm.ShowToast(m, _panel.Theme, ToastDuration, () => { _ = _panel.OpenAsync(m); });
    }

    private static GraphicsPath BellPath(RectangleF r)
    {
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        var p = new GraphicsPath();
        p.AddLine(x + 0.20f * w, y + 0.74f * h, x + 0.20f * w, y + 0.46f * h);
        p.AddArc(x + 0.20f * w, y + 0.14f * h, 0.60f * w, 0.62f * h, 180, 180);
        p.AddLine(x + 0.80f * w, y + 0.46f * h, x + 0.80f * w, y + 0.74f * h);
        p.AddLine(x + 0.80f * w, y + 0.74f * h, x + 0.92f * w, y + 0.84f * h);
        p.AddLine(x + 0.92f * w, y + 0.84f * h, x + 0.08f * w, y + 0.84f * h);
        p.CloseFigure();
        p.AddEllipse(x + 0.40f * w, y + 0.86f * h, 0.20f * w, 0.13f * h);
        p.AddEllipse(x + 0.45f * w, y + 0.05f * h, 0.10f * w, 0.10f * h);
        return p;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
