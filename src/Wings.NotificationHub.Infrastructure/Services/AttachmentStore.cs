using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Infrastructure.Services;

public interface IAttachmentStore
{
    Task<MessageAttachment> SaveAsync(Guid tenantId, Guid? companyId, string direction, string fileName, string contentType, byte[] content, string uploadedBy, Guid? inboundMessageId, CancellationToken ct);
    Task<byte[]> ReadAsync(MessageAttachment attachment, CancellationToken ct);
    Task<IReadOnlyList<OutboundAttachment>> LoadAsync(Guid tenantId, IReadOnlyList<AttachmentRefDto>? refs, CancellationToken ct);
    Task DeleteAsync(MessageAttachment attachment, CancellationToken ct);
}

/// <summary>
/// Encrypted file-system attachment store. Files live under an opaque random key (never a caller-supplied path),
/// are AES-GCM encrypted at rest, size-limited and restricted to an allow-list of content types.
/// Swap for blob storage by implementing <see cref="IAttachmentStore"/>.
/// </summary>
public sealed class FileAttachmentStore(NotificationHubDbContext db, ISecretProtector protector, IOptions<HubOptions> options) : IAttachmentStore
{
    public static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/png", "image/jpeg", "image/gif", "image/webp", "text/plain", "text/csv",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel", "application/msword", "audio/ogg", "audio/mpeg", "video/mp4", "application/zip",
    };

    private string Root => Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "attachments");

    public async Task<MessageAttachment> SaveAsync(Guid tenantId, Guid? companyId, string direction, string fileName, string contentType, byte[] content, string uploadedBy, Guid? inboundMessageId, CancellationToken ct)
    {
        if (content.Length == 0) throw HubException.Validation("The attachment is empty.");
        if (content.Length > options.Value.MaxAttachmentBytes) throw HubException.Validation($"Attachments are limited to {options.Value.MaxAttachmentBytes / (1024 * 1024)} MB.");
        var type = contentType.Split(';')[0].Trim();
        if (!AllowedContentTypes.Contains(type)) throw HubException.Validation($"Content type '{type}' is not permitted.");
        var key = $"{tenantId:N}/{Guid.NewGuid():N}";
        var path = Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, protector.Protect(Convert.ToBase64String(content), "attachment:" + key), ct);
        var safeName = Path.GetFileName(fileName.Replace('\\', '/'));
        var att = new MessageAttachment
        {
            TenantId = tenantId, CompanyId = companyId, Direction = direction, InboundMessageId = inboundMessageId,
            FileName = string.IsNullOrWhiteSpace(safeName) ? "attachment" : safeName[..Math.Min(200, safeName.Length)],
            ContentType = type, Size = content.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)), StorageKey = key, UploadedBy = uploadedBy,
        };
        db.MessageAttachments.Add(att);
        return att;
    }

    public async Task<byte[]> ReadAsync(MessageAttachment attachment, CancellationToken ct)
    {
        var path = Path.Combine(Root, attachment.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Root, StringComparison.Ordinal)) throw new AccessDeniedException("Invalid attachment key.");
        return Convert.FromBase64String(protector.Unprotect(await File.ReadAllTextAsync(full, ct), "attachment:" + attachment.StorageKey));
    }

    public async Task<IReadOnlyList<OutboundAttachment>> LoadAsync(Guid tenantId, IReadOnlyList<AttachmentRefDto>? refs, CancellationToken ct)
    {
        if (refs is null || refs.Count == 0) return [];
        var ids = refs.Select(r => r.AttachmentId).ToList();
        var rows = await db.MessageAttachments.AsNoTracking().Where(a => ids.Contains(a.Id) && a.TenantId == tenantId).ToListAsync(ct);
        var list = new List<OutboundAttachment>();
        foreach (var r in refs)
        {
            var row = rows.FirstOrDefault(a => a.Id == r.AttachmentId) ?? throw new ProviderConfigurationException($"Attachment {r.AttachmentId} is missing.");
            list.Add(new OutboundAttachment { FileName = row.FileName, ContentType = row.ContentType, Content = await ReadAsync(row, ct), Inline = r.Inline, ContentId = r.ContentId });
        }
        return list;
    }

    public Task DeleteAsync(MessageAttachment attachment, CancellationToken ct)
    {
        var path = Path.Combine(Root, attachment.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
        db.MessageAttachments.Remove(attachment);
        return Task.CompletedTask;
    }
}
