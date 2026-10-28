IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [AdminUsers] (
        [Id] uniqueidentifier NOT NULL,
        [Email] nvarchar(400) NOT NULL,
        [DisplayName] nvarchar(400) NOT NULL,
        [PasswordHash] nvarchar(400) NULL,
        [ExternalSubject] nvarchar(400) NULL,
        [Enabled] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastLoginAt] datetime2 NULL,
        [FailedLogins] int NOT NULL,
        [LockedUntil] datetime2 NULL,
        CONSTRAINT [PK_AdminUsers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [ApplicationCredentials] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Name] nvarchar(400) NOT NULL,
        [ClientId] nvarchar(400) NOT NULL,
        [SecretHash] nvarchar(400) NOT NULL,
        [Scopes] nvarchar(2000) NOT NULL,
        [AllowedCompanyIds] nvarchar(4000) NOT NULL,
        [IsAiAgent] bit NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [RevokedAt] datetime2 NULL,
        [LastUsedAt] datetime2 NULL,
        [CreatedBy] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ApplicationCredentials] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [ApprovalRequests] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [EntityType] nvarchar(400) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [Title] nvarchar(400) NOT NULL,
        [PayloadJson] nvarchar(max) NULL,
        [PayloadEncrypted] bit NOT NULL,
        [Status] int NOT NULL,
        [RequestedBy] nvarchar(400) NOT NULL,
        [RequestedByName] nvarchar(400) NULL,
        [RequestedAt] datetime2 NOT NULL,
        [DecidedBy] nvarchar(400) NULL,
        [DecidedAt] datetime2 NULL,
        [Comment] nvarchar(2000) NULL,
        CONSTRAINT [PK_ApprovalRequests] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [ActorType] nvarchar(400) NOT NULL,
        [ActorId] nvarchar(400) NOT NULL,
        [ActorName] nvarchar(400) NULL,
        [Action] nvarchar(400) NOT NULL,
        [EntityType] nvarchar(400) NOT NULL,
        [EntityId] nvarchar(400) NULL,
        [Summary] nvarchar(2000) NULL,
        [DetailJson] nvarchar(max) NULL,
        [IpAddress] nvarchar(400) NULL,
        [CorrelationId] nvarchar(400) NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [BillingRates] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [UnitPrice] decimal(18,6) NULL,
        [MarginPercent] decimal(18,6) NOT NULL,
        [Currency] nvarchar(400) NOT NULL,
        CONSTRAINT [PK_BillingRates] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [BusinessEvents] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [EventType] nvarchar(400) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [IdempotencyKey] nvarchar(400) NULL,
        [CorrelationId] nvarchar(400) NULL,
        [ActorId] nvarchar(400) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        [ReceivedAt] datetime2 NOT NULL,
        [ProcessedAt] datetime2 NULL,
        [MatchedRules] int NOT NULL,
        [NotificationsCreated] int NOT NULL,
        [ProcessingError] nvarchar(4000) NULL,
        CONSTRAINT [PK_BusinessEvents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [Campaigns] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(2000) NULL,
        [Channels] nvarchar(400) NOT NULL,
        [TemplateCode] nvarchar(400) NOT NULL,
        [NotificationTypeCode] nvarchar(400) NULL,
        [MessageClass] int NOT NULL,
        [Status] int NOT NULL,
        [GroupIds] nvarchar(4000) NOT NULL,
        [SegmentJson] nvarchar(max) NULL,
        [VariablesJson] nvarchar(max) NULL,
        [ScheduledAt] datetime2 NULL,
        [ThrottlePerMinute] int NOT NULL,
        [Suspended] bit NOT NULL,
        [CreatedBy] nvarchar(400) NOT NULL,
        [CreatedByName] nvarchar(400) NULL,
        [ApprovedBy] nvarchar(400) NULL,
        [ApprovedAt] datetime2 NULL,
        [StartedAt] datetime2 NULL,
        [CompletedAt] datetime2 NULL,
        [TotalRecipients] int NOT NULL,
        [EstimatedCost] decimal(18,6) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Campaigns] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [ConversationThreads] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [ProviderAccountId] uniqueidentifier NULL,
        [ExternalAddress] nvarchar(400) NOT NULL,
        [SenderAddress] nvarchar(400) NULL,
        [ContactId] uniqueidentifier NULL,
        [ContactName] nvarchar(400) NULL,
        [LastMessageAt] datetime2 NOT NULL,
        [LastInboundAt] datetime2 NULL,
        [UnreadCount] int NOT NULL,
        [Closed] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ConversationThreads] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [CostRates] (
        [Id] uniqueidentifier NOT NULL,
        [ProviderAccountId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [CountryPrefix] nvarchar(400) NULL,
        [MessageClass] int NULL,
        [UnitCost] decimal(18,6) NOT NULL,
        [Currency] nvarchar(400) NOT NULL,
        [EffectiveFrom] datetime2 NOT NULL,
        CONSTRAINT [PK_CostRates] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [CreditAccounts] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [Balance] decimal(18,6) NOT NULL,
        [Reserved] decimal(18,6) NOT NULL,
        [LowBalanceThreshold] decimal(18,6) NOT NULL,
        [Currency] nvarchar(400) NOT NULL,
        [Enabled] bit NOT NULL,
        [LowBalanceAlertedAt] datetime2 NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CreditAccounts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [CreditTransactions] (
        [Id] uniqueidentifier NOT NULL,
        [AccountId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [Amount] decimal(18,6) NOT NULL,
        [DeliveryId] uniqueidentifier NULL,
        [Note] nvarchar(400) NULL,
        [Actor] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_CreditTransactions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [DeliveryAttempts] (
        [Id] uniqueidentifier NOT NULL,
        [DeliveryId] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [AttemptNumber] int NOT NULL,
        [ProviderAccountId] uniqueidentifier NULL,
        [ProviderType] nvarchar(400) NULL,
        [WorkerId] nvarchar(400) NULL,
        [IdempotencyToken] nvarchar(400) NULL,
        [Outcome] int NULL,
        [ProviderMessageId] nvarchar(400) NULL,
        [ResponseCode] nvarchar(400) NULL,
        [ResponseSummary] nvarchar(4000) NULL,
        [ErrorCode] nvarchar(400) NULL,
        [ErrorMessage] nvarchar(2000) NULL,
        [DurationMs] int NULL,
        [Cost] decimal(18,6) NULL,
        [StartedAt] datetime2 NOT NULL,
        [CompletedAt] datetime2 NULL,
        CONSTRAINT [PK_DeliveryAttempts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [DeliveryEvents] (
        [Id] uniqueidentifier NOT NULL,
        [DeliveryId] uniqueidentifier NULL,
        [TenantId] uniqueidentifier NULL,
        [ProviderAccountId] uniqueidentifier NULL,
        [ProviderMessageId] nvarchar(400) NULL,
        [ProviderEventId] nvarchar(400) NULL,
        [Type] nvarchar(400) NOT NULL,
        [Status] int NULL,
        [Source] nvarchar(400) NOT NULL,
        [Detail] nvarchar(4000) NULL,
        [Applied] bit NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        [RecordedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_DeliveryEvents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [DeviceRegistrations] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [ContactId] uniqueidentifier NULL,
        [UserId] nvarchar(400) NOT NULL,
        [Platform] int NOT NULL,
        [Provider] nvarchar(400) NOT NULL,
        [Token] nvarchar(2000) NOT NULL,
        [TokenHash] nvarchar(400) NOT NULL,
        [P256dh] nvarchar(400) NULL,
        [Auth] nvarchar(400) NULL,
        [DeviceName] nvarchar(400) NULL,
        [AppVersion] nvarchar(400) NULL,
        [Locale] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastSeenAt] datetime2 NOT NULL,
        [InvalidatedAt] datetime2 NULL,
        [InvalidReason] nvarchar(400) NULL,
        CONSTRAINT [PK_DeviceRegistrations] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [HolidayCalendars] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [Name] nvarchar(400) NOT NULL,
        [Country] nvarchar(400) NULL,
        CONSTRAINT [PK_HolidayCalendars] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [InAppMessages] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [DeliveryId] uniqueidentifier NOT NULL,
        [UserId] nvarchar(400) NOT NULL,
        [Title] nvarchar(400) NOT NULL,
        [Body] nvarchar(max) NOT NULL,
        [Category] nvarchar(400) NULL,
        [Priority] int NOT NULL,
        [DeepLink] nvarchar(400) NULL,
        [ImageUrl] nvarchar(400) NULL,
        [ActionsJson] nvarchar(4000) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [ReadAt] datetime2 NULL,
        [ArchivedAt] datetime2 NULL,
        [DismissedAt] datetime2 NULL,
        [ActionTaken] nvarchar(400) NULL,
        [ActedAt] datetime2 NULL,
        CONSTRAINT [PK_InAppMessages] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [InboundMessages] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [ProviderAccountId] uniqueidentifier NOT NULL,
        [ThreadId] uniqueidentifier NOT NULL,
        [From] nvarchar(400) NOT NULL,
        [To] nvarchar(400) NULL,
        [Subject] nvarchar(400) NULL,
        [Body] nvarchar(max) NULL,
        [MessageType] nvarchar(400) NOT NULL,
        [ProviderMessageId] nvarchar(400) NULL,
        [InReplyToProviderMessageId] nvarchar(400) NULL,
        [CorrelatedDeliveryId] uniqueidentifier NULL,
        [CorrelatedRequestId] uniqueidentifier NULL,
        [ContactId] uniqueidentifier NULL,
        [RoutingStatus] int NOT NULL,
        [RoutingDetail] nvarchar(1000) NULL,
        [ReceivedAt] datetime2 NOT NULL,
        [ReadAt] datetime2 NULL,
        CONSTRAINT [PK_InboundMessages] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [InboxDeduplication] (
        [Id] uniqueidentifier NOT NULL,
        [Consumer] nvarchar(400) NOT NULL,
        [MessageKey] nvarchar(400) NOT NULL,
        [ProcessedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_InboxDeduplication] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [MessageAttachments] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [Direction] nvarchar(400) NOT NULL,
        [InboundMessageId] uniqueidentifier NULL,
        [FileName] nvarchar(400) NOT NULL,
        [ContentType] nvarchar(400) NOT NULL,
        [Size] bigint NOT NULL,
        [Sha256] nvarchar(400) NOT NULL,
        [StorageKey] nvarchar(400) NOT NULL,
        [UploadedBy] nvarchar(400) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_MessageAttachments] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationRequests] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [Source] int NOT NULL,
        [ActorId] nvarchar(400) NOT NULL,
        [ActorName] nvarchar(400) NULL,
        [BusinessEventId] uniqueidentifier NULL,
        [RuleId] uniqueidentifier NULL,
        [CampaignId] uniqueidentifier NULL,
        [ScheduleId] uniqueidentifier NULL,
        [NotificationTypeCode] nvarchar(400) NULL,
        [TemplateCode] nvarchar(400) NULL,
        [Category] nvarchar(400) NULL,
        [MessageClass] int NOT NULL,
        [Priority] int NOT NULL,
        [Status] int NOT NULL,
        [ScheduleAt] datetime2 NULL,
        [ExpiresAt] datetime2 NULL,
        [IdempotencyKey] nvarchar(400) NULL,
        [RequestHash] nvarchar(400) NULL,
        [CorrelationId] nvarchar(400) NULL,
        [TraceId] nvarchar(400) NULL,
        [MetadataJson] nvarchar(4000) NULL,
        [VariablesCipher] nvarchar(max) NULL,
        [RetryPolicyJson] nvarchar(1000) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CompletedAt] datetime2 NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_NotificationRequests] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationRules] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(2000) NULL,
        [EventType] nvarchar(400) NOT NULL,
        [Enabled] bit NOT NULL,
        [Order] int NOT NULL,
        [StopProcessing] bit NOT NULL,
        [NotificationTypeCode] nvarchar(400) NULL,
        [RecipientSelectorJson] nvarchar(4000) NOT NULL,
        [ActionsJson] nvarchar(max) NOT NULL,
        [Priority] int NULL,
        [DelaySeconds] int NOT NULL,
        [EscalationJson] nvarchar(4000) NULL,
        [RetryPolicyJson] nvarchar(1000) NULL,
        [QuietHoursJson] nvarchar(2000) NULL,
        [CreatedBy] nvarchar(400) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_NotificationRules] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationSchedules] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [CronExpression] nvarchar(400) NULL,
        [SequenceOffsetsJson] nvarchar(400) NULL,
        [SequenceIndex] int NOT NULL,
        [TimeZone] nvarchar(400) NOT NULL,
        [StartAt] datetime2 NOT NULL,
        [EndAt] datetime2 NULL,
        [NextRunAt] datetime2 NULL,
        [LastRunAt] datetime2 NULL,
        [RunCount] int NOT NULL,
        [MaxRuns] int NULL,
        [WorkingDaysOnly] bit NOT NULL,
        [HolidayCalendarId] uniqueidentifier NULL,
        [RequestJson] nvarchar(max) NOT NULL,
        [Enabled] bit NOT NULL,
        [CreatedBy] nvarchar(400) NOT NULL,
        [CreatedByCredential] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_NotificationSchedules] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationTemplates] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Code] nvarchar(400) NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(2000) NULL,
        [Category] nvarchar(400) NULL,
        [Channel] int NOT NULL,
        [Locale] nvarchar(400) NOT NULL,
        [Status] int NOT NULL,
        [ActiveVersionId] uniqueidentifier NULL,
        [ActiveVersionNumber] int NULL,
        [LatestVersionNumber] int NOT NULL,
        [CreatedBy] nvarchar(400) NOT NULL,
        [ApprovedBy] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_NotificationTemplates] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationTypes] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Code] nvarchar(400) NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(2000) NULL,
        [Category] nvarchar(400) NOT NULL,
        [MessageClass] int NOT NULL,
        [DefaultChannels] nvarchar(400) NOT NULL,
        [DefaultTemplateCode] nvarchar(400) NULL,
        [DefaultPriority] int NOT NULL,
        [Mandatory] bit NOT NULL,
        [AiAllowed] bit NOT NULL,
        [RequiresHumanApprovalForAi] bit NOT NULL,
        [IsSensitive] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_NotificationTypes] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [Id] uniqueidentifier NOT NULL,
        [Type] nvarchar(400) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [CorrelationId] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [AvailableAt] datetime2 NOT NULL,
        [PublishedAt] datetime2 NULL,
        [LockedBy] nvarchar(400) NULL,
        [LockedUntil] datetime2 NULL,
        [Attempts] int NOT NULL,
        [LastError] nvarchar(2000) NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [ProviderAccounts] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Channel] int NOT NULL,
        [ProviderType] nvarchar(400) NOT NULL,
        [Scope] int NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NULL,
        [SettingsJson] nvarchar(max) NOT NULL,
        [SecretsCipher] nvarchar(max) NULL,
        [SecretKeys] nvarchar(2000) NOT NULL,
        [CallbackSecretCipher] nvarchar(2000) NULL,
        [Priority] int NOT NULL,
        [RateLimitPerSecond] float NULL,
        [MonthlyCapacity] int NULL,
        [AllowedCountryPrefixes] nvarchar(2000) NOT NULL,
        [UnitCost] decimal(18,6) NULL,
        [Currency] nvarchar(400) NOT NULL,
        [FailoverProviderAccountId] uniqueidentifier NULL,
        [DuplicateRiskPolicy] int NOT NULL,
        [TimeoutSeconds] int NOT NULL,
        [Enabled] bit NOT NULL,
        [Suspended] bit NOT NULL,
        [SuspendedReason] nvarchar(400) NULL,
        [HealthStatus] int NOT NULL,
        [LastHealthCheckAt] datetime2 NULL,
        [LastHealthDetail] nvarchar(2000) NULL,
        [PendingApprovalId] uniqueidentifier NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ProviderAccounts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [ProviderHealthEvents] (
        [Id] uniqueidentifier NOT NULL,
        [ProviderAccountId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [LatencyMs] int NULL,
        [Detail] nvarchar(2000) NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ProviderHealthEvents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [QueueMessages] (
        [Id] uniqueidentifier NOT NULL,
        [Queue] nvarchar(400) NOT NULL,
        [Type] nvarchar(400) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [MessageId] uniqueidentifier NOT NULL,
        [CorrelationId] nvarchar(400) NULL,
        [EnqueuedAt] datetime2 NOT NULL,
        [VisibleAt] datetime2 NOT NULL,
        [LeaseOwner] nvarchar(400) NULL,
        [LeaseExpiresAt] datetime2 NULL,
        [DequeueCount] int NOT NULL,
        [DeadLetteredAt] datetime2 NULL,
        [LastError] nvarchar(2000) NULL,
        CONSTRAINT [PK_QueueMessages] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [RecipientConsents] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ContactId] uniqueidentifier NOT NULL,
        [Channel] int NOT NULL,
        [Purpose] int NOT NULL,
        [Status] int NOT NULL,
        [Source] nvarchar(400) NOT NULL,
        [Evidence] nvarchar(2000) NULL,
        [Scope] nvarchar(400) NULL,
        [IpAddress] nvarchar(400) NULL,
        [RecordedBy] nvarchar(400) NOT NULL,
        [CapturedAt] datetime2 NOT NULL,
        [RecordedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RecipientConsents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [RecipientContacts] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [ExternalId] nvarchar(400) NOT NULL,
        [Name] nvarchar(400) NULL,
        [Email] nvarchar(400) NULL,
        [Mobile] nvarchar(400) NULL,
        [WhatsApp] nvarchar(400) NULL,
        [TimeZone] nvarchar(400) NULL,
        [Locale] nvarchar(400) NULL,
        [Country] nvarchar(400) NULL,
        [Roles] nvarchar(2000) NOT NULL,
        [AttributesJson] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ErasedAt] datetime2 NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_RecipientContacts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [RecipientGroupMembers] (
        [Id] uniqueidentifier NOT NULL,
        [GroupId] uniqueidentifier NOT NULL,
        [ContactId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_RecipientGroupMembers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [RecipientGroups] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(400) NULL,
        [Dynamic] bit NOT NULL,
        [FilterJson] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RecipientGroups] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [RecipientPreferences] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ContactId] uniqueidentifier NOT NULL,
        [Category] nvarchar(400) NOT NULL,
        [Channel] int NOT NULL,
        [Enabled] bit NOT NULL,
        [Source] nvarchar(400) NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RecipientPreferences] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [Suppressions] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [Address] nvarchar(400) NOT NULL,
        [Reason] int NOT NULL,
        [PromotionalOnly] bit NOT NULL,
        [Source] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NULL,
        CONSTRAINT [PK_Suppressions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [SystemAlerts] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [Severity] nvarchar(400) NOT NULL,
        [Source] nvarchar(400) NOT NULL,
        [Message] nvarchar(2000) NOT NULL,
        [Key] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [AcknowledgedAt] datetime2 NULL,
        [AcknowledgedBy] nvarchar(400) NULL,
        CONSTRAINT [PK_SystemAlerts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [TenantQuotas] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Channel] int NULL,
        [MonthlyLimit] int NOT NULL,
        [AlertThresholdPercent] int NOT NULL,
        CONSTRAINT [PK_TenantQuotas] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [Tenants] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(400) NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Status] int NOT NULL,
        [SuspendedReason] nvarchar(400) NULL,
        [AllowSharedProviders] bit NOT NULL,
        [RequireMarketingConsent] bit NOT NULL,
        [DefaultTimeZone] nvarchar(400) NOT NULL,
        [BrandingJson] nvarchar(max) NULL,
        [BodyRetentionDays] int NOT NULL,
        [DataRetentionDays] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Tenants] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [UsageRecords] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [Channel] int NOT NULL,
        [ProviderAccountId] uniqueidentifier NULL,
        [DeliveryId] uniqueidentifier NOT NULL,
        [CampaignId] uniqueidentifier NULL,
        [MessageClass] int NOT NULL,
        [Period] int NOT NULL,
        [Units] int NOT NULL,
        [EstimatedCost] decimal(18,6) NOT NULL,
        [ProviderReportedCost] decimal(18,6) NULL,
        [BilledAmount] decimal(18,6) NOT NULL,
        [Currency] nvarchar(400) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_UsageRecords] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [WebhookDeliveryAttempts] (
        [Id] uniqueidentifier NOT NULL,
        [SubscriptionId] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [EventId] uniqueidentifier NOT NULL,
        [EventType] nvarchar(400) NOT NULL,
        [AttemptNumber] int NOT NULL,
        [Success] bit NOT NULL,
        [ResponseCode] int NULL,
        [Error] nvarchar(2000) NULL,
        [DurationMs] int NULL,
        [NextRetryAt] datetime2 NULL,
        [AttemptedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_WebhookDeliveryAttempts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [WebhookSubscriptions] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Url] nvarchar(2000) NOT NULL,
        [EventTypes] nvarchar(2000) NOT NULL,
        [SecretCipher] nvarchar(2000) NOT NULL,
        [Enabled] bit NOT NULL,
        [ConsecutiveFailures] int NOT NULL,
        [DisabledAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_WebhookSubscriptions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [WorkerHeartbeats] (
        [Id] nvarchar(400) NOT NULL,
        [Worker] nvarchar(400) NOT NULL,
        [Instance] nvarchar(400) NOT NULL,
        [Status] nvarchar(400) NOT NULL,
        [Processed] bigint NOT NULL,
        [Failed] bigint NOT NULL,
        [LastError] nvarchar(2000) NULL,
        [StartedAt] datetime2 NOT NULL,
        [LastHeartbeatAt] datetime2 NOT NULL,
        CONSTRAINT [PK_WorkerHeartbeats] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [RoleAssignments] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Role] nvarchar(400) NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Channel] int NULL,
        CONSTRAINT [PK_RoleAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RoleAssignments_AdminUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AdminUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [CampaignRecipients] (
        [Id] uniqueidentifier NOT NULL,
        [CampaignId] uniqueidentifier NOT NULL,
        [ContactId] uniqueidentifier NULL,
        [ExternalId] nvarchar(400) NULL,
        [Name] nvarchar(400) NULL,
        [Email] nvarchar(400) NULL,
        [Mobile] nvarchar(400) NULL,
        [VariablesJson] nvarchar(4000) NULL,
        [Status] int NOT NULL,
        [NotificationRequestId] uniqueidentifier NULL,
        [Error] nvarchar(1000) NULL,
        [RowNumber] int NOT NULL,
        [ProcessedAt] datetime2 NULL,
        CONSTRAINT [PK_CampaignRecipients] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CampaignRecipients_Campaigns_CampaignId] FOREIGN KEY ([CampaignId]) REFERENCES [Campaigns] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [HolidayDates] (
        [Id] uniqueidentifier NOT NULL,
        [CalendarId] uniqueidentifier NOT NULL,
        [Date] date NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        CONSTRAINT [PK_HolidayDates] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_HolidayDates_HolidayCalendars_CalendarId] FOREIGN KEY ([CalendarId]) REFERENCES [HolidayCalendars] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationDeliveries] (
        [Id] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [RecipientId] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [CampaignId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [Address] nvarchar(400) NOT NULL,
        [DeviceId] uniqueidentifier NULL,
        [TemplateId] uniqueidentifier NULL,
        [TemplateVersionId] uniqueidentifier NULL,
        [TemplateVersionNumber] int NULL,
        [MessageClass] int NOT NULL,
        [Priority] int NOT NULL,
        [Category] nvarchar(400) NULL,
        [ContentCipher] nvarchar(max) NULL,
        [ContentPurgedAt] datetime2 NULL,
        [Status] int NOT NULL,
        [StatusReason] nvarchar(1000) NULL,
        [ProviderAccountId] uniqueidentifier NULL,
        [ProviderType] nvarchar(400) NULL,
        [SenderIdentityId] uniqueidentifier NULL,
        [ProviderMessageId] nvarchar(400) NULL,
        [PinnedProviderAccountId] uniqueidentifier NULL,
        [AttemptCount] int NOT NULL,
        [MaxAttempts] int NOT NULL,
        [NextAttemptAt] datetime2 NULL,
        [LeaseOwner] nvarchar(400) NULL,
        [LeaseExpiresAt] datetime2 NULL,
        [FailureCode] nvarchar(400) NULL,
        [FailureReason] nvarchar(2000) NULL,
        [AwaitingOperator] bit NOT NULL,
        [DeadLettered] bit NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [FallbackOfDeliveryId] uniqueidentifier NULL,
        [EscalationOfDeliveryId] uniqueidentifier NULL,
        [FallbackJson] nvarchar(2000) NULL,
        [EscalationJson] nvarchar(4000) NULL,
        [EscalateAt] datetime2 NULL,
        [FollowUpProcessed] bit NOT NULL,
        [ThreadId] uniqueidentifier NULL,
        [Units] int NOT NULL,
        [Cost] decimal(18,6) NULL,
        [EstimatedCost] decimal(18,6) NULL,
        [Currency] nvarchar(400) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [SubmittedAt] datetime2 NULL,
        [AcceptedAt] datetime2 NULL,
        [SentAt] datetime2 NULL,
        [DeliveredAt] datetime2 NULL,
        [ReadAt] datetime2 NULL,
        [FailedAt] datetime2 NULL,
        [CompletedAt] datetime2 NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_NotificationDeliveries] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationDeliveries_NotificationRequests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [NotificationRequests] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationRecipients] (
        [Id] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [ContactId] uniqueidentifier NULL,
        [UserId] nvarchar(400) NULL,
        [CustomerId] nvarchar(400) NULL,
        [Name] nvarchar(400) NULL,
        [Email] nvarchar(400) NULL,
        [Mobile] nvarchar(400) NULL,
        [WhatsApp] nvarchar(400) NULL,
        [TimeZone] nvarchar(400) NULL,
        [Locale] nvarchar(400) NULL,
        CONSTRAINT [PK_NotificationRecipients] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NotificationRecipients_NotificationRequests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [NotificationRequests] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [RuleConditions] (
        [Id] uniqueidentifier NOT NULL,
        [RuleId] uniqueidentifier NOT NULL,
        [ParentId] uniqueidentifier NULL,
        [Order] int NOT NULL,
        [GroupOperator] nvarchar(400) NULL,
        [Field] nvarchar(400) NULL,
        [Comparison] nvarchar(400) NULL,
        [ValueJson] nvarchar(4000) NULL,
        CONSTRAINT [PK_RuleConditions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RuleConditions_NotificationRules_RuleId] FOREIGN KEY ([RuleId]) REFERENCES [NotificationRules] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [TemplateVersions] (
        [Id] uniqueidentifier NOT NULL,
        [TemplateId] uniqueidentifier NOT NULL,
        [VersionNumber] int NOT NULL,
        [Status] int NOT NULL,
        [Subject] nvarchar(1000) NULL,
        [Body] nvarchar(max) NOT NULL,
        [BodyFormat] int NOT NULL,
        [TextBody] nvarchar(max) NULL,
        [ComponentsJson] nvarchar(max) NULL,
        [ProviderTemplateName] nvarchar(400) NULL,
        [ProviderTemplateLanguage] nvarchar(400) NULL,
        [ProviderCategory] nvarchar(400) NULL,
        [ProviderApprovalStatus] nvarchar(400) NULL,
        [DltTemplateId] nvarchar(400) NULL,
        [Checksum] nvarchar(400) NOT NULL,
        [ChangeNote] nvarchar(2000) NULL,
        [CreatedBy] nvarchar(400) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [SubmittedBy] nvarchar(400) NULL,
        [SubmittedAt] datetime2 NULL,
        [ApprovedBy] nvarchar(400) NULL,
        [ApprovedAt] datetime2 NULL,
        [RejectionReason] nvarchar(2000) NULL,
        CONSTRAINT [PK_TemplateVersions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TemplateVersions_NotificationTemplates_TemplateId] FOREIGN KEY ([TemplateId]) REFERENCES [NotificationTemplates] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [ProviderRoutingRules] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NULL,
        [ApplicationId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [CountryPrefix] nvarchar(400) NULL,
        [MessageClass] int NULL,
        [ProviderAccountId] uniqueidentifier NOT NULL,
        [Order] int NOT NULL,
        [Enabled] bit NOT NULL,
        CONSTRAINT [PK_ProviderRoutingRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ProviderRoutingRules_ProviderAccounts_ProviderAccountId] FOREIGN KEY ([ProviderAccountId]) REFERENCES [ProviderAccounts] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [SenderIdentities] (
        [Id] uniqueidentifier NOT NULL,
        [ProviderAccountId] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [CompanyId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [Address] nvarchar(400) NOT NULL,
        [DisplayName] nvarchar(400) NULL,
        [ReplyTo] nvarchar(400) NULL,
        [Signature] nvarchar(max) NULL,
        [DltEntityId] nvarchar(400) NULL,
        [WhatsAppPhoneNumberId] nvarchar(400) NULL,
        [WhatsAppBusinessAccountId] nvarchar(400) NULL,
        [IsDefault] bit NOT NULL,
        [Suspended] bit NOT NULL,
        [VerificationStatus] nvarchar(400) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_SenderIdentities] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SenderIdentities_ProviderAccounts_ProviderAccountId] FOREIGN KEY ([ProviderAccountId]) REFERENCES [ProviderAccounts] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [Applications] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Code] nvarchar(400) NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Description] nvarchar(2000) NULL,
        [Status] int NOT NULL,
        [SuspendedReason] nvarchar(400) NULL,
        [Sandbox] bit NOT NULL,
        [AllowedChannels] nvarchar(400) NOT NULL,
        [DefaultLocale] nvarchar(400) NOT NULL,
        [RateLimitPerMinute] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Applications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Applications_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [Companies] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Code] nvarchar(400) NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Country] nvarchar(400) NULL,
        [TimeZone] nvarchar(400) NULL,
        [Status] int NOT NULL,
        [SuspendedReason] nvarchar(400) NULL,
        [BrandingJson] nvarchar(max) NULL,
        [QuietHoursJson] nvarchar(2000) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Companies] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Companies_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [TemplateVariables] (
        [Id] uniqueidentifier NOT NULL,
        [TemplateVersionId] uniqueidentifier NOT NULL,
        [Name] nvarchar(400) NOT NULL,
        [Required] bit NOT NULL,
        [SampleValue] nvarchar(2000) NULL,
        [Description] nvarchar(400) NULL,
        [MaxLength] int NULL,
        CONSTRAINT [PK_TemplateVariables] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TemplateVariables_TemplateVersions_TemplateVersionId] FOREIGN KEY ([TemplateVersionId]) REFERENCES [TemplateVersions] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE TABLE [CompanyApplications] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [CompanyId] uniqueidentifier NOT NULL,
        [ApplicationId] uniqueidentifier NOT NULL,
        [Enabled] bit NOT NULL,
        [DefaultSendersJson] nvarchar(2000) NULL,
        CONSTRAINT [PK_CompanyApplications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CompanyApplications_Applications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CompanyApplications_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AdminUsers_Email] ON [AdminUsers] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AdminUsers_ExternalSubject] ON [AdminUsers] ([ExternalSubject]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApplicationCredentials_ApplicationId] ON [ApplicationCredentials] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApplicationCredentials_ClientId] ON [ApplicationCredentials] ([ClientId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Applications_TenantId_Code] ON [Applications] ([TenantId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApprovalRequests_EntityType_EntityId] ON [ApprovalRequests] ([EntityType], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApprovalRequests_TenantId_Status_RequestedAt] ON [ApprovalRequests] ([TenantId], [Status], [RequestedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EntityType_EntityId] ON [AuditLogs] ([EntityType], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_TenantId_OccurredAt] ON [AuditLogs] ([TenantId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_BusinessEvents_ApplicationId_IdempotencyKey] ON [BusinessEvents] ([ApplicationId], [IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_BusinessEvents_TenantId_ReceivedAt] ON [BusinessEvents] ([TenantId], [ReceivedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CampaignRecipients_CampaignId_Status] ON [CampaignRecipients] ([CampaignId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Campaigns_TenantId_Status] ON [Campaigns] ([TenantId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Companies_TenantId_Code] ON [Companies] ([TenantId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CompanyApplications_ApplicationId] ON [CompanyApplications] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CompanyApplications_CompanyId_ApplicationId] ON [CompanyApplications] ([CompanyId], [ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ConversationThreads_TenantId_Channel_ExternalAddress_SenderAddress] ON [ConversationThreads] ([TenantId], [Channel], [ExternalAddress], [SenderAddress]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ConversationThreads_TenantId_LastMessageAt] ON [ConversationThreads] ([TenantId], [LastMessageAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_CreditAccounts_TenantId_CompanyId] ON [CreditAccounts] ([TenantId], [CompanyId]) WHERE [CompanyId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CreditTransactions_AccountId_CreatedAt] ON [CreditTransactions] ([AccountId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DeliveryAttempts_DeliveryId_AttemptNumber] ON [DeliveryAttempts] ([DeliveryId], [AttemptNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_DeliveryAttempts_ProviderAccountId_StartedAt] ON [DeliveryAttempts] ([ProviderAccountId], [StartedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_DeliveryEvents_DeliveryId] ON [DeliveryEvents] ([DeliveryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_DeliveryEvents_ProviderAccountId_ProviderMessageId] ON [DeliveryEvents] ([ProviderAccountId], [ProviderMessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DeviceRegistrations_ApplicationId_TokenHash] ON [DeviceRegistrations] ([ApplicationId], [TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_DeviceRegistrations_CompanyId_UserId] ON [DeviceRegistrations] ([CompanyId], [UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_HolidayDates_CalendarId_Date] ON [HolidayDates] ([CalendarId], [Date]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InAppMessages_ApplicationId_CompanyId_UserId_CreatedAt] ON [InAppMessages] ([ApplicationId], [CompanyId], [UserId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InAppMessages_CreatedAt] ON [InAppMessages] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InAppMessages_DeliveryId] ON [InAppMessages] ([DeliveryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InboundMessages_ProviderAccountId_ProviderMessageId] ON [InboundMessages] ([ProviderAccountId], [ProviderMessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InboundMessages_TenantId_ReceivedAt] ON [InboundMessages] ([TenantId], [ReceivedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InboundMessages_ThreadId_ReceivedAt] ON [InboundMessages] ([ThreadId], [ReceivedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InboxDeduplication_Consumer_MessageKey] ON [InboxDeduplication] ([Consumer], [MessageKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InboxDeduplication_ProcessedAt] ON [InboxDeduplication] ([ProcessedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MessageAttachments_InboundMessageId] ON [MessageAttachments] ([InboundMessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_ApplicationId_CreatedAt] ON [NotificationDeliveries] ([ApplicationId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_CampaignId] ON [NotificationDeliveries] ([CampaignId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_Channel_Address] ON [NotificationDeliveries] ([Channel], [Address]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_EscalateAt] ON [NotificationDeliveries] ([EscalateAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_ProviderAccountId_ProviderMessageId] ON [NotificationDeliveries] ([ProviderAccountId], [ProviderMessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_RequestId] ON [NotificationDeliveries] ([RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_Status_NextAttemptAt] ON [NotificationDeliveries] ([Status], [NextAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_TenantId_CompanyId_CreatedAt] ON [NotificationDeliveries] ([TenantId], [CompanyId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_ThreadId] ON [NotificationDeliveries] ([ThreadId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRecipients_RequestId] ON [NotificationRecipients] ([RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRequests_ApplicationId_CreatedAt] ON [NotificationRequests] ([ApplicationId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_NotificationRequests_ApplicationId_IdempotencyKey] ON [NotificationRequests] ([ApplicationId], [IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRequests_BusinessEventId] ON [NotificationRequests] ([BusinessEventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRequests_CampaignId] ON [NotificationRequests] ([CampaignId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRequests_CorrelationId] ON [NotificationRequests] ([CorrelationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRequests_TenantId_CompanyId_CreatedAt] ON [NotificationRequests] ([TenantId], [CompanyId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationRules_TenantId_EventType_Enabled] ON [NotificationRules] ([TenantId], [EventType], [Enabled]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationSchedules_Enabled_NextRunAt] ON [NotificationSchedules] ([Enabled], [NextRunAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NotificationTemplates_TenantId_Code] ON [NotificationTemplates] ([TenantId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_NotificationTemplates_TenantId_CompanyId_ApplicationId_Code_Channel_Locale] ON [NotificationTemplates] ([TenantId], [CompanyId], [ApplicationId], [Code], [Channel], [Locale]) WHERE [CompanyId] IS NOT NULL AND [ApplicationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_NotificationTypes_TenantId_ApplicationId_Code] ON [NotificationTypes] ([TenantId], [ApplicationId], [Code]) WHERE [ApplicationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_PublishedAt_AvailableAt] ON [OutboxMessages] ([PublishedAt], [AvailableAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProviderAccounts_Channel_Enabled] ON [ProviderAccounts] ([Channel], [Enabled]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProviderAccounts_TenantId] ON [ProviderAccounts] ([TenantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProviderHealthEvents_ProviderAccountId_OccurredAt] ON [ProviderHealthEvents] ([ProviderAccountId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProviderRoutingRules_ProviderAccountId] ON [ProviderRoutingRules] ([ProviderAccountId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProviderRoutingRules_TenantId_Channel] ON [ProviderRoutingRules] ([TenantId], [Channel]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_QueueMessages_MessageId] ON [QueueMessages] ([MessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_QueueMessages_Queue_DeadLetteredAt_VisibleAt] ON [QueueMessages] ([Queue], [DeadLetteredAt], [VisibleAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RecipientConsents_ContactId_Channel_Purpose_CapturedAt] ON [RecipientConsents] ([ContactId], [Channel], [Purpose], [CapturedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RecipientContacts_CompanyId_Email] ON [RecipientContacts] ([CompanyId], [Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RecipientContacts_CompanyId_Kind_ExternalId] ON [RecipientContacts] ([CompanyId], [Kind], [ExternalId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RecipientContacts_CompanyId_Mobile] ON [RecipientContacts] ([CompanyId], [Mobile]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RecipientContacts_CompanyId_WhatsApp] ON [RecipientContacts] ([CompanyId], [WhatsApp]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RecipientGroupMembers_GroupId_ContactId] ON [RecipientGroupMembers] ([GroupId], [ContactId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RecipientGroups_CompanyId_Name] ON [RecipientGroups] ([CompanyId], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RecipientPreferences_ContactId_Category_Channel] ON [RecipientPreferences] ([ContactId], [Category], [Channel]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RoleAssignments_UserId] ON [RoleAssignments] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RuleConditions_RuleId] ON [RuleConditions] ([RuleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SenderIdentities_ProviderAccountId] ON [SenderIdentities] ([ProviderAccountId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Suppressions_TenantId_Channel_Address] ON [Suppressions] ([TenantId], [Channel], [Address]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SystemAlerts_AcknowledgedAt_CreatedAt] ON [SystemAlerts] ([AcknowledgedAt], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SystemAlerts_Key] ON [SystemAlerts] ([Key]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_TemplateVariables_TemplateVersionId_Name] ON [TemplateVariables] ([TemplateVersionId], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_TemplateVersions_TemplateId_VersionNumber] ON [TemplateVersions] ([TemplateId], [VersionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TenantQuotas_TenantId] ON [TenantQuotas] ([TenantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Tenants_Code] ON [Tenants] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UsageRecords_ApplicationId_Period] ON [UsageRecords] ([ApplicationId], [Period]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UsageRecords_CompanyId_Period] ON [UsageRecords] ([CompanyId], [Period]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UsageRecords_DeliveryId] ON [UsageRecords] ([DeliveryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UsageRecords_TenantId_Period_Channel] ON [UsageRecords] ([TenantId], [Period], [Channel]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_WebhookDeliveryAttempts_EventId] ON [WebhookDeliveryAttempts] ([EventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_WebhookDeliveryAttempts_SubscriptionId_AttemptedAt] ON [WebhookDeliveryAttempts] ([SubscriptionId], [AttemptedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_WebhookSubscriptions_ApplicationId_Enabled] ON [WebhookSubscriptions] ([ApplicationId], [Enabled]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028044929_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261028044929_InitialCreate', N'10.0.14');
END;

COMMIT;
GO

