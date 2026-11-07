using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Providers;

namespace Wings.NotificationHub.IntegrationTests;

/// <summary>
/// GET /monitoring and requeue stay inside the caller's grants. The seeded demo tenant and Acme stand in for two customers.
/// </summary>
[Collection("hub")]
public class MonitoringTenantScopeTests(HubFactory hub)
{
    private const string Monitoring = "/api/v1/monitoring";
    private const string Strong = "Correct-Horse-Battery-9";

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static string Requeue(Guid id) => $"{Monitoring}/dead-letters/{id}/requeue";

    private Task<Guid> AcmeTenantAsync() => hub.DbAsync(db => db.Companies.Where(c => c.Id == hub.AcmeCompany).Select(c => c.TenantId).SingleAsync());

    private Task<Guid> AcmeAppAsync(Guid acme) => hub.DbAsync(db => db.Applications.Where(a => a.TenantId == acme).Select(a => a.Id).FirstAsync());

    private async Task<JsonElement> ReadAsync(HttpClient http)
    {
        var r = await http.GetAsync(Monitoring);
        if (!r.IsSuccessStatusCode) Assert.Fail($"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Signs in a new user holding exactly these grants.</summary>
    private async Task<HttpClient> UserAsync(params object[] roles)
    {
        var email = $"m{Tag()}@example.com";
        var admin = await hub.AdminHttpAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Monitoring scope", password = Strong, roles });
        if (!created.IsSuccessStatusCode) Assert.Fail($"{(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        var http = hub.CreateClient();
        var login = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Strong });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private Task<Guid> DeadLetterAsync(Guid? tenantId, string error = "boom") => hub.DbAsync(async db =>
    {
        var q = new QueueMessage { Queue = "regression-dead", Type = "regression.noop", MessageId = Guid.NewGuid(), TenantId = tenantId, DeadLetteredAt = DateTime.UtcNow, VisibleAt = DateTime.UtcNow.AddDays(1), LastError = error };
        db.QueueMessages.Add(q);
        await db.SaveChangesAsync();
        return q.Id;
    });

    private static List<Guid> DeadLetterIds(JsonElement monitoring)
        => monitoring.GetProperty("deadLetters").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToList();

    private static bool CanRequeue(JsonElement monitoring, Guid id)
        => monitoring.GetProperty("deadLetters").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == id).GetProperty("canRequeue").GetBoolean();

    /// <summary>
    /// A failed delivery with a distinctive error code. The many attempts keep the code in the top 30 that /monitoring returns,
    /// whatever else the other tests in this collection have left behind.
    /// </summary>
    private Task<NotificationDelivery> DeliveryAsync(Guid tenantId, Guid companyId, Guid applicationId, Action<NotificationDelivery>? change = null, string? errorCode = null) => hub.DbAsync(async db =>
    {
        var request = new NotificationRequest { TenantId = tenantId, CompanyId = companyId, ApplicationId = applicationId, ActorId = "monitoring-scope" };
        var delivery = new NotificationDelivery { RequestId = request.Id, RecipientId = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, ApplicationId = applicationId, Channel = Channel.Email, Address = Req.Email(), Status = DeliveryStatus.Failed };
        change?.Invoke(delivery);
        db.NotificationRequests.Add(request);
        db.NotificationDeliveries.Add(delivery);
        if (errorCode is not null)
            foreach (var n in Enumerable.Range(1, 200))
                db.DeliveryAttempts.Add(new DeliveryAttempt { DeliveryId = delivery.Id, TenantId = tenantId, AttemptNumber = n, Outcome = AttemptOutcome.PermanentFailure, ErrorCode = errorCode, StartedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return delivery;
    });

    private static List<string> ErrorCodes(JsonElement monitoring)
        => monitoring.GetProperty("providerErrors24h").EnumerateArray().Where(e => e.TryGetProperty("errorCode", out _)).Select(e => e.GetProperty("errorCode").GetString()!).ToList();

    // ───────── dead letters ─────────

    [Fact]
    public async Task A_tenant_role_cannot_see_another_tenants_dead_letters()
    {
        var acme = await AcmeTenantAsync();
        var secret = "acme-only-" + Tag();
        var own = await DeadLetterAsync(hub.TenantId);
        var acmeLetter = await DeadLetterAsync(acme, secret);
        var noTenant = await DeadLetterAsync(null, secret);

        foreach (var key in new[] { "OPERATOR", "AUDITOR", "TENANT_ADMIN" })
        {
            var http = await hub.AdminHttpAsync(key);
            var ids = DeadLetterIds(await ReadAsync(http));
            Assert.Contains(own, ids);
            Assert.DoesNotContain(acmeLetter, ids);
            Assert.DoesNotContain(noTenant, ids);
            Assert.DoesNotContain(secret, await http.GetStringAsync(Monitoring));
        }

        var acmeIds = DeadLetterIds(await ReadAsync(await UserAsync(new { role = Roles.Operator, tenantId = acme })));
        Assert.Contains(acmeLetter, acmeIds);
        Assert.DoesNotContain(own, acmeIds);
        Assert.DoesNotContain(noTenant, acmeIds);
    }

    [Fact]
    public async Task Grants_narrower_than_a_tenant_get_no_dead_letters_at_all()
    {
        await DeadLetterAsync(hub.TenantId);
        var channelOperator = await UserAsync(new { role = Roles.Operator, tenantId = hub.TenantId, channel = "email" });
        var applicationAdmin = await UserAsync(new { role = Roles.ApplicationAdmin, tenantId = hub.TenantId, applicationId = hub.ErpApp });

        foreach (var http in new[] { await hub.AdminHttpAsync("ABC_ADMIN"), channelOperator, applicationAdmin })
        {
            // Absent, not empty: the page must not tell them there are none.
            Assert.False((await ReadAsync(http)).TryGetProperty("deadLetters", out _));
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync(Requeue(await DeadLetterAsync(hub.TenantId)), null)).StatusCode);
        }
    }

    [Fact]
    public async Task Requeue_reaches_only_the_callers_own_tenant()
    {
        var acme = await AcmeTenantAsync();
        var op = await hub.AdminHttpAsync("OPERATOR");
        var acmeLetter = await DeadLetterAsync(acme);
        var noTenant = await DeadLetterAsync(null);
        var own = await DeadLetterAsync(hub.TenantId);

        // Refused the same way as an id that does not exist, so ids cannot be probed.
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync(Requeue(acmeLetter), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync(Requeue(noTenant), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync(Requeue(Guid.NewGuid()), null)).StatusCode);
        Assert.Equal(2, await hub.DbAsync(db => db.QueueMessages.CountAsync(q => (q.Id == acmeLetter || q.Id == noTenant) && q.DeadLetteredAt != null)));

        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync(Requeue(own), null)).StatusCode);
        Assert.Null(await hub.DbAsync(db => db.QueueMessages.Where(q => q.Id == own).Select(q => q.DeadLetteredAt).SingleAsync()));

