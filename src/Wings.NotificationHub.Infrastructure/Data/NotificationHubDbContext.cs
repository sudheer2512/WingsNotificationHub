using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using App = Wings.NotificationHub.Core.Domain.Application;

namespace Wings.NotificationHub.Infrastructure.Data;

public sealed class NotificationHubDbContext(DbContextOptions<NotificationHubDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<App> Applications => Set<App>();
    public DbSet<CompanyApplication> CompanyApplications => Set<CompanyApplication>();
    public DbSet<ApplicationCredential> ApplicationCredentials => Set<ApplicationCredential>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<NotificationType> NotificationTypes => Set<NotificationType>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<TemplateVariable> TemplateVariables => Set<TemplateVariable>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<RuleCondition> RuleConditions => Set<RuleCondition>();
    public DbSet<BusinessEvent> BusinessEvents => Set<BusinessEvent>();
    public DbSet<NotificationRequest> NotificationRequests => Set<NotificationRequest>();
    public DbSet<NotificationRecipient> NotificationRecipients => Set<NotificationRecipient>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<DeliveryEvent> DeliveryEvents => Set<DeliveryEvent>();
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
    public DbSet<SenderIdentity> SenderIdentities => Set<SenderIdentity>();
    public DbSet<ProviderRoutingRule> ProviderRoutingRules => Set<ProviderRoutingRule>();
    public DbSet<ProviderHealthEvent> ProviderHealthEvents => Set<ProviderHealthEvent>();
    public DbSet<RecipientContact> RecipientContacts => Set<RecipientContact>();
    public DbSet<RecipientPreference> RecipientPreferences => Set<RecipientPreference>();
    public DbSet<RecipientConsent> RecipientConsents => Set<RecipientConsent>();
    public DbSet<Suppression> Suppressions => Set<Suppression>();
    public DbSet<RecipientGroup> RecipientGroups => Set<RecipientGroup>();
    public DbSet<RecipientGroupMember> RecipientGroupMembers => Set<RecipientGroupMember>();
    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    public DbSet<InAppMessage> InAppMessages => Set<InAppMessage>();
    public DbSet<NotificationSchedule> NotificationSchedules => Set<NotificationSchedule>();
    public DbSet<HolidayCalendar> HolidayCalendars => Set<HolidayCalendar>();
    public DbSet<HolidayDate> HolidayDates => Set<HolidayDate>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<ConversationThread> ConversationThreads => Set<ConversationThread>();
    public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<CostRate> CostRates => Set<CostRate>();
    public DbSet<BillingRate> BillingRates => Set<BillingRate>();
    public DbSet<TenantQuota> TenantQuotas => Set<TenantQuota>();
    public DbSet<CreditAccount> CreditAccounts => Set<CreditAccount>();
    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<QueueMessage> QueueMessages => Set<QueueMessage>();
    public DbSet<InboxDeduplication> InboxDeduplication => Set<InboxDeduplication>();
    public DbSet<SystemAlert> SystemAlerts => Set<SystemAlert>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Stamp()
    {
        foreach (var e in ChangeTracker.Entries<IVersioned>())
            if (e.State == EntityState.Modified) e.Entity.ConcurrencyStamp = Guid.NewGuid();
        foreach (var e in ChangeTracker.Entries<NotificationDelivery>())
            if (e.State == EntityState.Modified) e.Entity.UpdatedAt = DateTime.UtcNow;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<string>().HaveMaxLength(400);
        b.Properties<decimal>().HavePrecision(18, 6);
        b.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        // Every key is set client-side (GUIDs); EF must not treat preset keys as "existing" rows.
        foreach (var et in m.Model.GetEntityTypes())
        {
            var key = et.FindPrimaryKey();
            if (key?.Properties is [{ ClrType: var t } p] && t == typeof(Guid)) p.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        m.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.BrandingJson).HasMaxLength(8000);
            Stamp(e);
        });
        m.Entity<Company>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.BrandingJson).HasMaxLength(8000);
            e.Property(x => x.QuietHoursJson).HasMaxLength(2000);
            Stamp(e);
        });
        m.Entity<App>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.AllowedChannels).HasConversion(JsonListConverter<Channel>(), JsonListComparer<Channel>()).HasMaxLength(400);
            e.Property(x => x.Description).HasMaxLength(2000);
            Stamp(e);
        });
        m.Entity<CompanyApplication>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.ApplicationId }).IsUnique();
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<App>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DefaultSendersJson).HasMaxLength(2000);
        });
        m.Entity<ApplicationCredential>(e =>
        {
            e.HasIndex(x => x.ClientId).IsUnique();
            e.HasIndex(x => x.ApplicationId);
            e.Property(x => x.Scopes).HasConversion(JsonListConverter<string>(), JsonListComparer<string>()).HasMaxLength(2000);
            e.Property(x => x.AllowedCompanyIds).HasConversion(JsonListConverter<Guid>(), JsonListComparer<Guid>()).HasMaxLength(4000);
        });
        m.Entity<AdminUser>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.ExternalSubject);
        });
        m.Entity<RoleAssignment>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ApprovalRequest>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAt });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.PayloadJson).HasMaxLength(-1);
            e.Property(x => x.Comment).HasMaxLength(2000);
        });
        m.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.DetailJson).HasMaxLength(-1);
            e.Property(x => x.Summary).HasMaxLength(2000);
        });

        m.Entity<NotificationType>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.ApplicationId, x.Code }).IsUnique();
            e.Property(x => x.DefaultChannels).HasConversion(JsonListConverter<Channel>(), JsonListComparer<Channel>()).HasMaxLength(400);
            e.Property(x => x.Description).HasMaxLength(2000);
            Stamp(e);
        });
        m.Entity<NotificationTemplate>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.ApplicationId, x.Code, x.Channel, x.Locale }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Code });
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Description).HasMaxLength(2000);
            Stamp(e);
        });
        m.Entity<TemplateVersion>(e =>
        {
            e.HasIndex(x => new { x.TemplateId, x.VersionNumber }).IsUnique();
            e.Property(x => x.Body).HasMaxLength(-1);
            e.Property(x => x.TextBody).HasMaxLength(-1);
            e.Property(x => x.ComponentsJson).HasMaxLength(-1);
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.Property(x => x.ChangeNote).HasMaxLength(2000);
            e.Property(x => x.RejectionReason).HasMaxLength(2000);
            e.HasMany(x => x.Variables).WithOne().HasForeignKey(v => v.TemplateVersionId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TemplateVariable>(e =>
        {
            e.HasIndex(x => new { x.TemplateVersionId, x.Name }).IsUnique();
            e.Property(x => x.SampleValue).HasMaxLength(2000);
        });

        m.Entity<NotificationRule>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EventType, x.Enabled });
            e.HasMany(x => x.Conditions).WithOne().HasForeignKey(c => c.RuleId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.RecipientSelectorJson).HasMaxLength(4000);
            e.Property(x => x.ActionsJson).HasMaxLength(8000);
            e.Property(x => x.EscalationJson).HasMaxLength(4000);
            e.Property(x => x.RetryPolicyJson).HasMaxLength(1000);
            e.Property(x => x.QuietHoursJson).HasMaxLength(2000);
            e.Property(x => x.Description).HasMaxLength(2000);
            Stamp(e);
        });
        m.Entity<RuleCondition>(e =>
        {
            e.HasIndex(x => x.RuleId);
            e.Property(x => x.ValueJson).HasMaxLength(4000);
        });
        m.Entity<BusinessEvent>(e =>
        {
            e.HasIndex(x => new { x.ApplicationId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.ReceivedAt });
            e.Property(x => x.PayloadJson).HasMaxLength(-1);
            e.Property(x => x.ProcessingError).HasMaxLength(4000);
        });

        m.Entity<NotificationRequest>(e =>
        {
            e.HasIndex(x => new { x.ApplicationId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.CreatedAt });
            e.HasIndex(x => new { x.ApplicationId, x.CreatedAt });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.CampaignId);
            e.HasIndex(x => x.BusinessEventId);
            e.HasMany(x => x.Recipients).WithOne().HasForeignKey(r => r.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Deliveries).WithOne().HasForeignKey(d => d.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.VariablesCipher).HasMaxLength(-1);
            e.Property(x => x.MetadataJson).HasMaxLength(4000);
            e.Property(x => x.RetryPolicyJson).HasMaxLength(1000);
            Stamp(e);
        });
        m.Entity<NotificationRecipient>(e => e.HasIndex(x => x.RequestId));
        m.Entity<NotificationDelivery>(e =>
        {
            // Dispatch/scheduler scans, explorer filters and provider-callback correlation.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.CreatedAt });
            e.HasIndex(x => new { x.ApplicationId, x.CreatedAt });
            e.HasIndex(x => new { x.ProviderAccountId, x.ProviderMessageId });
            e.HasIndex(x => new { x.Channel, x.Address });
            e.HasIndex(x => x.CampaignId);
            e.HasIndex(x => x.EscalateAt);
            e.HasIndex(x => x.ThreadId);
            e.Property(x => x.ContentCipher).HasMaxLength(-1);
            e.Property(x => x.FailureReason).HasMaxLength(2000);
            e.Property(x => x.StatusReason).HasMaxLength(1000);
            e.Property(x => x.FallbackJson).HasMaxLength(2000);
            e.Property(x => x.EscalationJson).HasMaxLength(4000);
            Stamp(e);
        });
        m.Entity<DeliveryAttempt>(e =>
        {
            e.HasIndex(x => new { x.DeliveryId, x.AttemptNumber }).IsUnique();
            e.HasIndex(x => new { x.ProviderAccountId, x.StartedAt });
            e.Property(x => x.ResponseSummary).HasMaxLength(4000);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
        });
        m.Entity<DeliveryEvent>(e =>
        {
            e.HasIndex(x => x.DeliveryId);
            e.HasIndex(x => new { x.ProviderAccountId, x.ProviderMessageId });
            e.Property(x => x.Detail).HasMaxLength(4000);
        });

        m.Entity<ProviderAccount>(e =>
        {
            e.HasIndex(x => new { x.Channel, x.Enabled });
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.SettingsJson).HasMaxLength(8000);
            e.Property(x => x.SecretsCipher).HasMaxLength(-1);
            e.Property(x => x.CallbackSecretCipher).HasMaxLength(2000);
            e.Property(x => x.LastHealthDetail).HasMaxLength(2000);
            e.Property(x => x.SecretKeys).HasConversion(JsonListConverter<string>(), JsonListComparer<string>()).HasMaxLength(2000);
            e.Property(x => x.AllowedCountryPrefixes).HasConversion(JsonListConverter<string>(), JsonListComparer<string>()).HasMaxLength(2000);
            Stamp(e);
        });
        m.Entity<SenderIdentity>(e =>
        {
            e.HasIndex(x => x.ProviderAccountId);
            e.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => x.ProviderAccountId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Signature).HasMaxLength(8000);
        });
        m.Entity<ProviderRoutingRule>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Channel });
            e.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => x.ProviderAccountId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ProviderHealthEvent>(e =>
        {
            e.HasIndex(x => new { x.ProviderAccountId, x.OccurredAt });
            e.Property(x => x.Detail).HasMaxLength(2000);
        });

        m.Entity<RecipientContact>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Kind, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Email });
            e.HasIndex(x => new { x.CompanyId, x.Mobile });
            e.HasIndex(x => new { x.CompanyId, x.WhatsApp });
            e.Property(x => x.Roles).HasConversion(JsonListConverter<string>(), JsonListComparer<string>()).HasMaxLength(2000);
            e.Property(x => x.AttributesJson).HasMaxLength(8000);
            Stamp(e);
        });
        m.Entity<RecipientPreference>(e => e.HasIndex(x => new { x.ContactId, x.Category, x.Channel }).IsUnique());
        m.Entity<RecipientConsent>(e =>
        {
            e.HasIndex(x => new { x.ContactId, x.Channel, x.Purpose, x.CapturedAt });
            e.Property(x => x.Evidence).HasMaxLength(2000);
        });
        m.Entity<Suppression>(e => e.HasIndex(x => new { x.TenantId, x.Channel, x.Address }));
        m.Entity<RecipientGroup>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
            e.Property(x => x.FilterJson).HasMaxLength(8000);
        });
        m.Entity<RecipientGroupMember>(e => e.HasIndex(x => new { x.GroupId, x.ContactId }).IsUnique());
        m.Entity<DeviceRegistration>(e =>
        {
            e.HasIndex(x => new { x.ApplicationId, x.TokenHash }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.UserId });
            e.Property(x => x.Token).HasMaxLength(2000);
        });
        m.Entity<InAppMessage>(e =>
        {
            e.HasIndex(x => new { x.ApplicationId, x.CompanyId, x.UserId, x.CreatedAt });
            e.HasIndex(x => x.DeliveryId).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Body).HasMaxLength(8000);
            e.Property(x => x.ActionsJson).HasMaxLength(4000);
        });

        m.Entity<NotificationSchedule>(e =>
        {
            e.HasIndex(x => new { x.Enabled, x.NextRunAt });
            e.Property(x => x.RequestJson).HasMaxLength(-1);
            Stamp(e);
        });
        m.Entity<HolidayCalendar>(e => e.HasMany(x => x.Dates).WithOne().HasForeignKey(d => d.CalendarId).OnDelete(DeleteBehavior.Cascade));
        m.Entity<HolidayDate>(e => e.HasIndex(x => new { x.CalendarId, x.Date }).IsUnique());
        m.Entity<Campaign>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.Channels).HasConversion(JsonListConverter<Channel>(), JsonListComparer<Channel>()).HasMaxLength(400);
            e.Property(x => x.GroupIds).HasConversion(JsonListConverter<Guid>(), JsonListComparer<Guid>()).HasMaxLength(4000);
            e.Property(x => x.SegmentJson).HasMaxLength(8000);
            e.Property(x => x.VariablesJson).HasMaxLength(8000);
            e.Property(x => x.Description).HasMaxLength(2000);
            Stamp(e);
        });
        m.Entity<CampaignRecipient>(e =>
        {
            e.HasIndex(x => new { x.CampaignId, x.Status });
            e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.VariablesJson).HasMaxLength(4000);
            e.Property(x => x.Error).HasMaxLength(1000);
        });

        m.Entity<ConversationThread>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Channel, x.ExternalAddress, x.SenderAddress });
            e.HasIndex(x => new { x.TenantId, x.LastMessageAt });
        });
        m.Entity<InboundMessage>(e =>
        {
            e.HasIndex(x => new { x.ThreadId, x.ReceivedAt });
            e.HasIndex(x => new { x.ProviderAccountId, x.ProviderMessageId });
            e.HasIndex(x => new { x.TenantId, x.ReceivedAt });
            e.Property(x => x.Body).HasMaxLength(-1);
            e.Property(x => x.RoutingDetail).HasMaxLength(1000);
        });
        m.Entity<MessageAttachment>(e => e.HasIndex(x => x.InboundMessageId));

        m.Entity<WebhookSubscription>(e =>
        {
            e.HasIndex(x => new { x.ApplicationId, x.Enabled });
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.SecretCipher).HasMaxLength(2000);
            e.Property(x => x.EventTypes).HasConversion(JsonListConverter<string>(), JsonListComparer<string>()).HasMaxLength(2000);
        });
        m.Entity<WebhookDeliveryAttempt>(e =>
        {
            e.HasIndex(x => new { x.SubscriptionId, x.AttemptedAt });
            e.HasIndex(x => x.EventId);
            e.Property(x => x.Error).HasMaxLength(2000);
        });

        m.Entity<UsageRecord>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Period, x.Channel });
            e.HasIndex(x => new { x.CompanyId, x.Period });
            e.HasIndex(x => new { x.ApplicationId, x.Period });
            e.HasIndex(x => x.DeliveryId);
        });
        m.Entity<TenantQuota>(e => e.HasIndex(x => x.TenantId));
        m.Entity<CreditAccount>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.CompanyId }).IsUnique();
            Stamp(e);
        });
        m.Entity<CreditTransaction>(e => e.HasIndex(x => new { x.AccountId, x.CreatedAt }));

        m.Entity<OutboxMessage>(e =>
        {
            e.HasIndex(x => new { x.PublishedAt, x.AvailableAt });
            e.Property(x => x.PayloadJson).HasMaxLength(-1);
            e.Property(x => x.LastError).HasMaxLength(2000);
        });
        m.Entity<QueueMessage>(e =>
        {
            e.HasIndex(x => new { x.Queue, x.DeadLetteredAt, x.VisibleAt });
            e.HasIndex(x => x.MessageId);
            e.Property(x => x.PayloadJson).HasMaxLength(-1);
            e.Property(x => x.LastError).HasMaxLength(2000);
        });
        m.Entity<InboxDeduplication>(e =>
        {
            e.HasIndex(x => new { x.Consumer, x.MessageKey }).IsUnique();
            e.HasIndex(x => x.ProcessedAt);
        });
        m.Entity<SystemAlert>(e =>
        {
            e.HasIndex(x => new { x.AcknowledgedAt, x.CreatedAt });
            e.HasIndex(x => x.Key);
            e.Property(x => x.Message).HasMaxLength(2000);
        });
        m.Entity<WorkerHeartbeat>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LastError).HasMaxLength(2000);
        });
    }

    private static void Stamp<T>(EntityTypeBuilder<T> e) where T : class, IVersioned
        => e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<T>, string> JsonListConverter<T>()
        => new(v => JsonSerializer.Serialize(v, JsonOpts), v => string.IsNullOrEmpty(v) ? new List<T>() : JsonSerializer.Deserialize<List<T>>(v, JsonOpts) ?? new List<T>());

    private static ValueComparer<List<T>> JsonListComparer<T>()
        => new((a, b) => (a ?? new List<T>()).SequenceEqual(b ?? new List<T>()), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)), v => v.ToList());
}

