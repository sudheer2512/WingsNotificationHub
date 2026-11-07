using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Client;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;

namespace Wings.NotificationHub.IntegrationTests;

/// <summary>Faults found while walking every admin-console procedure for the user manual.</summary>
[Collection("hub")]
public class AdminConsoleRegressionTests(HubFactory hub)
{
    private const string Strong = "Correct-Horse-Battery-9";

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private async Task<JsonElement> Json(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode) Assert.Fail($"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Guid> NewCampaignAsync(HttpClient http, string name)
    {
        var r = await Json(await http.PostAsJsonAsync("/api/v1/campaigns", new
        {
            companyId = hub.AbcCompany, applicationId = hub.ErpApp, name, channels = new[] { "email" }, templateCode = "PROMO_FESTIVE_V1", messageClass = "Transactional",
        }));
        return r.GetProperty("id").GetGuid();
    }

    private async Task<(string Email, Guid Id)> NewUserAsync(HttpClient admin, string role = "NotificationOperator", Guid? tenantId = null)
    {
        var email = $"u{Tag()}@example.com";
        var r = await Json(await admin.PostAsJsonAsync("/api/v1/users", new
        {
            email, displayName = "Regression user", password = Strong, roles = new[] { new { role, tenantId = tenantId ?? hub.TenantId } },
        }));
        return (email, r.GetProperty("id").GetGuid());
    }

    private async Task<HttpClient?> SignInAsync(string email, string password)
    {
        var http = hub.CreateClient();
        var r = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        if (!r.IsSuccessStatusCode) return null;
        var token = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    // ───────── campaigns ─────────

    [Fact]
    public async Task Editing_a_campaign_draft_keeps_everything_it_was_saved_with()
    {
        var manager = await hub.AdminHttpAsync("CAMPAIGNS");
        var group = await Json(await manager.PostAsJsonAsync("/api/v1/groups", new { companyId = hub.AbcCompany, name = "Group " + Tag(), dynamic = false, contactIds = Array.Empty<Guid>() }));
        var groupId = group.GetProperty("id").GetGuid();
        var segment = new { op = "and", children = new[] { new { field = "country", comparison = "eq", value = "IN" }, new { field = "kind", comparison = "eq", value = "customer" } } };
        var created = await Json(await manager.PostAsJsonAsync("/api/v1/campaigns", new
        {
            companyId = hub.AbcCompany, applicationId = hub.ErpApp, name = "Draft " + Tag(), channels = new[] { "email" }, templateCode = "PROMO_FESTIVE_V1",
            notificationTypeCode = "PROMO_FESTIVE", messageClass = "Promotional", groupIds = new[] { groupId }, segment, variables = new Dictionary<string, string> { ["Discount"] = "20" },
        }));
        var id = created.GetProperty("id").GetGuid();

        var draft = await Json(await manager.GetAsync($"/api/v1/campaigns/{id}"));
        Assert.Equal("PROMO_FESTIVE", draft.GetProperty("notificationTypeCode").GetString());
        Assert.Equal(groupId, draft.GetProperty("groupIds")[0].GetGuid());
        Assert.Equal("20", draft.GetProperty("variables").GetProperty("Discount").GetString());
        Assert.Equal(JsonValueKind.Object, draft.GetProperty("segment").ValueKind);

        // Saving what came back, as the edit form does, must lose nothing.
        var body = System.Text.Json.Nodes.JsonNode.Parse(draft.GetRawText())!.AsObject();
        body["name"] = "Renamed " + Tag();
        (await manager.PutAsJsonAsync($"/api/v1/campaigns/{id}", body)).EnsureSuccessStatusCode();
        var saved = await hub.DbAsync(db => db.Campaigns.AsNoTracking().FirstAsync(c => c.Id == id));
        Assert.Equal("PROMO_FESTIVE", saved.NotificationTypeCode);
        Assert.Equal([groupId], saved.GroupIds);
        Assert.Contains("Discount", saved.VariablesJson);
        Assert.Contains("country", saved.SegmentJson);
    }

    [Fact]
    public async Task Draft_fields_are_only_for_people_who_can_manage_campaigns()
    {
        var manager = await hub.AdminHttpAsync("CAMPAIGNS");
        var id = await NewCampaignAsync(manager, "Private " + Tag());
        var auditor = await hub.AdminHttpAsync("AUDITOR");
        Assert.Equal(HttpStatusCode.NotFound, (await auditor.GetAsync($"/api/v1/campaigns/{id}")).StatusCode);
    }

    [Fact]
    public async Task Cancelling_a_held_campaign_clears_the_hold()
    {
        var manager = await hub.AdminHttpAsync("CAMPAIGNS");
        var admin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var id = await NewCampaignAsync(manager, "Held " + Tag());
        (await admin.PostAsJsonAsync($"/api/v1/suspend/campaign/{id}", new { suspended = true, reason = "check" })).EnsureSuccessStatusCode();
        (await manager.PostAsync($"/api/v1/campaigns/{id}/cancel", null)).EnsureSuccessStatusCode();
        var c = await hub.DbAsync(db => db.Campaigns.AsNoTracking().FirstAsync(x => x.Id == id));
        Assert.Equal(CampaignStatus.Cancelled, c.Status);
        Assert.False(c.Suspended);
    }

    [Fact]
    public async Task Campaign_managers_can_test_their_campaign()
    {
        var manager = await hub.AdminHttpAsync("CAMPAIGNS");
        var created = await Json(await manager.PostAsJsonAsync("/api/v1/campaigns", new
        {
            companyId = hub.AbcCompany, applicationId = hub.ErpApp, name = "Test " + Tag(), channels = new[] { "email" }, templateCode = "PROMO_FESTIVE_V1", messageClass = "Transactional",
            variables = new { Discount = "20", ValidTill = "2026-12-31", CustomerName = "A" },
        }));
        var id = created.GetProperty("id").GetGuid();
        var sent = await manager.PostAsJsonAsync($"/api/v1/campaigns/{id}/test-send", new { recipient = new { email = Req.Email(), name = "Tester" } });
        var body = await Json(sent);
        Assert.Equal(1, body.GetProperty("deliveries").GetArrayLength());
    }

    // ───────── templates ─────────

    [Fact]
    public async Task An_unclosed_block_in_preview_is_reported_not_a_server_error()
    {
        var designer = await hub.AdminHttpAsync("DESIGNER");
        var r = await designer.PostAsJsonAsync("/api/v1/notifications/preview", new { channel = "email", subject = "Hi", body = "<p>{{#if Paid}}Thanks</p>", bodyFormat = "Html", variables = new { Paid = true } });
        var body = await Json(r);
        Assert.NotEmpty(body.GetProperty("errors").EnumerateArray());
        Assert.Empty(body.GetProperty("unknownVariables").EnumerateArray());
    }

    [Fact]
    public async Task Designers_can_test_send_the_active_version()
    {
        var designer = await hub.AdminHttpAsync("DESIGNER");
        var list = await Json(await designer.GetAsync("/api/v1/templates?code=PROMO_FESTIVE_V1&channel=email"));
        var template = list[0];
        var r = await designer.PostAsJsonAsync($"/api/v1/templates/{template.GetProperty("id").GetGuid()}/test-send", new
        {
            companyId = hub.AbcCompany, applicationId = hub.ErpApp, recipient = new { email = Req.Email(), name = "Tester" },
            variables = new { Discount = "20", ValidTill = "2026-12-31", CustomerName = "A" }, version = template.GetProperty("activeVersion").GetInt32(),
        });
        Assert.True((await Json(r)).GetProperty("delivered").GetBoolean());
    }

    [Fact]
    public async Task Designers_are_offered_whatsapp_accounts_without_provider_access()
    {
        var designer = await hub.AdminHttpAsync("DESIGNER");
        var list = await Json(await designer.GetAsync("/api/v1/templates?code=INVOICE_CREATED_V1&channel=whatsapp"));
        var accounts = await Json(await designer.GetAsync($"/api/v1/templates/{list[0].GetProperty("id").GetGuid()}/whatsapp-accounts"));
        var first = accounts[0];
        Assert.False(string.IsNullOrEmpty(first.GetProperty("name").GetString()));
        Assert.False(first.TryGetProperty("secretKeys", out _));
        Assert.False(first.TryGetProperty("settings", out _));
    }

    [Fact]
    public async Task Version_history_names_people_for_approvers()
    {
        var designer = await hub.AdminHttpAsync("DESIGNER");
        var created = await Json(await designer.PostAsJsonAsync("/api/v1/templates", new
        {
            template = new { tenantId = hub.TenantId, code = "NAMES_" + Tag().ToUpperInvariant(), name = "Names", channel = "email", locale = "en" },
            version = new { subject = "Hi", body = "Hello", bodyFormat = "Text" },
        }));
        var approver = await hub.AdminHttpAsync("APPROVER");
        var detail = await Json(await approver.GetAsync($"/api/v1/templates/{created.GetProperty("id").GetGuid()}"));
        var createdBy = detail.GetProperty("versions")[0].GetProperty("createdBy").GetString()!;
        Assert.Equal("Template Designer", detail.GetProperty("actors").GetProperty(createdBy).GetString());
    }

    // ───────── recipients and inbox ─────────

    [Fact]
    public async Task A_manual_suppression_keeps_its_company_and_note()
    {
        var admin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var address = Req.Email();
        foreach (var company in new[] { hub.AbcCompany, hub.SunriseCompany })
            (await admin.PostAsJsonAsync("/api/v1/suppressions", new { tenantId = hub.TenantId, companyId = company, channel = "email", address, reason = "Manual", note = "Asked by phone" })).EnsureSuccessStatusCode();
        var rows = await hub.DbAsync(db => db.Suppressions.AsNoTracking().Where(s => s.Address == address).ToListAsync());
        Assert.Equal([hub.AbcCompany, hub.SunriseCompany], rows.Select(r => r.CompanyId!.Value).OrderBy(x => x == hub.AbcCompany ? 0 : 1));
        var listed = await Json(await admin.GetAsync($"/api/v1/suppressions?address={Uri.EscapeDataString(address)}"));
        Assert.Equal("Asked by phone", listed[0].GetProperty("note").GetString());
    }

    [Fact]
    public async Task Inbox_search_ignores_case()
    {
        var name = "Zephyr" + Tag();
        await hub.DbAsync(async db =>
        {
            db.ConversationThreads.Add(new ConversationThread { TenantId = hub.TenantId, CompanyId = hub.AbcCompany, ApplicationId = hub.ErpApp, Channel = Channel.Sms, ExternalAddress = Req.Phone(), ContactName = name });
            return await db.SaveChangesAsync();
        });
        var op = await hub.AdminHttpAsync("OPERATOR");
        var rows = await Json(await op.GetAsync($"/api/v1/conversations?q={name.ToLowerInvariant()}"));
        Assert.Equal(1, rows.GetArrayLength());
    }

    // ───────── users ─────────

    [Fact]
    public async Task Adding_an_existing_email_is_refused_and_leaves_the_account_alone()
    {
        var admin = await hub.AdminHttpAsync();
        var (email, _) = await NewUserAsync(admin);
        var r = await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Someone else", password = "Another-Password-1", roles = new[] { new { role = "Auditor", tenantId = hub.TenantId } } });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.NotNull(await SignInAsync(email, Strong));
        Assert.Null(await SignInAsync(email, "Another-Password-1"));
        var user = await hub.DbAsync(db => db.AdminUsers.AsNoTracking().FirstAsync(u => u.Email == email));
        Assert.Equal("Regression user", user.DisplayName);
        Assert.Equal(1, await hub.DbAsync(db => db.RoleAssignments.CountAsync(x => x.UserId == user.Id)));
    }

    [Fact]
    public async Task A_tenant_administrator_cannot_take_over_a_platform_administrator()
    {
        var tenantAdmin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var email = hub.Credentials["ADMIN_EMAIL"];
        var r = await tenantAdmin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Mine now", password = "Attacker-Password-1", roles = new[] { new { role = "Auditor", tenantId = hub.TenantId } } });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Null(await SignInAsync(email, "Attacker-Password-1"));

        var id = await hub.DbAsync(db => db.AdminUsers.AsNoTracking().Where(u => u.Email == email).Select(u => u.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PutAsJsonAsync($"/api/v1/users/{id}", new { password = "Attacker-Password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PutAsJsonAsync($"/api/v1/users/{id}", new { enabled = false })).StatusCode);
        Assert.NotNull(await SignInAsync(email, hub.Credentials["ADMIN_PASSWORD"]));
    }

    [Fact]
    public async Task Roles_are_granted_to_existing_users_without_touching_the_account()
    {
        var admin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var (email, id) = await NewUserAsync(admin);
        var grant = new { role = "Auditor", tenantId = hub.TenantId };
        (await admin.PostAsJsonAsync($"/api/v1/users/{id}/roles", grant)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/users/{id}/roles", grant)).EnsureSuccessStatusCode(); // a repeat is not a second grant
        Assert.Equal(2, await hub.DbAsync(db => db.RoleAssignments.CountAsync(x => x.UserId == id)));
        Assert.NotNull(await SignInAsync(email, Strong));
        var denied = await admin.PostAsJsonAsync($"/api/v1/users/{id}/roles", new { role = "Auditor", tenantId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Accounts_can_be_disabled_renamed_and_given_a_new_password()
    {
        var admin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var (email, id) = await NewUserAsync(admin);
        var session = await SignInAsync(email, Strong);
        Assert.Equal(HttpStatusCode.OK, (await session!.GetAsync("/api/v1/auth/me")).StatusCode);

        (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { enabled = false })).EnsureSuccessStatusCode();
        Assert.Null(await SignInAsync(email, Strong));
        Assert.False((await session.GetAsync("/api/v1/auth/me")).IsSuccessStatusCode);

        (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { enabled = true, displayName = "Renamed", password = "Fresh-Password-Value-2" })).EnsureSuccessStatusCode();
        Assert.Null(await SignInAsync(email, Strong));
        Assert.NotNull(await SignInAsync(email, "Fresh-Password-Value-2"));
        Assert.Equal("Renamed", await hub.DbAsync(db => db.AdminUsers.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstAsync()));

        var shortPassword = await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { password = "short" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shortPassword.StatusCode);
        var audit = await hub.DbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.EntityId == id.ToString() && a.Action == "user.updated").Select(a => a.Summary).ToListAsync());
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, d => d != null && d.Contains("Fresh-Password"));
    }

    [Fact]
    public async Task Nobody_can_disable_their_own_account()
    {
        var admin = await hub.AdminHttpAsync();
        var me = await Json(await admin.GetAsync("/api/v1/auth/me"));
        var r = await admin.PutAsJsonAsync($"/api/v1/users/{me.GetProperty("actorId").GetString()}", new { enabled = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    // ───────── monitoring ─────────

    [Fact]
    public async Task Requeue_follows_permissions_not_a_role_name()
    {
        async Task<Guid> DeadLetterAsync() => await hub.DbAsync(async db =>
        {
            var q = new QueueMessage { Queue = "regression-dead", Type = "regression.noop", MessageId = Guid.NewGuid(), TenantId = hub.TenantId, DeadLetteredAt = DateTime.UtcNow, VisibleAt = DateTime.UtcNow.AddDays(1), LastError = "boom" };
            db.QueueMessages.Add(q);
            await db.SaveChangesAsync();
            return q.Id;
        });
        async Task<bool> OfferedAsync(HttpClient http, Guid id) => (await Json(await http.GetAsync("/api/v1/monitoring"))).GetProperty("deadLetters").EnumerateArray()
            .Single(m => m.GetProperty("id").GetGuid() == id).GetProperty("canRequeue").GetBoolean();

        var tenantAdmin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var letter = await DeadLetterAsync();
        Assert.True(await OfferedAsync(tenantAdmin, letter));
        Assert.Equal(HttpStatusCode.NoContent, (await tenantAdmin.PostAsync($"/api/v1/monitoring/dead-letters/{letter}/requeue", null)).StatusCode);

        var op = await hub.AdminHttpAsync("OPERATOR");
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/v1/monitoring/dead-letters/{await DeadLetterAsync()}/requeue", null)).StatusCode);

        var auditor = await hub.AdminHttpAsync("AUDITOR");
        letter = await DeadLetterAsync();
        Assert.False(await OfferedAsync(auditor, letter));
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsync($"/api/v1/monitoring/dead-letters/{letter}/requeue", null)).StatusCode);

        // A queue message has a tenant but no company, so a company-scoped grant cannot reach one.
        var abcAdmin = await hub.AdminHttpAsync("ABC_ADMIN");
        Assert.Equal(HttpStatusCode.Forbidden, (await abcAdmin.PostAsync($"/api/v1/monitoring/dead-letters/{await DeadLetterAsync()}/requeue", null)).StatusCode);
    }

    [Fact]
    public async Task Auditors_cannot_acknowledge_alerts()
    {
        async Task<Guid> AlertAsync() => await hub.DbAsync(async db =>
        {
            var a = new SystemAlert { TenantId = hub.TenantId, Source = "regression", Message = "Check me" };
            db.SystemAlerts.Add(a);
            await db.SaveChangesAsync();
            return a.Id;
        });
        var auditor = await hub.AdminHttpAsync("AUDITOR");
        var refused = await AlertAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsync($"/api/v1/monitoring/alerts/{refused}/ack", null)).StatusCode);
        Assert.Null(await hub.DbAsync(db => db.SystemAlerts.AsNoTracking().Where(a => a.Id == refused).Select(a => a.AcknowledgedAt).FirstAsync()));

        var op = await hub.AdminHttpAsync("OPERATOR");
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/v1/monitoring/alerts/{await AlertAsync()}/ack", null)).StatusCode);
        var companyAdmin = await hub.AdminHttpAsync("ABC_ADMIN");
        Assert.Equal(HttpStatusCode.NoContent, (await companyAdmin.PostAsync($"/api/v1/monitoring/alerts/{await AlertAsync()}/ack", null)).StatusCode);
    }

    [Fact]
    public async Task Full_addresses_in_history_follow_the_same_rule_as_the_delivery_detail()
    {
        var address = Req.Email();
        var sent = await hub.Sdk().SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Email = address }, Channel.Email));
        var op = await hub.AdminHttpAsync("OPERATOR");
        var shown = await Json(await op.GetAsync($"/api/v1/notifications/{sent.NotificationId}/history"));
        Assert.Equal(address, shown.GetProperty("deliveries")[0].GetProperty("recipient").GetString());
        var auditor = await hub.AdminHttpAsync("AUDITOR");
        var masked = await Json(await auditor.GetAsync($"/api/v1/notifications/{sent.NotificationId}/history"));
        Assert.NotEqual(address, masked.GetProperty("deliveries")[0].GetProperty("recipient").GetString());
    }

    // ───────── schedules and calendars ─────────

    private object ScheduleBody(string name, string cron, Guid? calendarId = null, DateTimeOffset? endAt = null) => new
    {
        companyId = hub.AbcCompany, applicationId = hub.ErpApp, name, cronExpression = cron, timeZone = "Asia/Kolkata", workingDaysOnly = calendarId is not null, holidayCalendarId = calendarId, endAt,
        notification = new
        {
            companyId = hub.AbcCompany, applicationId = hub.ErpApp, notificationType = "INVOICE_CREATED", channels = new[] { "email" }, recipient = new { email = Req.Email() },
            variables = Req.Invoice(),
        },
    };

    [Fact]
    public async Task Schedules_can_be_edited_and_are_revalidated()
    {
        var admin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var created = await Json(await admin.PostAsJsonAsync("/api/v1/schedules", ScheduleBody("Daily " + Tag(), "0 9 * * *")));
        var id = created.GetProperty("id").GetGuid();
        var name = "Weekly " + Tag();
        var updated = await Json(await admin.PutAsJsonAsync($"/api/v1/schedules/{id}", ScheduleBody(name, "0 10 * * MON")));
        var row = await hub.DbAsync(db => db.NotificationSchedules.AsNoTracking().FirstAsync(s => s.Id == id));
        Assert.Equal(name, row.Name);
        Assert.Equal("0 10 * * MON", row.CronExpression);
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.NextRunAt!.Value, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).DayOfWeek);
        Assert.Equal(JsonValueKind.String, updated.GetProperty("nextRunAt").ValueKind);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync($"/api/v1/schedules/{id}", ScheduleBody(name, "not a cron"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await hub.AppHttpAsync("ACME_BOOKS_DEMO")).PutAsJsonAsync($"/api/v1/schedules/{id}", ScheduleBody(name, "0 9 * * *"))).StatusCode);
    }

    [Fact]
    public async Task Holiday_calendars_can_be_edited_and_deleted_when_unused()
    {
        var admin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var first = new DateOnly(DateTime.UtcNow.Year + 1, 1, 26);
        var created = await Json(await admin.PostAsJsonAsync("/api/v1/holiday-calendars", new { tenantId = hub.TenantId, name = "Cal " + Tag(), country = "IN", dates = new[] { new { date = first, name = "Republic Day" } } }));
        var id = created.GetProperty("id").GetGuid();

        var second = first.AddDays(1);
        (await admin.PutAsJsonAsync($"/api/v1/holiday-calendars/{id}", new { name = "Renamed", country = "IN", dates = new[] { new { date = first, name = "Republic Day (observed)" }, new { date = second, name = "Extra" } } })).EnsureSuccessStatusCode();
        var dates = await hub.DbAsync(db => db.HolidayDates.AsNoTracking().Where(d => d.CalendarId == id).OrderBy(d => d.Date).ToListAsync());
        Assert.Equal(["Republic Day (observed)", "Extra"], dates.Select(d => d.Name));

        (await admin.PutAsJsonAsync($"/api/v1/holiday-calendars/{id}", new { name = "Renamed", country = "IN", dates = new[] { new { date = second, name = "Extra" } } })).EnsureSuccessStatusCode();
        Assert.Equal(1, await hub.DbAsync(db => db.HolidayDates.CountAsync(d => d.CalendarId == id)));

        var schedule = await Json(await admin.PostAsJsonAsync("/api/v1/schedules", ScheduleBody("Uses calendar " + Tag(), "0 9 * * *", id)));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/holiday-calendars/{id}")).StatusCode);
        (await admin.DeleteAsync($"/api/v1/schedules/{schedule.GetProperty("id").GetGuid()}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/holiday-calendars/{id}")).StatusCode);
        Assert.False(await hub.DbAsync(db => db.HolidayCalendars.AnyAsync(c => c.Id == id)));
    }

    [Fact]
    public async Task Adding_a_date_to_a_calendar_moves_the_next_run_off_it()
    {
        var admin = await hub.AdminHttpAsync("TENANT_ADMIN");
        var created = await Json(await admin.PostAsJsonAsync("/api/v1/holiday-calendars", new { tenantId = hub.TenantId, name = "Cal " + Tag(), country = "IN", dates = Array.Empty<object>() }));
        var id = created.GetProperty("id").GetGuid();
        var schedule = await Json(await admin.PostAsJsonAsync("/api/v1/schedules", ScheduleBody("Next run " + Tag(), "0 9 * * *", id)));
        var scheduleId = schedule.GetProperty("id").GetGuid();
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var before = await hub.DbAsync(db => db.NotificationSchedules.AsNoTracking().Where(s => s.Id == scheduleId).Select(s => s.NextRunAt).FirstAsync());
        var blocked = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(before!.Value, DateTimeKind.Utc), tz));

        (await admin.PutAsJsonAsync($"/api/v1/holiday-calendars/{id}", new { name = "Now blocked", country = "IN", dates = new[] { new { date = blocked, name = "New holiday" } } })).EnsureSuccessStatusCode();
        var after = await hub.DbAsync(db => db.NotificationSchedules.AsNoTracking().Where(s => s.Id == scheduleId).Select(s => s.NextRunAt).FirstAsync());
        Assert.NotEqual(blocked, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(after!.Value, DateTimeKind.Utc), tz)));
        (await admin.DeleteAsync($"/api/v1/schedules/{scheduleId}")).EnsureSuccessStatusCode();
    }
}