        // Only the requeue that happened is audited, and the tenant's own auditors can find it.
        var audited = await hub.DbAsync(db => db.AuditLogs.Where(a => a.Action == "queue.requeued").Where(a => a.EntityId == own.ToString() || a.EntityId == acmeLetter.ToString() || a.EntityId == noTenant.ToString()).ToListAsync());
        var entry = Assert.Single(audited);
        Assert.Equal(own.ToString(), entry.EntityId);
        Assert.Equal(hub.TenantId, entry.TenantId);
    }

    [Fact]
    public async Task Requeue_is_offered_row_by_row_when_a_user_holds_different_roles_in_two_tenants()
    {
        var acme = await AcmeTenantAsync();
        var http = await UserAsync(new { role = Roles.Auditor, tenantId = acme }, new { role = Roles.Operator, tenantId = hub.TenantId });
        var readOnly = await DeadLetterAsync(acme);
        var requeueable = await DeadLetterAsync(hub.TenantId);

        var monitoring = await ReadAsync(http);
        Assert.False(CanRequeue(monitoring, readOnly));
        Assert.True(CanRequeue(monitoring, requeueable));
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync(Requeue(readOnly), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync(Requeue(requeueable), null)).StatusCode);
    }

    [Fact]
    public async Task Platform_administrators_keep_every_dead_letter_and_can_requeue_any_of_them()
    {
        var admin = await hub.AdminHttpAsync();
        var acmeLetter = await DeadLetterAsync(await AcmeTenantAsync());
        var noTenant = await DeadLetterAsync(null);
        var demo = await DeadLetterAsync(hub.TenantId);

        var monitoring = await ReadAsync(admin);
        foreach (var id in new[] { acmeLetter, noTenant, demo })
        {
            Assert.True(CanRequeue(monitoring, id));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync(Requeue(id), null)).StatusCode);
        }
    }

    [Fact]
    public async Task A_platform_wide_auditor_reads_everything_but_requeues_nothing()
    {
        var auditor = await UserAsync(new { role = Roles.Auditor, tenantId = (Guid?)null });
        var acmeLetter = await DeadLetterAsync(await AcmeTenantAsync());

        var monitoring = await ReadAsync(auditor);
        Assert.False(CanRequeue(monitoring, acmeLetter));
        Assert.True(monitoring.TryGetProperty("queues", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsync(Requeue(acmeLetter), null)).StatusCode);
    }

    [Fact]
    public async Task Queue_messages_keep_the_tenant_of_their_outbox_row()
    {
        var acme = await AcmeTenantAsync();
        var id = await hub.DbAsync(async db =>
        {
            // The handler cannot read this payload, so the message stays on the queue, retrying, long enough to inspect.
            var outbox = new OutboxMessage { Type = MessageTypes.ProcessEvent, PayloadJson = "not json", TenantId = acme };
            db.OutboxMessages.Add(outbox);
            await db.SaveChangesAsync();
            return outbox.Id;
        });
        try
        {
            var queued = await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.QueueMessages.AsNoTracking().Where(q => q.MessageId == id).Select(q => new { q.TenantId }).FirstOrDefaultAsync()), q => q is not null);
            Assert.Equal(acme, queued!.TenantId);
        }
        finally
        {
            await hub.DbAsync(db => db.QueueMessages.Where(q => q.MessageId == id).ExecuteDeleteAsync());
        }
    }

    // ───────── provider errors and delivery counts ─────────

    [Fact]
    public async Task Provider_errors_cover_only_the_callers_own_deliveries()
    {
        var acme = await AcmeTenantAsync();
        var (abcCode, sunriseCode, acmeCode) = ("ABC_" + Tag(), "SUNRISE_" + Tag(), "ACME_" + Tag());
        await DeliveryAsync(hub.TenantId, hub.AbcCompany, hub.ErpApp, errorCode: abcCode);
        await DeliveryAsync(hub.TenantId, hub.SunriseCompany, hub.ErpApp, errorCode: sunriseCode);
        await DeliveryAsync(acme, hub.AcmeCompany, await AcmeAppAsync(acme), errorCode: acmeCode);

        var tenantWide = ErrorCodes(await ReadAsync(await hub.AdminHttpAsync("OPERATOR")));
        Assert.Contains(abcCode, tenantWide);
        Assert.Contains(sunriseCode, tenantWide);
        Assert.DoesNotContain(acmeCode, tenantWide);

        var company = ErrorCodes(await ReadAsync(await hub.AdminHttpAsync("ABC_ADMIN")));
        Assert.Contains(abcCode, company);
        Assert.DoesNotContain(sunriseCode, company);
        Assert.DoesNotContain(acmeCode, company);

        var otherTenant = ErrorCodes(await ReadAsync(await UserAsync(new { role = Roles.Operator, tenantId = acme })));
        Assert.Contains(acmeCode, otherTenant);
        Assert.DoesNotContain(abcCode, otherTenant);
        Assert.DoesNotContain(sunriseCode, otherTenant);

        var platform = ErrorCodes(await ReadAsync(await hub.AdminHttpAsync()));
        Assert.Contains(abcCode, platform);
        Assert.Contains(sunriseCode, platform);
        Assert.Contains(acmeCode, platform);
    }

    [Fact]
    public async Task Retry_and_operator_counts_cover_only_the_callers_own_deliveries()
    {
        var acme = await AcmeTenantAsync();
        var app = await AcmeAppAsync(acme);
        await DeliveryAsync(acme, hub.AcmeCompany, app, d => { d.Status = DeliveryStatus.Unknown; d.AwaitingOperator = true; });
        await DeliveryAsync(acme, hub.AcmeCompany, app, d => { d.Status = DeliveryStatus.RetryPending; d.NextAttemptAt = DateTime.UtcNow.AddDays(1); });

        var acmeView = await ReadAsync(await UserAsync(new { role = Roles.Operator, tenantId = acme }));
        var demoView = await ReadAsync(await hub.AdminHttpAsync("OPERATOR"));

        Assert.True(acmeView.GetProperty("awaitingOperator").GetInt32() >= 1);
        Assert.True(acmeView.GetProperty("retries").GetInt32() >= 1);
        Assert.Equal(await hub.DbAsync(db => db.NotificationDeliveries.CountAsync(d => d.TenantId == acme && d.AwaitingOperator)), acmeView.GetProperty("awaitingOperator").GetInt32());
        Assert.Equal(await hub.DbAsync(db => db.NotificationDeliveries.CountAsync(d => d.TenantId == hub.TenantId && d.AwaitingOperator)), demoView.GetProperty("awaitingOperator").GetInt32());
        Assert.Equal(await hub.DbAsync(db => db.NotificationDeliveries.CountAsync(d => d.TenantId == hub.TenantId && d.Status == DeliveryStatus.RetryPending)), demoView.GetProperty("retries").GetInt32());
    }

    // ───────── shared infrastructure ─────────

    [Fact]
    public async Task Queues_outbox_and_workers_stay_with_platform_wide_grants()
    {
        foreach (var key in new[] { "OPERATOR", "AUDITOR", "TENANT_ADMIN", "ABC_ADMIN" })
        {
            var monitoring = await ReadAsync(await hub.AdminHttpAsync(key));
            Assert.False(monitoring.TryGetProperty("queues", out _), key);
            Assert.False(monitoring.TryGetProperty("outboxBacklog", out _), key);
            Assert.False(monitoring.TryGetProperty("workers", out _), key);
        }

        var platform = await ReadAsync(await hub.AdminHttpAsync());
        Assert.True(platform.TryGetProperty("queues", out _));
        Assert.True(platform.TryGetProperty("outboxBacklog", out _));
        Assert.True(platform.TryGetProperty("workers", out _));
    }

    [Fact]
    public async Task Circuit_breakers_show_only_providers_the_caller_can_see()
    {
        var acme = await AcmeTenantAsync();
        var acmeProvider = await hub.DbAsync(db => db.ProviderAccounts.Where(a => a.TenantId == acme).Select(a => a.Id).FirstAsync());
        var sharedProvider = await hub.DbAsync(db => db.ProviderAccounts.Where(a => a.Scope == ProviderScope.Shared).Select(a => a.Id).FirstAsync());
        var circuits = hub.Services.GetRequiredService<CircuitBreakerRegistry>();
        circuits.RecordFailure(acmeProvider);
        circuits.RecordFailure(sharedProvider);
        try
        {
            static List<Guid> Ids(JsonElement m) => m.GetProperty("circuits").EnumerateArray().Select(c => c.GetProperty("providerAccountId").GetGuid()).ToList();

            var demo = Ids(await ReadAsync(await hub.AdminHttpAsync("OPERATOR")));
            Assert.Contains(sharedProvider, demo);
            Assert.DoesNotContain(acmeProvider, demo);

            var acmeView = Ids(await ReadAsync(await UserAsync(new { role = Roles.Operator, tenantId = acme })));
            Assert.Contains(sharedProvider, acmeView);
            Assert.Contains(acmeProvider, acmeView);

            var platform = Ids(await ReadAsync(await hub.AdminHttpAsync()));
            Assert.Contains(sharedProvider, platform);
            Assert.Contains(acmeProvider, platform);
        }
        finally
        {
            circuits.Reset(acmeProvider);
            circuits.Reset(sharedProvider);
        }
    }
}
