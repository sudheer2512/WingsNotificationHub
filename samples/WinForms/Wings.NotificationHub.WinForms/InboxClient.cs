using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.WinForms;

/// <summary>Options for <see cref="InboxClient"/>.</summary>
public sealed class InboxClientOptions
{
    /// <summary>Base URL of Wings Notification Hub, e.g. https://notifications.wings.example.</summary>
    public Uri BaseAddress { get; set; } = new("http://localhost:5480");

    /// <summary>
    /// Returns an inbox token for the signed-in user. Call YOUR backend here (it mints the token with
    /// POST /api/v1/inapp/tokens using its client credential). Never embed a client secret in a desktop app.
    /// </summary>
    public Func<CancellationToken, Task<InboxTokenResponse>> TokenProvider { get; set; } = _ => throw new InvalidOperationException("InboxClientOptions.TokenProvider is not set.");

    /// <summary>Refresh the token this long before it expires (default 30 s).</summary>
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>SignalR hub path; defaults to the token response's HubUrl ("/hubs/inapp").</summary>
    public string? HubPath { get; set; }

    /// <summary>Retries for 429/502/503/504 and network errors (default 3).</summary>
    public int MaxRetries { get; set; } = 3;
}

/// <summary>One page of the user's inbox.</summary>
public sealed class InboxPage
{
    /// <summary>Total unread, non-archived messages (independent of the filters).</summary>
    public int Unread { get; set; }
    public List<InAppMessageDto> Items { get; set; } = [];
}

/// <summary>Inbox filters.</summary>
public sealed record InboxQuery(bool? UnreadOnly = null, bool? Archived = null, string? Category = null, int Page = 1, int PageSize = 30);

/// <summary>White-label inbox branding of the user's company (falls back to the tenant).</summary>
public sealed class InboxBranding
{
    public string? InboxTitle { get; set; }
    public string? PrimaryColor { get; set; }
    public string? AccentColor { get; set; }
    public string? LogoUrl { get; set; }
}

/// <summary>Error returned by the hub (or a network failure, StatusCode 0).</summary>
public sealed class InboxClientException(int statusCode, string code, string message, string? correlationId = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? CorrelationId { get; } = correlationId;
}