/// <summary>All timestamps are stored as UTC; values read back are marked UTC.</summary>
public sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public static class QueryExtensions
{
    /// <summary>
    /// Applies an <see cref="Core.Security.AccessFilter"/> to any entity with TenantId and optional CompanyId/ApplicationId
    /// properties. Rows whose company/application is null (tenant-wide) are visible to narrower clauses only when
    /// <paramref name="includeBroader"/> is set (read access to shared definitions).
    /// </summary>
    public static IQueryable<T> Visible<T>(this IQueryable<T> query, Core.Security.AccessFilter filter, bool includeBroader = false)
    {
        if (filter.IsUnrestricted) return query;
        if (filter.IsEmpty) return query.Where(_ => false);
        var p = Expression.Parameter(typeof(T), "e");
        var tenantProp = typeof(T).GetProperty("TenantId") ?? throw new InvalidOperationException($"{typeof(T).Name} has no TenantId.");
        var companyProp = typeof(T).GetProperty("CompanyId");
        var appProp = typeof(T).GetProperty("ApplicationId");
        Expression? body = null;
        foreach (var c in filter.Clauses)
        {
            Expression clause = Equal(Expression.Property(p, tenantProp), c.TenantId);
            if (c.CompanyIds is { Count: > 0 } companies && companyProp is not null)
                clause = Expression.AndAlso(clause, InList(Expression.Property(p, companyProp), companies, includeBroader));
            if (c.ApplicationId is { } app && appProp is not null)
                clause = Expression.AndAlso(clause, InList(Expression.Property(p, appProp), [app], includeBroader));
            body = body is null ? clause : Expression.OrElse(body, clause);
        }
        return query.Where(Expression.Lambda<Func<T, bool>>(body!, p));
    }

    private static Expression Equal(Expression member, Guid value)
        => member.Type == typeof(Guid?)
            ? Expression.Equal(member, Expression.Constant(value, typeof(Guid?)))
            : Expression.Equal(member, Expression.Constant(value));

    private static Expression InList(Expression member, IReadOnlyList<Guid> ids, bool includeNull)
    {
        Expression? any = null;
        foreach (var id in ids)
        {
            var eq = Equal(member, id);
            any = any is null ? eq : Expression.OrElse(any, eq);
        }
        if (includeNull && member.Type == typeof(Guid?))
            any = Expression.OrElse(any!, Expression.Equal(member, Expression.Constant(null, typeof(Guid?))));
        return any!;
    }
}
