using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wings.NotificationHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ExternalSubject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedLogins = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AllowedCompanyIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsAiAgent = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    PayloadEncrypted = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RequestedByName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    MarginPercent = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ActorId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MatchedRules = table.Column<int>(type: "int", nullable: false),
                    NotificationsCreated = table.Column<int>(type: "int", nullable: false),
                    ProcessingError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Channels = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TemplateCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    NotificationTypeCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MessageClass = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    GroupIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SegmentJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    VariablesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    ScheduledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ThrottlePerMinute = table.Column<int>(type: "int", nullable: false),
                    Suspended = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedByName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalRecipients = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConversationThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SenderAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastInboundAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UnreadCount = table.Column<int>(type: "int", nullable: false),
                    Closed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationThreads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CostRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    CountryPrefix = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MessageClass = table.Column<int>(type: "int", nullable: true),
                    UnitCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CreditAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Balance = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Reserved = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    LowBalanceThreshold = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LowBalanceAlertedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CreditTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    WorkerId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IdempotencyToken = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ResponseCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ResponseSummary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ProviderEventId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Applied = table.Column<bool>(type: "bit", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Platform = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    P256dh = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Auth = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DeviceName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AppVersion = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    InvalidatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InvalidReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HolidayCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HolidayCalendars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InAppMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    DeepLink = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ImageUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ActionsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActionTaken = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ActedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InAppMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboundMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    From = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    To = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    MessageType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    InReplyToProviderMessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CorrelatedDeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelatedRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoutingStatus = table.Column<int>(type: "int", nullable: false),
                    RoutingDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxDeduplication",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Consumer = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    MessageKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxDeduplication", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    InboundMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BusinessEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotificationTypeCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TemplateCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MessageClass = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ScheduleAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RequestHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    VariablesCipher = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    RetryPolicyJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    StopProcessing = table.Column<bool>(type: "bit", nullable: false),
                    NotificationTypeCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RecipientSelectorJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ActionsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: true),
                    DelaySeconds = table.Column<int>(type: "int", nullable: false),
                    EscalationJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RetryPolicyJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    QuietHoursJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SequenceOffsetsJson = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SequenceIndex = table.Column<int>(type: "int", nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    StartAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RunCount = table.Column<int>(type: "int", nullable: false),
                    MaxRuns = table.Column<int>(type: "int", nullable: true),
                    WorkingDaysOnly = table.Column<bool>(type: "bit", nullable: false),
                    HolidayCalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedByCredential = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ActiveVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveVersionNumber = table.Column<int>(type: "int", nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    MessageClass = table.Column<int>(type: "int", nullable: false),
                    DefaultChannels = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DefaultTemplateCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DefaultPriority = table.Column<int>(type: "int", nullable: false),
                    Mandatory = table.Column<bool>(type: "bit", nullable: false),
                    AiAllowed = table.Column<bool>(type: "bit", nullable: false),
                    RequiresHumanApprovalForAi = table.Column<bool>(type: "bit", nullable: false),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AvailableAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ProviderType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    SecretsCipher = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    SecretKeys = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CallbackSecretCipher = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    RateLimitPerSecond = table.Column<double>(type: "float", nullable: true),
                    MonthlyCapacity = table.Column<int>(type: "int", nullable: true),
                    AllowedCountryPrefixes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FailoverProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DuplicateRiskPolicy = table.Column<int>(type: "int", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Suspended = table.Column<bool>(type: "bit", nullable: false),
                    SuspendedReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    HealthStatus = table.Column<int>(type: "int", nullable: false),
                    LastHealthCheckAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastHealthDetail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PendingApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderHealthEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderHealthEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Queue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EnqueuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VisibleAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DequeueCount = table.Column<int>(type: "int", nullable: false),
                    DeadLetteredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipientConsents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientConsents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipientContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Mobile = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    WhatsApp = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Roles = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AttributesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ErasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientContacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipientGroupMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientGroupMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipientGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Dynamic = table.Column<bool>(type: "bit", nullable: false),
                    FilterJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipientPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientPreferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Suppressions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    PromotionalOnly = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppressions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantQuotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: true),
                    MonthlyLimit = table.Column<int>(type: "int", nullable: false),
                    AlertThresholdPercent = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantQuotas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SuspendedReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AllowSharedProviders = table.Column<bool>(type: "bit", nullable: false),
                    RequireMarketingConsent = table.Column<bool>(type: "bit", nullable: false),
                    DefaultTimeZone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    BrandingJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    BodyRetentionDays = table.Column<int>(type: "int", nullable: false),
                    DataRetentionDays = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MessageClass = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<int>(type: "int", nullable: false),
                    Units = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    ProviderReportedCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    BilledAmount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ResponseCode = table.Column<int>(type: "int", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    NextRetryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveryAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EventTypes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SecretCipher = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    DisabledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerHeartbeats",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Worker = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Instance = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Processed = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<long>(type: "bigint", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_AdminUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CampaignRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Mobile = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    VariablesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    NotificationRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HolidayDates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HolidayDates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HolidayDates_HolidayCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "HolidayCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemplateVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemplateVersionNumber = table.Column<int>(type: "int", nullable: true),
                    MessageClass = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ContentCipher = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    ContentPurgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SenderIdentityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PinnedProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AwaitingOperator = table.Column<bool>(type: "bit", nullable: false),
                    DeadLettered = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FallbackOfDeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EscalationOfDeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FallbackJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EscalationJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EscalateAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FollowUpProcessed = table.Column<bool>(type: "bit", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Units = table.Column<int>(type: "int", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_NotificationRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "NotificationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CustomerId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Mobile = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    WhatsApp = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRecipients_NotificationRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "NotificationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RuleConditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    GroupOperator = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Field = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Comparison = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleConditions_NotificationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "NotificationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    BodyFormat = table.Column<int>(type: "int", nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    ComponentsJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    ProviderTemplateName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ProviderTemplateLanguage = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ProviderCategory = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ProviderApprovalStatus = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DltTemplateId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Checksum = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectionReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateVersions_NotificationTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "NotificationTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderRoutingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    CountryPrefix = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MessageClass = table.Column<int>(type: "int", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderRoutingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderRoutingRules_ProviderAccounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "ProviderAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SenderIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ReplyTo = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Signature = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    DltEntityId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    WhatsAppPhoneNumberId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    WhatsAppBusinessAccountId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Suspended = table.Column<bool>(type: "bit", nullable: false),
                    VerificationStatus = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SenderIdentities_ProviderAccounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "ProviderAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SuspendedReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Sandbox = table.Column<bool>(type: "bit", nullable: false),
                    AllowedChannels = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DefaultLocale = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RateLimitPerMinute = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Applications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SuspendedReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BrandingJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    QuietHoursJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Companies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemplateVariables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    SampleValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MaxLength = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateVariables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateVariables_TemplateVersions_TemplateVersionId",
                        column: x => x.TemplateVersionId,
                        principalTable: "TemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DefaultSendersJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyApplications_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyApplications_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Email",
                table: "AdminUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_ExternalSubject",
                table: "AdminUsers",
                column: "ExternalSubject");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationCredentials_ApplicationId",
                table: "ApplicationCredentials",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationCredentials_ClientId",
                table: "ApplicationCredentials",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_TenantId_Code",
                table: "Applications",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_EntityType_EntityId",
                table: "ApprovalRequests",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_Status_RequestedAt",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_OccurredAt",
                table: "AuditLogs",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessEvents_ApplicationId_IdempotencyKey",
                table: "BusinessEvents",
                columns: new[] { "ApplicationId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessEvents_TenantId_ReceivedAt",
                table: "BusinessEvents",
                columns: new[] { "TenantId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_CampaignId_Status",
                table: "CampaignRecipients",
                columns: new[] { "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId_Status",
                table: "Campaigns",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_Code",
                table: "Companies",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyApplications_ApplicationId",
                table: "CompanyApplications",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyApplications_CompanyId_ApplicationId",
                table: "CompanyApplications",
                columns: new[] { "CompanyId", "ApplicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationThreads_TenantId_Channel_ExternalAddress_SenderAddress",
                table: "ConversationThreads",
                columns: new[] { "TenantId", "Channel", "ExternalAddress", "SenderAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationThreads_TenantId_LastMessageAt",
                table: "ConversationThreads",
                columns: new[] { "TenantId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditAccounts_TenantId_CompanyId",
                table: "CreditAccounts",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true,
                filter: "[CompanyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreditTransactions_AccountId_CreatedAt",
                table: "CreditTransactions",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_DeliveryId_AttemptNumber",
                table: "DeliveryAttempts",
                columns: new[] { "DeliveryId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_ProviderAccountId_StartedAt",
                table: "DeliveryAttempts",
                columns: new[] { "ProviderAccountId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryEvents_DeliveryId",
                table: "DeliveryEvents",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryEvents_ProviderAccountId_ProviderMessageId",
                table: "DeliveryEvents",
                columns: new[] { "ProviderAccountId", "ProviderMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_ApplicationId_TokenHash",
                table: "DeviceRegistrations",
                columns: new[] { "ApplicationId", "TokenHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_CompanyId_UserId",
                table: "DeviceRegistrations",
                columns: new[] { "CompanyId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_HolidayDates_CalendarId_Date",
                table: "HolidayDates",
                columns: new[] { "CalendarId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InAppMessages_ApplicationId_CompanyId_UserId_CreatedAt",
                table: "InAppMessages",
                columns: new[] { "ApplicationId", "CompanyId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InAppMessages_CreatedAt",
                table: "InAppMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InAppMessages_DeliveryId",
                table: "InAppMessages",
                column: "DeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_ProviderAccountId_ProviderMessageId",
                table: "InboundMessages",
                columns: new[] { "ProviderAccountId", "ProviderMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_TenantId_ReceivedAt",
                table: "InboundMessages",
                columns: new[] { "TenantId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_ThreadId_ReceivedAt",
                table: "InboundMessages",
                columns: new[] { "ThreadId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxDeduplication_Consumer_MessageKey",
                table: "InboxDeduplication",
                columns: new[] { "Consumer", "MessageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxDeduplication_ProcessedAt",
                table: "InboxDeduplication",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_InboundMessageId",
                table: "MessageAttachments",
                column: "InboundMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ApplicationId_CreatedAt",
                table: "NotificationDeliveries",
                columns: new[] { "ApplicationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_CampaignId",
                table: "NotificationDeliveries",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Channel_Address",
                table: "NotificationDeliveries",
                columns: new[] { "Channel", "Address" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_EscalateAt",
                table: "NotificationDeliveries",
                column: "EscalateAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ProviderAccountId_ProviderMessageId",
                table: "NotificationDeliveries",
                columns: new[] { "ProviderAccountId", "ProviderMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_RequestId",
                table: "NotificationDeliveries",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Status_NextAttemptAt",
                table: "NotificationDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_TenantId_CompanyId_CreatedAt",
                table: "NotificationDeliveries",
                columns: new[] { "TenantId", "CompanyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ThreadId",
                table: "NotificationDeliveries",
                column: "ThreadId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_RequestId",
                table: "NotificationRecipients",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRequests_ApplicationId_CreatedAt",
                table: "NotificationRequests",
                columns: new[] { "ApplicationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRequests_ApplicationId_IdempotencyKey",
                table: "NotificationRequests",
                columns: new[] { "ApplicationId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRequests_BusinessEventId",
                table: "NotificationRequests",
                column: "BusinessEventId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRequests_CampaignId",
                table: "NotificationRequests",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRequests_CorrelationId",
                table: "NotificationRequests",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRequests_TenantId_CompanyId_CreatedAt",
                table: "NotificationRequests",
                columns: new[] { "TenantId", "CompanyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_TenantId_EventType_Enabled",
                table: "NotificationRules",
                columns: new[] { "TenantId", "EventType", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSchedules_Enabled_NextRunAt",
                table: "NotificationSchedules",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_TenantId_Code",
                table: "NotificationTemplates",
                columns: new[] { "TenantId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_TenantId_CompanyId_ApplicationId_Code_Channel_Locale",
                table: "NotificationTemplates",
                columns: new[] { "TenantId", "CompanyId", "ApplicationId", "Code", "Channel", "Locale" },
                unique: true,
                filter: "[CompanyId] IS NOT NULL AND [ApplicationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTypes_TenantId_ApplicationId_Code",
                table: "NotificationTypes",
                columns: new[] { "TenantId", "ApplicationId", "Code" },
                unique: true,
                filter: "[ApplicationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PublishedAt_AvailableAt",
                table: "OutboxMessages",
                columns: new[] { "PublishedAt", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_Channel_Enabled",
                table: "ProviderAccounts",
                columns: new[] { "Channel", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_TenantId",
                table: "ProviderAccounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderHealthEvents_ProviderAccountId_OccurredAt",
                table: "ProviderHealthEvents",
                columns: new[] { "ProviderAccountId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRoutingRules_ProviderAccountId",
                table: "ProviderRoutingRules",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRoutingRules_TenantId_Channel",
                table: "ProviderRoutingRules",
                columns: new[] { "TenantId", "Channel" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueMessages_MessageId",
                table: "QueueMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_QueueMessages_Queue_DeadLetteredAt_VisibleAt",
                table: "QueueMessages",
                columns: new[] { "Queue", "DeadLetteredAt", "VisibleAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipientConsents_ContactId_Channel_Purpose_CapturedAt",
                table: "RecipientConsents",
                columns: new[] { "ContactId", "Channel", "Purpose", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipientContacts_CompanyId_Email",
                table: "RecipientContacts",
                columns: new[] { "CompanyId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipientContacts_CompanyId_Kind_ExternalId",
                table: "RecipientContacts",
                columns: new[] { "CompanyId", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipientContacts_CompanyId_Mobile",
                table: "RecipientContacts",
                columns: new[] { "CompanyId", "Mobile" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipientContacts_CompanyId_WhatsApp",
                table: "RecipientContacts",
                columns: new[] { "CompanyId", "WhatsApp" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipientGroupMembers_GroupId_ContactId",
                table: "RecipientGroupMembers",
                columns: new[] { "GroupId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipientGroups_CompanyId_Name",
                table: "RecipientGroups",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipientPreferences_ContactId_Category_Channel",
                table: "RecipientPreferences",
                columns: new[] { "ContactId", "Category", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_UserId",
                table: "RoleAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RuleConditions_RuleId",
                table: "RuleConditions",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderIdentities_ProviderAccountId",
                table: "SenderIdentities",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppressions_TenantId_Channel_Address",
                table: "Suppressions",
                columns: new[] { "TenantId", "Channel", "Address" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlerts_AcknowledgedAt_CreatedAt",
                table: "SystemAlerts",
                columns: new[] { "AcknowledgedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemAlerts_Key",
                table: "SystemAlerts",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVariables_TemplateVersionId_Name",
                table: "TemplateVariables",
                columns: new[] { "TemplateVersionId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVersions_TemplateId_VersionNumber",
                table: "TemplateVersions",
                columns: new[] { "TemplateId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantQuotas_TenantId",
                table: "TenantQuotas",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Code",
                table: "Tenants",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_ApplicationId_Period",
                table: "UsageRecords",
                columns: new[] { "ApplicationId", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_CompanyId_Period",
                table: "UsageRecords",
                columns: new[] { "CompanyId", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_DeliveryId",
                table: "UsageRecords",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_TenantId_Period_Channel",
                table: "UsageRecords",
                columns: new[] { "TenantId", "Period", "Channel" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryAttempts_EventId",
                table: "WebhookDeliveryAttempts",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryAttempts_SubscriptionId_AttemptedAt",
                table: "WebhookDeliveryAttempts",
                columns: new[] { "SubscriptionId", "AttemptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSubscriptions_ApplicationId_Enabled",
                table: "WebhookSubscriptions",
                columns: new[] { "ApplicationId", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationCredentials");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "BillingRates");

            migrationBuilder.DropTable(
                name: "BusinessEvents");

            migrationBuilder.DropTable(
                name: "CampaignRecipients");

            migrationBuilder.DropTable(
                name: "CompanyApplications");

            migrationBuilder.DropTable(
                name: "ConversationThreads");

            migrationBuilder.DropTable(
                name: "CostRates");

            migrationBuilder.DropTable(
                name: "CreditAccounts");

            migrationBuilder.DropTable(
                name: "CreditTransactions");

            migrationBuilder.DropTable(
                name: "DeliveryAttempts");

            migrationBuilder.DropTable(
                name: "DeliveryEvents");

            migrationBuilder.DropTable(
                name: "DeviceRegistrations");

            migrationBuilder.DropTable(
                name: "HolidayDates");

            migrationBuilder.DropTable(
                name: "InAppMessages");

            migrationBuilder.DropTable(
                name: "InboundMessages");

            migrationBuilder.DropTable(
                name: "InboxDeduplication");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "NotificationRecipients");

            migrationBuilder.DropTable(
                name: "NotificationSchedules");

            migrationBuilder.DropTable(
                name: "NotificationTypes");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "ProviderHealthEvents");

            migrationBuilder.DropTable(
                name: "ProviderRoutingRules");

            migrationBuilder.DropTable(
                name: "QueueMessages");

            migrationBuilder.DropTable(
                name: "RecipientConsents");

            migrationBuilder.DropTable(
                name: "RecipientContacts");

            migrationBuilder.DropTable(
                name: "RecipientGroupMembers");

            migrationBuilder.DropTable(
                name: "RecipientGroups");

            migrationBuilder.DropTable(
                name: "RecipientPreferences");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "RuleConditions");

            migrationBuilder.DropTable(
                name: "SenderIdentities");

            migrationBuilder.DropTable(
                name: "Suppressions");

            migrationBuilder.DropTable(
                name: "SystemAlerts");

            migrationBuilder.DropTable(
                name: "TemplateVariables");

            migrationBuilder.DropTable(
                name: "TenantQuotas");

            migrationBuilder.DropTable(
                name: "UsageRecords");

            migrationBuilder.DropTable(
                name: "WebhookDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "WebhookSubscriptions");

            migrationBuilder.DropTable(
                name: "WorkerHeartbeats");

            migrationBuilder.DropTable(
                name: "Campaigns");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropTable(
                name: "HolidayCalendars");

            migrationBuilder.DropTable(
                name: "NotificationRequests");

            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "NotificationRules");

            migrationBuilder.DropTable(
                name: "ProviderAccounts");

            migrationBuilder.DropTable(
                name: "TemplateVersions");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "NotificationTemplates");
        }
    }
}
