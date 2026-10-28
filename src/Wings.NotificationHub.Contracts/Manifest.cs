namespace Wings.NotificationHub.Contracts;

/// <summary>
/// Notification configuration manifest that a Wings Code generated application registers during deployment
/// (POST /api/v1/registrations/manifest). Registration is an idempotent upsert keyed by application code.
/// </summary>
public sealed class NotificationManifest
{
    public string SchemaVersion { get; set; } = "1.0";
    public ManifestApplication Application { get; set; } = new();
    /// <summary>Companies (by code) that may use the application. Must already exist in the tenant.</summary>
    public List<string> Companies { get; set; } = [];
    public List<Channel> Channels { get; set; } = [];
    public List<ManifestNotificationType> NotificationTypes { get; set; } = [];
    public List<ManifestTemplate> Templates { get; set; } = [];
    public List<ManifestWebhook> Webhooks { get; set; } = [];
    /// <summary>Create a client credential when the application has none (secret is returned once).</summary>
    public ManifestCredential? Credential { get; set; }
}

public sealed class ManifestApplication
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string DefaultLocale { get; set; } = "en";
    /// <summary>Sandbox applications are routed to mock providers only (safe pre-deployment testing).</summary>
    public bool Sandbox { get; set; }
}

public sealed class ManifestNotificationType
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "General";
    public MessageClass MessageClass { get; set; } = MessageClass.Transactional;
    public List<Channel> DefaultChannels { get; set; } = [];
    public string? DefaultTemplateCode { get; set; }
    public NotificationPriority DefaultPriority { get; set; } = NotificationPriority.Normal;
    /// <summary>Business event types this application raises for this notification (documentation + rule wiring).</summary>
    public List<string> Events { get; set; } = [];
    public bool AiAllowed { get; set; }
    public bool RequiresHumanApprovalForAi { get; set; } = true;
}

public sealed class ManifestTemplate
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Channel Channel { get; set; }
    public string Locale { get; set; } = "en";
    public string? Category { get; set; }
    public string? Subject { get; set; }
    public string Body { get; set; } = "";
    public BodyFormat BodyFormat { get; set; } = BodyFormat.Text;
    public List<TemplateVariableDto> Variables { get; set; } = [];
}

public sealed class ManifestWebhook
{
    public string Url { get; set; } = "";
    public List<string> EventTypes { get; set; } = [];
}

public sealed class ManifestCredential
{
    public string Name { get; set; } = "default";
    public List<string>? Scopes { get; set; }
}

public sealed class ManifestRegistrationResult
{
    public Guid ApplicationId { get; set; }
    public bool Created { get; set; }
    public int NotificationTypesUpserted { get; set; }
    /// <summary>Templates are registered as Draft versions and go through review/approval before activation.</summary>
    public int TemplatesRegistered { get; set; }
    public int WebhooksRegistered { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Only returned when a new credential was created. Store it in the deployment's secret store.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Webhook signing secrets for newly created subscriptions, keyed by URL. Returned once.</summary>
    public Dictionary<string, string> WebhookSecrets { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}
