using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.WinForms;

/// <summary>One inbox message: priority strip, category/time, title, body, action buttons and a ⋯ menu.</summary>
internal sealed class MessageCard : UserControl
{
    private const int StripWidth = 4;
    private readonly Panel _strip = new() { Dock = DockStyle.Left, Width = StripWidth };
    private readonly Label _meta = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _title = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _body = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
    private readonly FlowLayoutPanel _actions = new() { AutoSize = true, WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly Button _more = new() { Text = "⋯", FlatStyle = FlatStyle.Flat, Size = new Size(30, 24), TabStop = true, AccessibleName = "Message options" };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _toggleRead = new();
    private readonly ToolStripMenuItem _archive = new("Archive");
    private readonly ToolStripMenuItem _dismiss = new("Dismiss");
    private InboxTheme _theme;

    public MessageCard(InAppMessageDto message, InboxTheme theme)
    {
        Message = message;
        _theme = theme;
        DoubleBuffered = true;
        Margin = new Padding(0, 0, 0, 1);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.ListItem;

        _more.FlatAppearance.BorderSize = 0;
        _more.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _more.Click += (_, _) => _menu.Show(_more, new Point(0, _more.Height));
        _menu.Items.AddRange([_toggleRead, _archive, _dismiss]);
        _toggleRead.Click += (_, _) => ToggleReadRequested?.Invoke(this, EventArgs.Empty);
        _archive.Click += (_, _) => ArchiveRequested?.Invoke(this, EventArgs.Empty);
        _dismiss.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        ContextMenuStrip = _menu;

        Controls.AddRange([_strip, _meta, _title, _body, _actions, _more]);
        foreach (Control c in new Control[] { this, _meta, _title, _body })
        {
            c.Click += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
            c.ContextMenuStrip = _menu;
        }
        Bind(message);
    }

    public InAppMessageDto Message { get; private set; }

    public event EventHandler? Opened;
    public event EventHandler<AppActionDto>? ActionClicked;
    public event EventHandler? ToggleReadRequested;
    public event EventHandler? ArchiveRequested;
    public event EventHandler? DismissRequested;

    public void ApplyTheme(InboxTheme theme)
    {
        _theme = theme;
        Bind(Message);
    }

    public void Bind(InAppMessageDto m)
    {
        Message = m;
        var unread = m.ReadAt is null;
        BackColor = unread ? _theme.UnreadBackground : _theme.Surface;
        _strip.BackColor = m.Priority is NotificationPriority.Critical or NotificationPriority.High ? _theme.PriorityColor(m.Priority) : BackColor;

        var meta = new List<string>();
        if (m.Priority is NotificationPriority.Critical or NotificationPriority.High) meta.Add(m.Priority.ToString().ToUpperInvariant());
        if (!string.IsNullOrEmpty(m.Category)) meta.Add(m.Category);
        meta.Add(InboxFormatting.RelativeTime(m.CreatedAt));
        _meta.Text = string.Join("  ·  ", meta);
        _meta.ForeColor = m.Priority == NotificationPriority.Critical ? _theme.Critical : _theme.Muted;
        _meta.Font = new Font(Font.FontFamily, 7.75f);

        _title.Text = m.Title;
        _title.ForeColor = _theme.Text;
        _title.Font = new Font(Font.FontFamily, 9.5f, unread ? FontStyle.Bold : FontStyle.Regular);
        _body.Text = m.Body;
        _body.ForeColor = _theme.Muted;
        _body.Font = new Font(Font.FontFamily, 9f);

        _actions.Controls.Clear();
        foreach (var a in m.Actions)
        {
            var b = new Button
            {
                Text = a.Label, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 6, 0), Enabled = m.ActionTaken is null,
                ForeColor = m.ActionTaken == a.Id ? _theme.OnPrimary : _theme.Primary, BackColor = m.ActionTaken == a.Id ? _theme.Primary : _theme.Surface,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = _theme.Primary;
            var action = a;
            b.Click += (_, _) => ActionClicked?.Invoke(this, action);
            _actions.Controls.Add(b);
        }
        _actions.Visible = m.Actions.Count > 0;

        _toggleRead.Text = unread ? "Mark as read" : "Mark as unread";
        _archive.Visible = m.ArchivedAt is null;
        AccessibleName = (unread ? "Unread: " : "") + m.Title;
        LayoutFor(Width > 0 ? Width : 360);
    }

    /// <summary>Wraps the text to <paramref name="width"/> and sizes the card to fit.</summary>
    public void LayoutFor(int width)
    {
        SuspendLayout();
        Width = width;
        const int left = StripWidth + 12, right = 40, top = 8;
        var textWidth = Math.Max(80, width - left - right);
        var y = top;
        foreach (var c in new Control[] { _meta, _title, _body })
        {
            c.MaximumSize = new Size(textWidth, 0);
            c.Location = new Point(left, y);
            y += c.PreferredSize.Height + c.Margin.Bottom;
        }
        if (_actions.Visible)
        {
            _actions.MaximumSize = new Size(textWidth, 0);
            _actions.Location = new Point(left, y + 2);
            y += _actions.PreferredSize.Height + 4;
        }
        _more.Location = new Point(width - _more.Width - 6, 4);
        Height = y + 8;
        ResumeLayout();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { Opened?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { DismissRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(StripWidth, 0, Width - StripWidth, Height - 1));
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}
