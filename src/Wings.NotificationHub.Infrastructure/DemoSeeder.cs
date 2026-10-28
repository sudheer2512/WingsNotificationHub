using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Services;
using App = Wings.NotificationHub.Core.Domain.Application;

namespace Wings.NotificationHub.Infrastructure;

/// <summary>
/// System data (built-in providers, bootstrap administrator) and optional demo data: two tenants, companies,
/// applications, mock providers for every channel, templates, rules, contacts with consent, credentials.
/// Generated secrets are written once to App_Data/dev-credentials.env (never logged).
/// </summary>
public sealed class DemoSeeder(NotificationHubDbContext db, ISecretProtector protector, IConfiguration config, IOptions<HubOptions> options, ILogger<DemoSeeder> logger)
{
    public static readonly Guid InAppProviderId = Guid.Parse("00000000-0000-0000-0001-000000000001");
    public static readonly Guid WebhookProviderId = Guid.Parse("00000000-0000-0000-0001-000000000002");
    private readonly List<string> _credentialLines = [];

    private string CredentialsFile => Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "dev-credentials.env");

    public async Task EnsureSystemDataAsync(CancellationToken ct)
    {
        if (!await db.ProviderAccounts.AnyAsync(p => p.Id == InAppProviderId, ct))
            db.ProviderAccounts.Add(new ProviderAccount { Id = InAppProviderId, Name = "In-app inbox (built-in)", Channel = Channel.InApp, ProviderType = "inapp", Scope = ProviderScope.Shared, Priority = 1, DuplicateRiskPolicy = DuplicateRiskPolicy.AllowDuplicates, HealthStatus = ProviderHealthStatus.Healthy });
        if (!await db.ProviderAccounts.AnyAsync(p => p.Id == WebhookProviderId, ct))
            db.ProviderAccounts.Add(new ProviderAccount { Id = WebhookProviderId, Name = "Outbound webhooks (built-in)", Channel = Channel.Webhook, ProviderType = "webhook", Scope = ProviderScope.Shared, Priority = 1, DuplicateRiskPolicy = DuplicateRiskPolicy.AllowDuplicates, HealthStatus = ProviderHealthStatus.Healthy });

        if (!await db.AdminUsers.AnyAsync(ct))
        {
            var email = config["Bootstrap:AdminEmail"] ?? "admin@wings.local";
            var password = config["Bootstrap:AdminPassword"] ?? Hashing.NewSecret("Wnh-", 18);
            var admin = new AdminUser { Email = email, DisplayName = "Platform Administrator", PasswordHash = Hashing.HashPassword(password) };
            db.AdminUsers.Add(admin);
            db.RoleAssignments.Add(new RoleAssignment { UserId = admin.Id, Role = Roles.PlatformAdmin });
            if (config["Bootstrap:AdminPassword"] is null)
            {
                _credentialLines.Add($"ADMIN_EMAIL={email}");
                _credentialLines.Add($"ADMIN_PASSWORD={password}");
                logger.LogWarning("Created bootstrap administrator {Email}; the generated password was written to {File}.", email, CredentialsFile);
            }
        }
        await db.SaveChangesAsync(ct);
        await FlushCredentialsAsync(ct);
    }

    private async Task FlushCredentialsAsync(CancellationToken ct)
    {
        if (_credentialLines.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialsFile)!);
        await File.AppendAllLinesAsync(CredentialsFile, _credentialLines, ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CredentialsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _credentialLines.Clear();
    }

    public async Task SeedDemoAsync(CancellationToken ct)
    {
        if (await db.Tenants.AnyAsync(t => t.Code == "WINGS-DEMO", ct)) return;
        logger.LogInformation("Seeding demo data");
        var now = DateTime.UtcNow;

        // ── tenancy ──
        var tenant = new Tenant
        {
            Code = "WINGS-DEMO", Name = "Wings Demo Tenant", BrandingJson = Json.Serialize(new { inboxTitle = "Wings Notifications", primaryColor = "#1565c0", emailFooterHtml = "<p style=\"font-size:12px;color:#777\">Sent via Wings Notification Hub</p>" }),
        };
        var acmeTenant = new Tenant { Code = "ACME", Name = "Acme Holdings" };
        db.Tenants.AddRange(tenant, acmeTenant);
        var abc = new Company { TenantId = tenant.Id, Code = "ABC", Name = "ABC Traders Pvt Ltd", TimeZone = "Asia/Kolkata", QuietHoursJson = Json.Serialize(new QuietHoursPolicy { Start = "21:00", End = "08:00", AppliesTo = [MessageClass.Promotional] }) };
        var sunrise = new Company { TenantId = tenant.Id, Code = "SUNRISE", Name = "Sunrise Retail LLP", TimeZone = "Asia/Kolkata" };
        var acme = new Company { TenantId = acmeTenant.Id, Code = "ACME-RETAIL", Name = "Acme Retail Inc", Country = "US", TimeZone = "America/New_York" };
        db.Companies.AddRange(abc, sunrise, acme);

        App NewApp(Tenant t, string code, string name, bool sandbox = false) => new() { TenantId = t.Id, Code = code, Name = name, Sandbox = sandbox, AllowedChannels = [] };
        var erp = NewApp(tenant, "WINGS-ERP-AUTO", "Wings ERP Auto");
        var books = NewApp(tenant, "WINGS-BOOKS", "Wings Books");
        var payroll = NewApp(tenant, "WINGS-PAYROLL", "Wings Payroll");
        var serviceDesk = NewApp(tenant, "WINGS-SERVICE-DESK", "Wings Service Desk");
        var aiStudio = NewApp(tenant, "WINGS-AI-STUDIO", "Wings AI Studio");
        var acmeBooks = NewApp(acmeTenant, "WINGS-BOOKS", "Wings Books (Acme)");
        db.Applications.AddRange(erp, books, payroll, serviceDesk, aiStudio, acmeBooks);
        foreach (var (c, a) in new[] { (abc, erp), (abc, books), (abc, payroll), (abc, serviceDesk), (abc, aiStudio), (sunrise, erp), (sunrise, books), (acme, acmeBooks) })
            db.CompanyApplications.Add(new CompanyApplication { TenantId = c.TenantId, CompanyId = c.Id, ApplicationId = a.Id });

        // ── credentials ──
        void Credential(App? app, Tenant t, string clientId, string name, IEnumerable<string> scopes, bool ai = false)
        {
            var secret = Hashing.NewSecret("wnhs_");
            db.ApplicationCredentials.Add(new ApplicationCredential { TenantId = t.Id, ApplicationId = app?.Id, ClientId = clientId, Name = name, SecretHash = Hashing.HashSecret(secret), Scopes = scopes.ToList(), IsAiAgent = ai, CreatedBy = "seed" });
            var key = clientId.ToUpperInvariant().Replace('-', '_');
            _credentialLines.Add($"{key}_CLIENT_ID={clientId}");
            _credentialLines.Add($"{key}_CLIENT_SECRET={secret}");
        }
        Credential(erp, tenant, "erp-auto-demo", "ERP Auto server", Scopes.ApplicationDefaults);
        Credential(books, tenant, "books-demo", "Books server", Scopes.ApplicationDefaults);
        Credential(acmeBooks, acmeTenant, "acme-books-demo", "Acme Books server", Scopes.ApplicationDefaults);
        Credential(aiStudio, tenant, "ai-agent-demo", "Wings AI agent (MCP)", [Scopes.Mcp, Scopes.NotificationsSend, Scopes.NotificationsRead, Scopes.TemplatesRead, Scopes.PreferencesRead, Scopes.DevicesWrite, Scopes.UsageRead, Scopes.ProvidersHealth], ai: true);
        Credential(null, tenant, "wings-code-provisioning", "Wings Code deployment pipeline", [Scopes.ApplicationsRegister]);

        // ── admin users per role (maker-checker demos) ──
        foreach (var (email, name, role, company) in new (string, string, string, Guid?)[]
                 {
                     ("tenant.admin@wings.local", "Tenant Administrator", Roles.TenantAdmin, null),
                     ("designer@wings.local", "Template Designer", Roles.TemplateDesigner, null),
                     ("approver@wings.local", "Template Approver", Roles.TemplateApprover, null),
                     ("campaigns@wings.local", "Campaign Manager", Roles.CampaignManager, null),
                     ("operator@wings.local", "Notification Operator", Roles.Operator, null),
                     ("auditor@wings.local", "Auditor", Roles.Auditor, null),
                     ("abc.admin@wings.local", "ABC Company Administrator", Roles.CompanyAdmin, abc.Id),
                 })
        {
            if (await db.AdminUsers.AnyAsync(u => u.Email == email, ct)) continue;
            var pw = Hashing.NewSecret("Wnh-", 12);
            var u = new AdminUser { Email = email, DisplayName = name, PasswordHash = Hashing.HashPassword(pw) };
            db.AdminUsers.Add(u);
            db.RoleAssignments.Add(new RoleAssignment { UserId = u.Id, Role = role, TenantId = tenant.Id, CompanyId = company });
            _credentialLines.Add($"{email.Split('@')[0].ToUpperInvariant().Replace('.', '_')}_PASSWORD={pw}  # {email}");
        }

        // ── providers (shared mock accounts for every channel) ──
        ProviderAccount Mock(string name, Channel ch, int priority, double? rps = null, decimal? cost = null, string? settings = null, DuplicateRiskPolicy policy = DuplicateRiskPolicy.Never)
        {
            var p = new ProviderAccount
            {
                Name = name, Channel = ch, ProviderType = "mock", Scope = ProviderScope.Shared, Priority = priority, RateLimitPerSecond = rps, UnitCost = cost,
                SettingsJson = settings ?? "{\"latencyMs\":25,\"autoDeliver\":true}", DuplicateRiskPolicy = policy, HealthStatus = ProviderHealthStatus.Healthy,
                CallbackSecretCipher = protector.Protect(Hashing.NewSecret("cb_"), Purposes.CallbackSecret), TimeoutSeconds = 10,
            };
            db.ProviderAccounts.Add(p);
            return p;
        }
        var email1 = Mock("Mock Email (primary)", Channel.Email, 10, 50, 0.05m, "{\"latencyMs\":25,\"autoDeliver\":true,\"autoRead\":true}");
        var sms2 = Mock("Mock SMS (secondary)", Channel.Sms, 20, 20, 0.22m);
        var sms1 = Mock("Mock SMS (primary)", Channel.Sms, 10, 50, 0.18m);
        sms1.FailoverProviderAccountId = sms2.Id;
        sms1.AllowedCountryPrefixes = ["+91"];
        var wa = Mock("Mock WhatsApp Business", Channel.WhatsApp, 10, 40, 0.35m, "{\"latencyMs\":30,\"autoDeliver\":true,\"autoRead\":true}");
        Mock("Mock Push (FCM/APNs simulator)", Channel.Push, 10, 200, 0m, policy: DuplicateRiskPolicy.AllowDuplicates);
        Mock("Mock Web Push", Channel.WebPush, 10, 200, 0m, policy: DuplicateRiskPolicy.AllowDuplicates);
        var acmeEmail = new ProviderAccount { Name = "Acme dedicated mock email", Channel = Channel.Email, ProviderType = "mock", Scope = ProviderScope.Tenant, TenantId = acmeTenant.Id, Priority = 5, SettingsJson = "{\"latencyMs\":20}", HealthStatus = ProviderHealthStatus.Healthy };
        db.ProviderAccounts.Add(acmeEmail);
        db.SenderIdentities.AddRange(
            new SenderIdentity { ProviderAccountId = email1.Id, TenantId = tenant.Id, CompanyId = abc.Id, Channel = Channel.Email, Address = "billing@abctraders.example", DisplayName = "ABC Traders Billing", ReplyTo = "accounts@abctraders.example", IsDefault = true, VerificationStatus = "verified", Signature = "<p>Regards,<br>Accounts Team, ABC Traders</p>" },
            new SenderIdentity { ProviderAccountId = email1.Id, Channel = Channel.Email, Address = "noreply@wings.example", DisplayName = "Wings Notifications", IsDefault = true, VerificationStatus = "verified" },
            new SenderIdentity { ProviderAccountId = sms1.Id, TenantId = tenant.Id, Channel = Channel.Sms, Address = "ABCTRD", DltEntityId = "1101234567890123456", IsDefault = true, VerificationStatus = "verified" },
            new SenderIdentity { ProviderAccountId = sms2.Id, TenantId = tenant.Id, Channel = Channel.Sms, Address = "ABCTRD", DltEntityId = "1101234567890123456", IsDefault = true, VerificationStatus = "verified" },
            new SenderIdentity { ProviderAccountId = wa.Id, TenantId = tenant.Id, Channel = Channel.WhatsApp, Address = "+911140000000", DisplayName = "ABC Traders", WhatsAppPhoneNumberId = "demo-phone-number-id", WhatsAppBusinessAccountId = "demo-waba", IsDefault = true, VerificationStatus = "verified" },
            new SenderIdentity { ProviderAccountId = acmeEmail.Id, TenantId = acmeTenant.Id, Channel = Channel.Email, Address = "hello@acme.example", DisplayName = "Acme Retail", IsDefault = true, VerificationStatus = "verified" });

        // ── costs, billing, quotas, credits ──
        db.CostRates.AddRange(
            new CostRate { Channel = Channel.Sms, CountryPrefix = "+91", UnitCost = 0.18m, EffectiveFrom = now.AddYears(-1) },
            new CostRate { Channel = Channel.Sms, UnitCost = 3.50m, EffectiveFrom = now.AddYears(-1) },
            new CostRate { Channel = Channel.WhatsApp, MessageClass = MessageClass.Transactional, UnitCost = 0.13m, EffectiveFrom = now.AddYears(-1) },
            new CostRate { Channel = Channel.WhatsApp, MessageClass = MessageClass.Promotional, UnitCost = 0.88m, EffectiveFrom = now.AddYears(-1) },
            new CostRate { Channel = Channel.Email, UnitCost = 0.01m, EffectiveFrom = now.AddYears(-1) });
        db.BillingRates.AddRange(
            new BillingRate { Channel = Channel.Sms, MarginPercent = 25 },
            new BillingRate { Channel = Channel.WhatsApp, MarginPercent = 20 },
            new BillingRate { Channel = Channel.Email, UnitPrice = 0.02m });
        db.TenantQuotas.Add(new TenantQuota { TenantId = tenant.Id, Channel = Channel.Sms, MonthlyLimit = 100_000, AlertThresholdPercent = 80 });
        db.TenantQuotas.Add(new TenantQuota { TenantId = acmeTenant.Id, MonthlyLimit = 5_000 });
        db.CreditAccounts.Add(new CreditAccount { TenantId = tenant.Id, CompanyId = sunrise.Id, Balance = 500m, LowBalanceThreshold = 100m });

        // ── notification types ──
        NotificationType Type(App? app, string code, string name, string category, MessageClass cls, Channel[] channels, string template, NotificationPriority pr = NotificationPriority.Normal, bool ai = false, bool sensitive = false, bool mandatory = false, bool approval = true)
        {
            var t = new NotificationType { TenantId = tenant.Id, ApplicationId = app?.Id, Code = code, Name = name, Category = category, MessageClass = cls, DefaultChannels = [.. channels], DefaultTemplateCode = template, DefaultPriority = pr, AiAllowed = ai, IsSensitive = sensitive, Mandatory = mandatory, RequiresHumanApprovalForAi = approval };
            db.NotificationTypes.Add(t);
            return t;
        }
        Type(null, "INVOICE_CREATED", "Invoice created", "Invoice", MessageClass.Transactional, [Channel.Email, Channel.WhatsApp, Channel.InApp], "INVOICE_CREATED_V1", ai: true, approval: false);
        Type(null, "PAYMENT_OVERDUE", "Payment overdue reminder", "Payment", MessageClass.Transactional, [Channel.Sms, Channel.Email], "PAYMENT_OVERDUE_V1", NotificationPriority.High, ai: true, approval: true);
        Type(null, "ORDER_APPROVED", "Sales order approved", "Approval", MessageClass.Transactional, [Channel.InApp, Channel.Push], "ORDER_APPROVED_V1");
        Type(payroll, "LEAVE_APPROVED", "Leave approved", "HR", MessageClass.Transactional, [Channel.Email, Channel.InApp], "LEAVE_APPROVED_V1");
        Type(serviceDesk, "SERVICE_JOB_COMPLETED", "Service job completed", "Service", MessageClass.Transactional, [Channel.WhatsApp, Channel.Sms], "SERVICE_JOB_COMPLETED_V1");
        Type(serviceDesk, "TICKET_UPDATED", "Customer ticket updated", "Support", MessageClass.Transactional, [Channel.Email, Channel.Push], "TICKET_UPDATED_V1", ai: true, approval: false);
        Type(null, "STOCK_BELOW_REORDER", "Stock below reorder level", "Inventory", MessageClass.Transactional, [Channel.InApp], "STOCK_BELOW_REORDER_V1");
        Type(null, "BACKUP_FAILED", "ERP backup failed", "Security", MessageClass.Critical, [Channel.Email, Channel.Push], "BACKUP_FAILED_V1", NotificationPriority.Critical);
        Type(null, "DEPLOYMENT_COMPLETED", "Application deployment completed", "Operations", MessageClass.Transactional, [Channel.Email, Channel.InApp], "DEPLOYMENT_COMPLETED_V1");
        Type(null, "APPROVAL_PENDING", "Workflow approval pending", "Approval", MessageClass.Transactional, [Channel.Push, Channel.InApp], "APPROVAL_PENDING_V1", NotificationPriority.High);
        Type(null, "PROMO_FESTIVE", "Festive offer", "Promotions", MessageClass.Promotional, [Channel.Email, Channel.Sms], "PROMO_FESTIVE_V1");
        Type(null, "OTP_LOGIN", "Login one-time password", "Security", MessageClass.Critical, [Channel.Sms], "OTP_LOGIN_V1", NotificationPriority.Critical, sensitive: true);

        // ── templates (seeded active, approved by the system) ──
        void Tpl(string code, string name, Channel ch, string? subject, string body, BodyFormat fmt = BodyFormat.Text, string? category = null, string? components = null,
            string? waName = null, string? dlt = null, App? app = null, params (string Name, string Sample)[] samples)
        {
            var t = new NotificationTemplate { TenantId = tenant.Id, ApplicationId = app?.Id, Code = code, Name = name, Channel = ch, Locale = "en", Category = category, Status = TemplateStatus.Active, CreatedBy = "seed", ApprovedBy = "seed", LatestVersionNumber = 1, ActiveVersionNumber = 1 };
            var v = new TemplateVersion
            {
                TemplateId = t.Id, VersionNumber = 1, Status = TemplateStatus.Active, Subject = subject, Body = body, BodyFormat = fmt, ComponentsJson = components,
                ProviderTemplateName = waName, ProviderTemplateLanguage = waName is null ? null : "en", ProviderCategory = waName is null ? null : "UTILITY",
                ProviderApprovalStatus = waName is null ? null : "APPROVED", DltTemplateId = dlt, CreatedBy = "seed", SubmittedBy = "seed", ApprovedBy = "seed", ApprovedAt = now, Checksum = "seed",
                ChangeNote = "Initial version",
            };
            foreach (var (n, required) in TemplateService.TemplateUsage(subject, body))
                v.Variables.Add(new TemplateVariable { TemplateVersionId = v.Id, Name = n, Required = required, SampleValue = samples.FirstOrDefault(s => s.Name == n).Sample });
            t.ActiveVersionId = v.Id;
            t.Versions.Add(v);
            db.NotificationTemplates.Add(t);
        }
        (string, string)[] inv = [("CustomerName", "ABC Traders"), ("InvoiceNumber", "INV-10045"), ("InvoiceAmount", "25000"), ("InvoiceDate", "2026-10-28")];
        Tpl("INVOICE_CREATED_V1", "Invoice created", Channel.Email, "Invoice {{InvoiceNumber}} from {{CompanyName}}",
            "<p>Dear {{CustomerName}},</p><p>Your invoice <strong>{{InvoiceNumber}}</strong> dated {{InvoiceDate | date:dd-MMM-yyyy}} for <strong>{{InvoiceAmount | currency:INR}}</strong> has been generated.</p><p>Thank you for your business.</p><p>Regards,<br>{{CompanyName}}</p>",
            BodyFormat.Html, "Invoice", samples: inv);
        Tpl("INVOICE_CREATED_V1", "Invoice created", Channel.WhatsApp, null,
            "Dear {{CustomerName}}, your invoice {{InvoiceNumber}} dated {{InvoiceDate}} for ₹{{InvoiceAmount}} has been generated. Thank you for your business. - {{CompanyName}}",
            category: "Invoice", waName: "invoice_created_v1",
            components: Json.Serialize(new TemplateComponents { WhatsApp = new WhatsAppComponents { HeaderFormat = "TEXT", HeaderText = "Invoice {{InvoiceNumber}}", FooterText = "Reply STOP to opt out", Buttons = [new WhatsAppButtonDefinition { Type = "QUICK_REPLY", Text = "Send statement" }] } }),
            samples: inv);
        Tpl("INVOICE_CREATED_V1", "Invoice created", Channel.Sms, null, "Dear {{CustomerName}}, invoice {{InvoiceNumber}} for Rs.{{InvoiceAmount}} dated {{InvoiceDate}} generated. -{{CompanyName}}", category: "Invoice", dlt: "1107160000000000001", samples: inv);
        Tpl("INVOICE_CREATED_V1", "Invoice created", Channel.InApp, "Invoice {{InvoiceNumber}} created", "{{CustomerName}}: {{InvoiceAmount | currency:INR}} dated {{InvoiceDate}}", category: "Invoice",
            components: Json.Serialize(new TemplateComponents { Title = "Invoice {{InvoiceNumber}} created", Push = new PushComponents { DeepLink = "/sales/invoices/{{InvoiceNumber}}" } }), samples: inv);
        Tpl("INVOICE_CREATED_V1", "Invoice created", Channel.Webhook, "Invoice {{InvoiceNumber}}", "Invoice {{InvoiceNumber}} for {{InvoiceAmount}}", category: "Invoice", samples: inv);
        (string, string)[] pay = [("CustomerName", "ABC Traders"), ("InvoiceNumber", "INV-10045"), ("AmountDue", "25000"), ("DueDate", "2026-10-15"), ("DaysOverdue", "13")];
        Tpl("PAYMENT_OVERDUE_V1", "Payment overdue", Channel.Sms, null, "Dear {{CustomerName}}, payment of Rs.{{AmountDue}} for {{InvoiceNumber}} is {{DaysOverdue}} days overdue. Please pay at the earliest. -{{CompanyName}}", category: "Payment", dlt: "1107160000000000002", samples: pay);
        Tpl("PAYMENT_OVERDUE_V1", "Payment overdue", Channel.Email, "Payment reminder: {{InvoiceNumber}} is overdue",
            "<p>Dear {{CustomerName}},</p><p>Our records show that payment of <strong>{{AmountDue | currency:INR}}</strong> for invoice {{InvoiceNumber}} was due on {{DueDate | date}} ({{DaysOverdue}} days ago).</p>{{#if PaymentLink}}<p><a href=\"{{PaymentLink | url}}\">Pay now</a></p>{{/if}}<p>Regards,<br>{{CompanyName}}</p>",
            BodyFormat.Html, "Payment", samples: pay);
        Tpl("PAYMENT_OVERDUE_V1", "Payment overdue", Channel.WhatsApp, null, "Dear {{CustomerName}}, payment of ₹{{AmountDue}} for invoice {{InvoiceNumber}} is {{DaysOverdue}} days overdue. Reply to this message if you need your account statement.", category: "Payment", waName: "payment_overdue_v1", samples: pay);
        (string, string)[] ord = [("OrderNumber", "SO-2231"), ("OrderAmount", "184500"), ("ApprovedBy", "Rahul Verma")];
        Tpl("ORDER_APPROVED_V1", "Sales order approved", Channel.InApp, "Order {{OrderNumber}} approved", "Sales order {{OrderNumber}} ({{OrderAmount | currency:INR}}) was approved by {{ApprovedBy}}.", category: "Approval",
            components: Json.Serialize(new TemplateComponents { Title = "Order {{OrderNumber}} approved", Push = new PushComponents { DeepLink = "/sales/orders/{{OrderNumber}}" } }), samples: ord);
        Tpl("ORDER_APPROVED_V1", "Sales order approved", Channel.Push, "Order {{OrderNumber}} approved", "{{OrderAmount | currency:INR}} approved by {{ApprovedBy}}", category: "Approval",
            components: Json.Serialize(new TemplateComponents { Title = "Order {{OrderNumber}} approved", Push = new PushComponents { DeepLink = "wingserp://orders/{{OrderNumber}}" } }), samples: ord);
        (string, string)[] leave = [("EmployeeName", "Priya Sharma"), ("FromDate", "2026-11-03"), ("ToDate", "2026-11-05"), ("Approver", "Rahul Verma")];
        Tpl("LEAVE_APPROVED_V1", "Leave approved", Channel.Email, "Your leave request has been approved", "<p>Hi {{EmployeeName}},</p><p>Your leave from {{FromDate | date}} to {{ToDate | date}} was approved by {{Approver}}.</p>", BodyFormat.Html, "HR", app: payroll, samples: leave);
        Tpl("LEAVE_APPROVED_V1", "Leave approved", Channel.InApp, "Leave approved", "{{FromDate}} → {{ToDate}} approved by {{Approver}}", category: "HR", app: payroll, samples: leave);
        (string, string)[] job = [("CustomerName", "ABC Traders"), ("JobNumber", "SJ-778"), ("Technician", "Imran")];
        Tpl("SERVICE_JOB_COMPLETED_V1", "Service job completed", Channel.WhatsApp, null, "Hello {{CustomerName}}, service job {{JobNumber}} has been completed by {{Technician}}. Thank you!", category: "Service", waName: "service_job_completed", app: serviceDesk, samples: job);
        Tpl("SERVICE_JOB_COMPLETED_V1", "Service job completed", Channel.Sms, null, "Service job {{JobNumber}} completed by {{Technician}}. -{{CompanyName}}", category: "Service", dlt: "1107160000000000003", app: serviceDesk, samples: job);
        (string, string)[] tk = [("TicketNumber", "TCK-5521"), ("Status", "In progress"), ("Comment", "Engineer assigned")];
        Tpl("TICKET_UPDATED_V1", "Ticket updated", Channel.Email, "Ticket {{TicketNumber}}: {{Status}}", "<p>Your ticket {{TicketNumber}} is now <b>{{Status}}</b>.</p><p>{{Comment}}</p>", BodyFormat.Html, "Support", app: serviceDesk, samples: tk);
        Tpl("TICKET_UPDATED_V1", "Ticket updated", Channel.Push, "Ticket {{TicketNumber}} updated", "{{Status}}: {{Comment}}", category: "Support", app: serviceDesk, samples: tk);
        (string, string)[] st = [("ItemName", "A4 Paper Ream"), ("Quantity", "12"), ("ReorderLevel", "50"), ("Warehouse", "Main")];
        Tpl("STOCK_BELOW_REORDER_V1", "Stock below reorder level", Channel.InApp, "Low stock: {{ItemName}}", "{{ItemName}} at {{Warehouse}} is {{Quantity}} (reorder level {{ReorderLevel}}).", category: "Inventory", samples: st);
        (string, string)[] bk = [("ServerName", "ERP-DB-01"), ("ErrorMessage", "Disk full"), ("FailedAt", "2026-10-28T02:00:00Z")];
        Tpl("BACKUP_FAILED_V1", "Backup failed", Channel.Email, "CRITICAL: ERP backup failed on {{ServerName}}", "<p><b>The scheduled backup failed.</b></p><p>Server: {{ServerName}}<br>Time: {{FailedAt}}<br>Error: {{ErrorMessage}}</p>", BodyFormat.Html, "Security", samples: bk);
        Tpl("BACKUP_FAILED_V1", "Backup failed", Channel.Push, "Backup failed: {{ServerName}}", "{{ErrorMessage}}", category: "Security", samples: bk);
        (string, string)[] dep = [("AppName", "Wings ERP Auto"), ("Version", "4.2.0"), ("Environment", "Production")];
        Tpl("DEPLOYMENT_COMPLETED_V1", "Deployment completed", Channel.Email, "{{AppName}} {{Version}} deployed to {{Environment}}", "<p>{{AppName}} version {{Version}} was deployed to {{Environment}}.</p>", BodyFormat.Html, "Operations", samples: dep);
        Tpl("DEPLOYMENT_COMPLETED_V1", "Deployment completed", Channel.InApp, "Deployment completed", "{{AppName}} {{Version}} → {{Environment}}", category: "Operations", samples: dep);
        (string, string)[] ap = [("DocumentType", "Purchase order"), ("DocumentNumber", "PO-9001"), ("RequestedBy", "Anita Desai")];
        Tpl("APPROVAL_PENDING_V1", "Approval pending", Channel.Push, "Approval needed: {{DocumentNumber}}", "{{DocumentType}} {{DocumentNumber}} from {{RequestedBy}} awaits your approval.", category: "Approval", samples: ap);
        Tpl("APPROVAL_PENDING_V1", "Approval pending", Channel.InApp, "Approval needed: {{DocumentNumber}}", "{{DocumentType}} {{DocumentNumber}} from {{RequestedBy}} awaits your approval.", category: "Approval",
            components: Json.Serialize(new TemplateComponents { Title = "Approval needed: {{DocumentNumber}}" }), samples: ap);
        (string, string)[] promo = [("CustomerName", "ABC Traders"), ("Discount", "15"), ("ValidTill", "2026-11-15")];
        Tpl("PROMO_FESTIVE_V1", "Festive offer", Channel.Email, "{{Discount}}% festive discount for you, {{CustomerName}}", "<p>Dear {{CustomerName}},</p><p>Enjoy <b>{{Discount}}% off</b> until {{ValidTill | date}}.</p><p><a href=\"{{UnsubscribeUrl}}\">Unsubscribe</a></p>", BodyFormat.Html, "Promotions", samples: promo);
        Tpl("PROMO_FESTIVE_V1", "Festive offer", Channel.Sms, null, "{{Discount}}% festive offer till {{ValidTill}}! -{{CompanyName}}. Opt out: {{UnsubscribeUrl}}", category: "Promotions", dlt: "1107160000000000009", samples: promo);
        Tpl("OTP_LOGIN_V1", "Login OTP", Channel.Sms, null, "{{Otp}} is your Wings login code. It expires in 5 minutes. Do not share it.", category: "Security", dlt: "1107160000000000010", samples: [("Otp", "482913")]);

        // ── contacts, consent, preferences, devices ──
        RecipientContact Contact(Company c, ContactKind kind, string ext, string name, string? email, string? mobile, params string[] roles)
        {
            var x = new RecipientContact { TenantId = c.TenantId, CompanyId = c.Id, Kind = kind, ExternalId = ext, Name = name, Email = email, Mobile = mobile, WhatsApp = mobile, TimeZone = "Asia/Kolkata", Locale = "en", Country = c.Country, Roles = [.. roles] };
            db.RecipientContacts.Add(x);
            return x;
        }
        var cust1 = Contact(abc, ContactKind.Customer, "CUST-1001", "ABC Traders (Ramesh)", "customer@example.com", "+919876543210");
        var cust2 = Contact(abc, ContactKind.Customer, "CUST-1002", "Gupta Stores", "gupta.stores@example.com", "+919812345678");
        var cust3 = Contact(abc, ContactKind.Customer, "CUST-1003", "Mehta Enterprises", "mehta@example.com", "+919899001122");
        var priya = Contact(abc, ContactKind.User, "u-1001", "Priya Sharma", "priya.sharma@abctraders.example", "+919800000001", "Accountant", "Approver");
        var rahul = Contact(abc, ContactKind.User, "u-1002", "Rahul Verma", "rahul.verma@abctraders.example", "+919800000002", "Manager", "Approver");
        var anita = Contact(abc, ContactKind.User, "u-1003", "Anita Desai", "anita.desai@abctraders.example", "+919800000003", "StoreKeeper");
        Contact(abc, ContactKind.User, "u-admin", "ERP Administrator", "it@abctraders.example", "+919800000009", "Administrator");
        Contact(acme, ContactKind.Customer, "ACME-C1", "Acme Customer", "buyer@acme.example", "+12025550123");
        void Consent(RecipientContact c, Channel ch, ConsentPurpose p, ConsentStatus s = ConsentStatus.Granted, string source = "web-form")
            => db.RecipientConsents.Add(new RecipientConsent { TenantId = c.TenantId, CompanyId = c.CompanyId, ContactId = c.Id, Channel = ch, Purpose = p, Status = s, Source = source, Evidence = $"seed:{source}", RecordedBy = "seed", CapturedAt = now.AddDays(-30) });
        Consent(cust1, Channel.WhatsApp, ConsentPurpose.ChannelOptIn, source: "whatsapp-optin-keyword");
        Consent(cust1, Channel.Email, ConsentPurpose.Marketing);
        Consent(cust1, Channel.Sms, ConsentPurpose.Marketing);
        Consent(cust3, Channel.WhatsApp, ConsentPurpose.ChannelOptIn, source: "crm");
        Consent(cust3, Channel.Email, ConsentPurpose.Marketing, ConsentStatus.Withdrawn, "unsubscribe-link");
        db.RecipientPreferences.Add(new RecipientPreference { TenantId = tenant.Id, CompanyId = abc.Id, ContactId = cust3.Id, Category = "Invoice", Channel = Channel.Sms, Enabled = false, Source = "customer-portal" });
        db.RecipientPreferences.Add(new RecipientPreference { TenantId = tenant.Id, CompanyId = abc.Id, ContactId = priya.Id, Category = "Approval", Channel = Channel.Email, Enabled = false, Source = "user-settings" });
        foreach (var (u, platform, token) in new[] { (priya, DevicePlatform.Android, "demo-fcm-token-priya-pixel"), (rahul, DevicePlatform.Ios, "demo-apns-token-rahul-iphone"), (rahul, DevicePlatform.Web, "https://push.example/demo/rahul-chrome") })
            db.DeviceRegistrations.Add(new DeviceRegistration { TenantId = tenant.Id, CompanyId = abc.Id, ApplicationId = erp.Id, ContactId = u.Id, UserId = u.ExternalId, Platform = platform, Provider = platform switch { DevicePlatform.Android => "fcm", DevicePlatform.Ios => "apns", _ => "webpush" }, Token = token, TokenHash = Core.Addressing.Sha256Hex(token), DeviceName = $"{u.Name}'s {platform}", P256dh = platform == DevicePlatform.Web ? "BDemoKey" : null, Auth = platform == DevicePlatform.Web ? "demo" : null });

        var approvers = new RecipientGroup { TenantId = tenant.Id, CompanyId = abc.Id, Name = "Approvers", Description = "Users who approve documents" };
        var vip = new RecipientGroup { TenantId = tenant.Id, CompanyId = abc.Id, Name = "Customers with WhatsApp", Dynamic = true, FilterJson = Json.Serialize(new ConditionNode { Op = "and", Children = [new ConditionNode { Field = "kind", Comparison = "eq", Value = "Customer" }, new ConditionNode { Field = "whatsapp", Comparison = "notempty" }] }) };
        db.RecipientGroups.AddRange(approvers, vip);
        db.RecipientGroupMembers.AddRange(new RecipientGroupMember { GroupId = approvers.Id, ContactId = priya.Id }, new RecipientGroupMember { GroupId = approvers.Id, ContactId = rahul.Id });

        // ── rules for the business events in the specification ──
        void Rule(string name, string eventType, string typeCode, RecipientSelector who, RuleChannelAction[] actions, ConditionNode? when = null, NotificationPriority? pr = null, EscalationDto? esc = null, App? app = null, int order = 0)
        {
            var r = new NotificationRule
            {
                TenantId = tenant.Id, ApplicationId = app?.Id, Name = name, EventType = eventType, NotificationTypeCode = typeCode, RecipientSelectorJson = Json.Serialize(who),
                ActionsJson = Json.Serialize(actions), Priority = pr, EscalationJson = esc is null ? null : Json.Serialize(esc), CreatedBy = "seed", Order = order,
            };
            r.Conditions = ConditionTree.Flatten(r.Id, when);
            db.NotificationRules.Add(r);
        }
        var cust = new RecipientSelector { Type = "payload", Path = "customer" };
        Rule("Invoice created → email + WhatsApp", "invoice.created", "INVOICE_CREATED", cust,
            [new() { Channel = Channel.Email, TemplateCode = "INVOICE_CREATED_V1" }, new() { Channel = Channel.WhatsApp, TemplateCode = "INVOICE_CREATED_V1", FallbackChannel = Channel.Sms, FallbackTemplateCode = "INVOICE_CREATED_V1" }]);
        Rule("Payment overdue → SMS + email reminder", "payment.overdue", "PAYMENT_OVERDUE", cust,
            [new() { Channel = Channel.Sms, TemplateCode = "PAYMENT_OVERDUE_V1" }, new() { Channel = Channel.Email, TemplateCode = "PAYMENT_OVERDUE_V1" }],
            new ConditionNode { Op = "and", Children = [new ConditionNode { Field = "payload.DaysOverdue", Comparison = "gte", Value = 7 }, new ConditionNode { Op = "or", Children = [new ConditionNode { Field = "payload.AmountDue", Comparison = "gt", Value = 10000 }, new ConditionNode { Field = "payload.customer.tier", Comparison = "eq", Value = "gold" }] }] },
            NotificationPriority.High);
        Rule("Sales order approved → in-app + push", "salesorder.approved", "ORDER_APPROVED", new RecipientSelector { Type = "payload", Path = "requester" },
            [new() { Channel = Channel.InApp, TemplateCode = "ORDER_APPROVED_V1" }, new() { Channel = Channel.Push, TemplateCode = "ORDER_APPROVED_V1" }]);
        Rule("Leave approved → email + in-app", "leave.approved", "LEAVE_APPROVED", new RecipientSelector { Type = "payload", Path = "employee" },
            [new() { Channel = Channel.Email, TemplateCode = "LEAVE_APPROVED_V1" }, new() { Channel = Channel.InApp, TemplateCode = "LEAVE_APPROVED_V1" }], app: payroll);
        Rule("Service job completed → WhatsApp (SMS fallback)", "servicejob.completed", "SERVICE_JOB_COMPLETED", cust,
            [new() { Channel = Channel.WhatsApp, TemplateCode = "SERVICE_JOB_COMPLETED_V1", FallbackChannel = Channel.Sms, FallbackTemplateCode = "SERVICE_JOB_COMPLETED_V1" }], app: serviceDesk);
        Rule("Ticket updated → email + push", "ticket.updated", "TICKET_UPDATED", cust,
            [new() { Channel = Channel.Email, TemplateCode = "TICKET_UPDATED_V1" }, new() { Channel = Channel.Push, TemplateCode = "TICKET_UPDATED_V1" }], app: serviceDesk);
        Rule("Stock below reorder → in-app to store keepers", "stock.below_reorder", "STOCK_BELOW_REORDER", new RecipientSelector { Type = "roles", Roles = ["StoreKeeper", "Manager"] },
            [new() { Channel = Channel.InApp, TemplateCode = "STOCK_BELOW_REORDER_V1" }], new ConditionNode { Field = "payload.Quantity", Comparison = "lt", Value = 50 });
        Rule("ERP backup failed → critical email + push", "backup.failed", "BACKUP_FAILED", new RecipientSelector { Type = "roles", Roles = ["Administrator", "Manager"] },
            [new() { Channel = Channel.Email, TemplateCode = "BACKUP_FAILED_V1" }, new() { Channel = Channel.Push, TemplateCode = "BACKUP_FAILED_V1" }], pr: NotificationPriority.Critical,
            esc: new EscalationDto { AfterMinutes = 15, UnlessStatus = DeliveryStatus.Read, Channel = Channel.Sms, TemplateCode = "BACKUP_FAILED_V1" });
        Rule("Deployment completed → email + in-app", "deployment.completed", "DEPLOYMENT_COMPLETED", new RecipientSelector { Type = "roles", Roles = ["Administrator"] },
            [new() { Channel = Channel.Email, TemplateCode = "DEPLOYMENT_COMPLETED_V1" }, new() { Channel = Channel.InApp, TemplateCode = "DEPLOYMENT_COMPLETED_V1" }]);
        Rule("Workflow approval pending → push + in-app", "approval.pending", "APPROVAL_PENDING", new RecipientSelector { Type = "group", GroupId = approvers.Id },
            [new() { Channel = Channel.Push, TemplateCode = "APPROVAL_PENDING_V1" }, new() { Channel = Channel.InApp, TemplateCode = "APPROVAL_PENDING_V1" }], pr: NotificationPriority.High);

        // ── calendar, schedule, campaign draft, webhook ──
        var cal = new HolidayCalendar { TenantId = tenant.Id, Name = "India 2026", Country = "IN" };
        foreach (var (d, n) in new[] { (new DateOnly(2026, 11, 8), "Diwali"), (new DateOnly(2026, 11, 24), "Guru Nanak Jayanti"), (new DateOnly(2026, 12, 25), "Christmas") })
            cal.Dates.Add(new HolidayDate { CalendarId = cal.Id, Date = d, Name = n });
        db.HolidayCalendars.Add(cal);
        db.Campaigns.Add(new Campaign
        {
            TenantId = tenant.Id, CompanyId = abc.Id, ApplicationId = erp.Id, Name = "Festive offer 2026", Description = "Diwali discount to opted-in customers",
            Channels = [Channel.Email, Channel.Sms], TemplateCode = "PROMO_FESTIVE_V1", NotificationTypeCode = "PROMO_FESTIVE", GroupIds = [vip.Id],
            VariablesJson = Json.Serialize(new { Discount = "15", ValidTill = "2026-11-15" }), ThrottlePerMinute = 120, CreatedBy = "seed", CreatedByName = "Seed data",
        });
        var sinkUrl = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/api/v1/dev/webhook-sink";
        var whSecret = Hashing.NewSecret("whsec_");
        db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            TenantId = tenant.Id, ApplicationId = erp.Id, Name = "ERP Auto (dev sink)", Url = sinkUrl,
            EventTypes = [WebhookEventTypes.DeliveryStatusChanged, WebhookEventTypes.NotificationCompleted, WebhookEventTypes.InboundMessageReceived, WebhookEventTypes.NotificationWebhook],
            SecretCipher = protector.Protect(whSecret, Purposes.WebhookSecret),
        });
        _credentialLines.Add($"ERP_WEBHOOK_SIGNING_SECRET={whSecret}");
        _credentialLines.Add($"DEMO_TENANT_ID={tenant.Id}");
        _credentialLines.Add($"DEMO_COMPANY_ABC_ID={abc.Id}");
        _credentialLines.Add($"DEMO_COMPANY_SUNRISE_ID={sunrise.Id}");
        _credentialLines.Add($"DEMO_APP_ERP_ID={erp.Id}");
        _credentialLines.Add($"DEMO_APP_SERVICEDESK_ID={serviceDesk.Id}");
        _credentialLines.Add($"ACME_COMPANY_ID={acme.Id}");

        await db.SaveChangesAsync(ct);
        await FlushCredentialsAsync(ct);
        logger.LogInformation("Demo data seeded; credentials written to {File}", CredentialsFile);
    }
}