/// <summary>
/// End-user in-app inbox client for desktop apps: REST (list, read/unread/archive/dismiss/action, read-all, branding)
/// plus SignalR live updates from /hubs/inapp. Uses short-lived inbox tokens from <see cref="InboxClientOptions.TokenProvider"/>,
/// refreshing them before expiry and after a 401. Events are raised on the <see cref="SynchronizationContext"/> that was
/// current when the client was created (the UI thread in WinForms), so handlers can touch controls directly.
/// </summary>
public sealed class InboxClient : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ChannelJsonConverter(), new JsonStringEnumConverter() },
    };

    private readonly InboxClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SynchronizationContext? _context;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Dictionary<Guid, InAppMessageDto> _known = [];
    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _tokenRefreshAt;
    private string _hubPath;
    private HubConnection? _connection;
    private InboxBranding? _branding;
    private int _unread;
    private bool _unreadKnown;

    public InboxClient(InboxClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _hubPath = options.HubPath ?? "/hubs/inapp";
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
        _context = SynchronizationContext.Current;
    }

    /// <summary>A new message arrived over SignalR.</summary>
    public event EventHandler<InAppMessageDto>? NotificationReceived;
    /// <summary>The unread count changed (list, mutations, realtime messages).</summary>
    public event EventHandler<int>? UnreadCountChanged;
    /// <summary>The realtime connection changed state.</summary>
    public event EventHandler<HubConnectionState>? ConnectionStateChanged;
    /// <summary>A background operation failed (realtime connection, unread resync).</summary>
    public event EventHandler<Exception>? BackgroundError;

    public int UnreadCount => _unread;
    public HubConnectionState ConnectionState => _connection?.State ?? HubConnectionState.Disconnected;

    // ───────── REST ─────────

    /// <summary>Lists messages (critical first, newest first) and refreshes <see cref="UnreadCount"/>.</summary>
    public async Task<InboxPage> ListAsync(InboxQuery? query = null, CancellationToken ct = default)
    {
        var q = query ?? new InboxQuery();
        var qs = new List<string> { $"page={q.Page}", $"pageSize={q.PageSize}" };
        if (q.UnreadOnly == true) qs.Add("unreadOnly=true");
        if (q.Archived == true) qs.Add("archived=true");
        if (!string.IsNullOrEmpty(q.Category)) qs.Add("category=" + Uri.EscapeDataString(q.Category));
        var page = await SendAsync<InboxPage>(HttpMethod.Get, "api/v1/inapp/messages?" + string.Join('&', qs), null, ct);
        lock (_gate) foreach (var m in page.Items) _known[m.Id] = m;
        SetUnread(page.Unread);
        return page;
    }

    public async Task<int> RefreshUnreadCountAsync(CancellationToken ct = default)
    {
        var page = await SendAsync<InboxPage>(HttpMethod.Get, "api/v1/inapp/messages?unreadOnly=true&pageSize=1", null, ct);
        SetUnread(page.Unread);
        return page.Unread;
    }

    public Task<InAppMessageDto> MarkReadAsync(Guid id, CancellationToken ct = default) => MutateAsync(id, "read", null, ct);
    public Task<InAppMessageDto> MarkUnreadAsync(Guid id, CancellationToken ct = default) => MutateAsync(id, "unread", null, ct);
    /// <summary>Archives (and marks read).</summary>
    public Task<InAppMessageDto> ArchiveAsync(Guid id, CancellationToken ct = default) => MutateAsync(id, "archive", null, ct);
    /// <summary>Removes the message from the inbox.</summary>
    public Task<InAppMessageDto> DismissAsync(Guid id, CancellationToken ct = default) => MutateAsync(id, "dismiss", null, ct);
    /// <summary>Records an action-button press (the hub raises an inapp.action webhook to the application).</summary>
    public Task<InAppMessageDto> PerformActionAsync(Guid id, string actionId, CancellationToken ct = default) => MutateAsync(id, "action", new { actionId }, ct);

    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync<JsonDocument>(HttpMethod.Post, "api/v1/inapp/messages/read-all", null, ct);
        lock (_gate)
            foreach (var m in _known.Values.Where(m => m.ReadAt is null)) m.ReadAt = DateTimeOffset.UtcNow;
        SetUnread(0);
        return doc.RootElement.TryGetProperty("marked", out var n) ? n.GetInt32() : 0;
    }

    /// <summary>White-label settings (title, colours, logo); cached after the first call.</summary>
    public async Task<InboxBranding> GetBrandingAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_branding is null || refresh) _branding = await SendAsync<InboxBranding>(HttpMethod.Get, "api/v1/inapp/branding", null, ct) ?? new InboxBranding();
        return _branding;
    }

    // ───────── realtime ─────────

    /// <summary>Opens the SignalR connection (automatic reconnect). Idempotent.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connection is not null) return;
        await GetTokenAsync(false, ct); // learns HubUrl and validates the token source first
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_options.BaseAddress, _hubPath.TrimStart('/')), o => o.AccessTokenProvider = async () => await GetTokenAsync(false, CancellationToken.None))
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                o.PayloadSerializerOptions.Converters.Add(new ChannelJsonConverter());
                o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .WithAutomaticReconnect()
            .Build();
        connection.On<InAppMessageDto>("notification", Receive);
        connection.Reconnecting += _ => { Raise(ConnectionStateChanged, HubConnectionState.Reconnecting); return Task.CompletedTask; };
        connection.Reconnected += async _ =>
        {
            Raise(ConnectionStateChanged, HubConnectionState.Connected);
            try { await RefreshUnreadCountAsync(); } catch (Exception ex) { Raise(BackgroundError, ex); }
        };
        connection.Closed += ex =>
        {
            Raise(ConnectionStateChanged, HubConnectionState.Disconnected);
            if (ex is not null) Raise(BackgroundError, ex);
            return Task.CompletedTask;
        };
        _connection = connection;
        try
        {
            Raise(ConnectionStateChanged, HubConnectionState.Connecting);
            await connection.StartAsync(ct);
            Raise(ConnectionStateChanged, HubConnectionState.Connected);
        }
        catch
        {
            _connection = null;
            await connection.DisposeAsync();
            Raise(ConnectionStateChanged, HubConnectionState.Disconnected);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var c = _connection;
        _connection = null;
        if (c is not null) await c.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        if (_ownsHttp) _http.Dispose();
        _tokenLock.Dispose();
    }

    /// <summary>Feeds a message into the client as if it had arrived over SignalR (also used internally).</summary>
    public void Receive(InAppMessageDto message)
    {
        bool isNew;
        lock (_gate)
        {
            isNew = !_known.ContainsKey(message.Id);
            _known[message.Id] = message;
        }
        if (isNew && message.ReadAt is null && message.ArchivedAt is null) SetUnread(_unread + 1);
        Raise(NotificationReceived, message);
    }

    // ───────── plumbing ─────────

    private async Task<InAppMessageDto> MutateAsync(Guid id, string action, object? body, CancellationToken ct)
    {
        InAppMessageDto? before;
        lock (_gate) before = _known.TryGetValue(id, out var b) ? Clone(b) : null;
        // The hub's action route binds an optional JSON body and only matches requests that send one.
        var after = await SendAsync<InAppMessageDto>(HttpMethod.Post, $"api/v1/inapp/messages/{id}/{action}", body ?? new { }, ct);
        var gone = action == "dismiss";
        lock (_gate)
        {
            if (gone) _known.Remove(id);
            else _known[id] = after;
        }
        if (before is not null && _unreadKnown)
            SetUnread(_unread + ((!gone && IsUnread(after)) ? 1 : 0) - (IsUnread(before) ? 1 : 0));
        else
            _ = Task.Run(async () => { try { await RefreshUnreadCountAsync(); } catch (Exception ex) { Raise(BackgroundError, ex); } });
        return after;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, new Uri(_options.BaseAddress, path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(false, ct));
            req.Headers.Accept.ParseAdd("application/json");
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < _options.MaxRetries) { await Task.Delay(Backoff(attempt), ct); continue; }
            catch (HttpRequestException ex) { throw new InboxClientException(0, "network_error", ex.Message, null, ex); }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new InboxClientException(0, "timeout", "The notification service did not respond in time.", null, ex); }
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    await GetTokenAsync(true, ct);
                    continue;
                }
                if ((int)resp.StatusCode is 429 or 502 or 503 or 504 && attempt < _options.MaxRetries)
                {
                    await Task.Delay(resp.Headers.RetryAfter?.Delta ?? Backoff(attempt), ct);
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    ApiError? err = null;
                    try { err = JsonSerializer.Deserialize<ApiError>(text, JsonOptions); } catch (JsonException) { }
                    throw new InboxClientException((int)resp.StatusCode, err?.Code is { Length: > 0 } c ? c : $"http_{(int)resp.StatusCode}",
                        err?.Message is { Length: > 0 } m ? m : $"Notification Hub returned {(int)resp.StatusCode}.", err?.CorrelationId);
                }
                if (typeof(T) == typeof(JsonDocument)) return (T)(object)JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
                return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
            }
        }
    }

    private async Task<string> GetTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _token is not null && DateTimeOffset.UtcNow < _tokenRefreshAt) return _token;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _token is not null && DateTimeOffset.UtcNow < _tokenRefreshAt) return _token;
            var t = await _options.TokenProvider(ct);
            if (string.IsNullOrEmpty(t.AccessToken)) throw new InboxClientException(401, "no_inbox_token", "The token provider returned no access token.");
            if (_options.HubPath is null && !string.IsNullOrEmpty(t.HubUrl)) _hubPath = t.HubUrl;
            var now = DateTimeOffset.UtcNow;
            var lifetime = t.ExpiresAt == default ? TimeSpan.FromMinutes(5) : t.ExpiresAt - now;
            _tokenRefreshAt = lifetime > _options.TokenRefreshSkew * 2 ? now + lifetime - _options.TokenRefreshSkew : now + lifetime / 2;
            _token = t.AccessToken;
            return _token;
        }
        finally { _tokenLock.Release(); }
    }

    private void SetUnread(int value)
    {
        value = Math.Max(0, value);
        var changed = value != _unread || !_unreadKnown;
        _unread = value;
        _unreadKnown = true;
        if (changed) Raise(UnreadCountChanged, value);
    }

    private void Raise<T>(EventHandler<T>? handler, T arg)
    {
        if (handler is null) return;
        if (_context is null || SynchronizationContext.Current == _context) handler(this, arg);
        else _context.Post(_ => handler(this, arg), null);
    }

    private static bool IsUnread(InAppMessageDto m) => m.ReadAt is null && m.ArchivedAt is null;

    private static InAppMessageDto Clone(InAppMessageDto m) => new()
    {
        Id = m.Id, Title = m.Title, Body = m.Body, Category = m.Category, Priority = m.Priority, DeepLink = m.DeepLink, ImageUrl = m.ImageUrl,
        Actions = m.Actions, CreatedAt = m.CreatedAt, ExpiresAt = m.ExpiresAt, ReadAt = m.ReadAt, ArchivedAt = m.ArchivedAt, ActionTaken = m.ActionTaken,
    };

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt) + Random.Shared.Next(0, 200));

    /// <summary>Convenience token provider: GET <paramref name="endpoint"/> on your backend, which returns the hub's InboxTokenResponse JSON.</summary>
    public static Func<CancellationToken, Task<InboxTokenResponse>> FromBackend(HttpClient backend, string endpoint)
        => async ct =>
        {
            using var resp = await backend.GetAsync(endpoint, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InboxClientException((int)resp.StatusCode, "token_endpoint_failed", $"Inbox token endpoint returned {(int)resp.StatusCode}: {Truncate(text)}");
            return JsonSerializer.Deserialize<InboxTokenResponse>(text, JsonOptions) ?? throw new InboxClientException(500, "token_endpoint_failed", "Empty inbox token response.");
        };

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;
}
