using System.Net.Http.Json;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.WinForms.Sample;

/// <summary>
/// Sample desktop app ("Wings ERP"). The bell in the title bar shows the user's in-app inbox. Inbox tokens come from the
/// app's own backend (samples/AspNetCore.OrderService, GET /me/inbox-token); the desktop app never holds a client secret.
/// </summary>
public sealed class MainForm : Form
{
    private readonly SampleSettings _settings;
    private readonly HttpClient _backend;
    private readonly InboxClient _inbox;
    private readonly NotificationBellControl _bell = new() { Dock = DockStyle.Right, Width = 48 };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Font = new Font(FontFamily.GenericMonospace, 9f) };
    private int _orderSeq = 1000;

    public MainForm(SampleSettings settings)
    {
        _settings = settings;
        Text = "Wings ERP — Notification Hub desktop sample";
        Size = new Size(900, 600);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;

        // Demo authentication: the sample backend trusts an X-User-Id header. A real app sends its session/bearer token.
        _backend = new HttpClient { BaseAddress = new Uri(EnsureSlash(settings.BackendBaseUrl)) };
        _backend.DefaultRequestHeaders.Add("X-User-Id", settings.UserId);
        _inbox = new InboxClient(new InboxClientOptions
        {
            BaseAddress = new Uri(EnsureSlash(settings.HubBaseUrl)),
            TokenProvider = InboxClient.FromBackend(_backend, "me/inbox-token"),
        });

        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.White, Padding = new Padding(12, 6, 12, 6) };
        var title = new Label { Text = "Wings ERP", Dock = DockStyle.Left, AutoSize = false, Width = 200, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 13f, FontStyle.Bold) };
        var user = new Label { Text = $"Signed in as {settings.UserId}", Dock = DockStyle.Right, AutoSize = false, Width = 180, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.DimGray };
        header.Controls.AddRange([title, user, _bell]);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        buttons.Controls.AddRange([
            Button("Send test notification", SendTestNotificationAsync),
            Button("Approve order (business event)", ApproveOrderAsync),
            Button("Open inbox window", () => { new InboxForm(_inbox, WebBase()).Show(this); return Task.CompletedTask; }),
            Button("Reconnect", async () => { await _inbox.DisconnectAsync(); await _inbox.ConnectAsync(); }),
        ]);
        Controls.AddRange([_log, buttons, header]);

        _bell.Client = _inbox;
        _bell.WebBaseAddress = WebBase();
        _bell.MessageOpened += (_, m) => Log($"opened: {m.Title}");
        _bell.ActionInvoked += (_, e) => Log($"action '{e.Action.Id}' on: {e.Message.Title}");
        _bell.DeepLinkRequested += (_, e) => Log($"deep link: {e.Url}{(WebBase() is null && e.Url.StartsWith('/') ? " (set WebBaseUrl to open relative links)" : "")}");
        _inbox.NotificationReceived += (_, m) => Log($"received [{m.Priority}] {m.Title} — {m.Body}");
        _inbox.UnreadCountChanged += (_, n) => Log($"unread: {n}");
        _inbox.ConnectionStateChanged += (_, s) => Log($"realtime: {s}");
        _inbox.BackgroundError += (_, ex) => Log($"background error: {ex.Message}");

        Shown += async (_, _) =>
        {
            Log($"hub {settings.HubBaseUrl}, backend {settings.BackendBaseUrl}, user {settings.UserId}");
            try { await _bell.StartAsync(); }
            catch (Exception ex) { Log($"start failed: {ex.Message}"); }
        };
        FormClosing += async (_, _) => await _inbox.DisposeAsync();
    }

    private async Task SendTestNotificationAsync()
    {
        var order = $"SO-{++_orderSeq}";
        using var resp = await _backend.PostAsJsonAsync($"orders/{order}/notify", new { userId = _settings.UserId, amount = 18450 });
        Log($"POST /orders/{order}/notify → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task ApproveOrderAsync()
    {
        var order = $"SO-{++_orderSeq}";
        using var resp = await _backend.PostAsJsonAsync($"orders/{order}/approve", new { requesterUserId = _settings.UserId, amount = 184500, approvedBy = "Rahul Verma" });
        Log($"POST /orders/{order}/approve → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private Button Button(string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        b.Click += async (_, _) =>
        {
            b.Enabled = false;
            try { await onClick(); }
            catch (Exception ex) { Log($"{text} failed: {ex.Message}"); }
            finally { b.Enabled = true; }
        };
        return b;
    }

    private Uri? WebBase() => string.IsNullOrWhiteSpace(_settings.WebBaseUrl) ? null : new Uri(EnsureSlash(_settings.WebBaseUrl));

    private void Log(string line) => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _backend.Dispose();
        base.Dispose(disposing);
    }
}
