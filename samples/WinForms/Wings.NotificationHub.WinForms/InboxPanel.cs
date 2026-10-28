using System.ComponentModel;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.WinForms;

/// <summary>Which messages the panel shows.</summary>
public enum InboxView { All, Unread, Archived }

/// <summary>
/// The inbox list: header with title and "Mark all as read", All/Unread/Archived tabs, message cards with priority styling,
/// action buttons and a per-message menu (read/unread, archive, dismiss), live insertion of new messages and "Load more".
/// Host it in any form, or use <see cref="NotificationBellControl"/> which shows it as a dropdown.
/// </summary>
[DefaultEvent(nameof(MessageOpened))]
public sealed class InboxPanel : UserControl
{
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 0, 8, 0) };
    private readonly Label _title = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "Notifications" };
    private readonly LinkLabel _markAll = new() { Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Text = "Mark all as read", Padding = new Padding(0, 14, 0, 0) };
    private readonly FlowLayoutPanel _tabs = new() { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(8, 4, 8, 0) };
    private readonly FlowLayoutPanel _list = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(8) };
    private readonly Button _loadMore = new() { Dock = DockStyle.Bottom, Height = 32, Text = "Load more", FlatStyle = FlatStyle.Flat, Visible = false };
    private readonly Dictionary<InboxView, RadioButton> _tabButtons = [];
    private InboxClient? _client;
    private InboxTheme _theme = new();
    private InboxView _view = InboxView.All;
    private int _page = 1;
    private bool _loading;
    private bool _loadedOnce;

    public InboxPanel()
    {
        DoubleBuffered = true;
        Size = new Size(380, 520);
        BackColor = _theme.Surface;
        _title.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _header.Controls.AddRange([_title, _markAll]);
        _markAll.LinkClicked += async (_, _) => await RunAsync(async () =>
        {
            if (_client is null) return;
            await _client.MarkAllReadAsync();
            foreach (var card in Cards()) card.Bind(Mark(card.Message, read: true));
        });
        foreach (var v in Enum.GetValues<InboxView>())
        {
            var tab = new RadioButton { Text = v.ToString(), Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, AutoSize = true, Checked = v == _view, Margin = new Padding(0, 0, 4, 0), Cursor = Cursors.Hand };
            tab.FlatAppearance.BorderSize = 0;
            tab.CheckedChanged += async (_, _) => { if (tab.Checked) { _view = v; await ReloadAsync(); } };
            _tabButtons[v] = tab;
            _tabs.Controls.Add(tab);
        }
        _list.Resize += (_, _) => RelayoutCards();
        _loadMore.Click += async (_, _) => { _page++; await LoadAsync(append: true); };
        // Fill must be added first so the docked header/tabs/footer take their space before it.
        Controls.AddRange([_list, _loadMore, _status, _tabs, _header]);
        ApplyTheme(_theme);
    }

    /// <summary>The inbox client; set it before the panel is shown.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InboxClient? Client
    {
        get => _client;
        set
        {
            if (_client is not null) { _client.NotificationReceived -= OnNotification; _client.UnreadCountChanged -= OnUnreadChanged; }
            _client = value;
            if (_client is not null) { _client.NotificationReceived += OnNotification; _client.UnreadCountChanged += OnUnreadChanged; }
        }
    }

    /// <summary>Base address of your web app; relative deep links ("/sales/orders/42") are opened against it.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Uri? WebBaseAddress { get; set; }

    /// <summary>Overrides branding.inboxTitle.</summary>
    [Category("Appearance"), DefaultValue(null), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public string? Title { get; set; }

    [Category("Behavior"), DefaultValue(20), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int PageSize { get; set; } = 20;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InboxTheme Theme => _theme;

    /// <summary>A message was opened (clicked or Enter).</summary>
    public event EventHandler<InAppMessageDto>? MessageOpened;
    /// <summary>An action button was pressed (the hub also raises an inapp.action webhook to your backend).</summary>
    public event EventHandler<InboxActionEventArgs>? ActionInvoked;
    /// <summary>A deep link should be followed. Set Handled to navigate inside your app; otherwise http(s) links open in the browser.</summary>
    public event EventHandler<DeepLinkEventArgs>? DeepLinkRequested;

    public void ApplyTheme(InboxTheme theme)
    {
        _theme = theme;
        BackColor = theme.Surface;
        _header.BackColor = theme.Primary;
        _title.ForeColor = theme.OnPrimary;
        _markAll.LinkColor = _markAll.ActiveLinkColor = theme.OnPrimary;
        _markAll.DisabledLinkColor = Color.FromArgb(160, theme.OnPrimary);
        _tabs.BackColor = theme.Surface;
        _list.BackColor = theme.Surface;
        _loadMore.ForeColor = theme.Primary;
        _loadMore.FlatAppearance.BorderColor = theme.Border;
        foreach (var t in _tabButtons.Values) { t.ForeColor = theme.Text; t.FlatAppearance.CheckedBackColor = theme.UnreadBackground; }
        foreach (var c in Cards()) c.ApplyTheme(theme);
    }

    /// <summary>Loads branding and the first page (first call), or reloads the current view.</summary>
    public async Task ReloadAsync()
    {
        if (_client is null) return;
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            try
            {
                var branding = await _client.GetBrandingAsync();
                ApplyTheme(InboxTheme.From(branding));
                _title.Text = Title ?? branding.InboxTitle ?? "Notifications";
            }
            catch (Exception ex) { ShowStatus(ex.Message); }
        }
        _page = 1;
        await LoadAsync(append: false);
    }

    /// <summary>Opens a message: marks it read and follows its deep link.</summary>
    public async Task OpenAsync(InAppMessageDto m)
    {
        MessageOpened?.Invoke(this, m);
        if (m.ReadAt is null && _client is not null) await RunAsync(async () => Update(await _client.MarkReadAsync(m.Id)));
        FollowLink(m, m.DeepLink, null);
    }

    private async Task LoadAsync(bool append)
    {
        if (_client is null || _loading) return;
        _loading = true;
        ShowStatus(null);
        try
        {
            var page = await _client.ListAsync(new InboxQuery(UnreadOnly: _view == InboxView.Unread ? true : null, Archived: _view == InboxView.Archived ? true : null, Page: _page, PageSize: PageSize));
            _list.SuspendLayout();
            if (!append) ClearCards();
            foreach (var m in page.Items.Where(m => Cards().All(c => c.Message.Id != m.Id))) _list.Controls.Add(CreateCard(m));
            _list.ResumeLayout();
            RelayoutCards();
            _loadMore.Visible = page.Items.Count == PageSize;
            if (_list.Controls.Count == 0) ShowStatus(_view == InboxView.Archived ? "No archived notifications." : "You're all caught up.");
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
        finally { _loading = false; }
    }

    private MessageCard CreateCard(InAppMessageDto m)
    {
        var card = new MessageCard(m, _theme) { Width = CardWidth() };
        card.Opened += async (_, _) => await OpenAsync(card.Message);
        card.ActionClicked += async (_, action) =>
        {
            ActionInvoked?.Invoke(this, new InboxActionEventArgs(card.Message, action));
            if (_client is null) return;
            await RunAsync(async () => Update(await _client.PerformActionAsync(card.Message.Id, action.Id)));
            FollowLink(card.Message, action.DeepLink ?? card.Message.DeepLink, action);
        };
        card.ToggleReadRequested += async (_, _) => await RunAsync(async () =>
        {
            if (_client is null) return;
            Update(card.Message.ReadAt is null ? await _client.MarkReadAsync(card.Message.Id) : await _client.MarkUnreadAsync(card.Message.Id));
        });
        card.ArchiveRequested += async (_, _) => await RunAsync(async () =>
        {
            if (_client is null) return;
            var updated = await _client.ArchiveAsync(card.Message.Id);
            if (_view == InboxView.Archived) Update(updated); else RemoveCard(card);
        });
        card.DismissRequested += async (_, _) => await RunAsync(async () =>
        {
            if (_client is null) return;
            await _client.DismissAsync(card.Message.Id);
            RemoveCard(card);
        });
        return card;
    }

    private void OnNotification(object? sender, InAppMessageDto m)
    {
        if (_view == InboxView.Archived || !_loadedOnce || Cards().Any(c => c.Message.Id == m.Id)) return;
        var card = CreateCard(m);
        _list.Controls.Add(card);
        // Critical messages go to the very top; everything else goes after the critical ones.
        var index = m.Priority == NotificationPriority.Critical ? 0 : Cards().Count(c => c != card && c.Message.Priority == NotificationPriority.Critical);
        _list.Controls.SetChildIndex(card, index);
        RelayoutCards();
        ShowStatus(null);
    }

    private void OnUnreadChanged(object? sender, int count)
    {
        _tabButtons[InboxView.Unread].Text = count > 0 ? $"Unread ({InboxFormatting.Badge(count)})" : "Unread";
        _markAll.Enabled = count > 0;
    }

    private void FollowLink(InAppMessageDto m, string? url, AppActionDto? action)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "#") return;
        var args = new DeepLinkEventArgs(m, url, action);
        DeepLinkRequested?.Invoke(this, args);
        if (args.Handled) return;
        if (DeepLinks.Resolve(url, WebBaseAddress) is { } uri) DeepLinks.OpenInBrowser(uri);
    }

    private void Update(InAppMessageDto updated)
    {
        foreach (var c in Cards().Where(c => c.Message.Id == updated.Id)) c.Bind(updated);
    }

    private void RemoveCard(MessageCard card)
    {
        _list.Controls.Remove(card);
        card.Dispose();
        if (_list.Controls.Count == 0) ShowStatus("You're all caught up.");
    }

    private void ClearCards()
    {
        var cards = Cards().ToList();
        _list.Controls.Clear();
        foreach (var c in cards) c.Dispose();
    }

    private IEnumerable<MessageCard> Cards() => _list.Controls.OfType<MessageCard>();

    private int CardWidth() => Math.Max(200, _list.ClientSize.Width - (_list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2);

    private void RelayoutCards()
    {
        var w = CardWidth();
        _list.SuspendLayout();
        foreach (var c in Cards()) c.LayoutFor(w);
        _list.ResumeLayout();
    }

    private void ShowStatus(string? text)
    {
        _status.Text = text ?? "";
        _status.Height = string.IsNullOrEmpty(text) ? 0 : 40;
        _status.ForeColor = _theme.Muted;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowStatus(ex.Message); }
    }

    private static InAppMessageDto Mark(InAppMessageDto m, bool read)
    {
        if (read && m.ReadAt is null) m.ReadAt = DateTimeOffset.UtcNow;
        return m;
    }
}
