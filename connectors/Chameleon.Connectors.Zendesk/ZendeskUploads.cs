using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Chameleon.Sdk;

namespace Chameleon.Connectors.Zendesk;

/// POST /uploads with the binary body, then the token in a comment.
public sealed partial class ZendeskConnector
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;
    // ponytail: bytes kept for content_url only up to 5 MB; above that, metadata only (content_url 404).
    private const long MaxStoredBytes = 5L * 1024 * 1024;

    private static async Task<IResult> Upload(HttpContext http, ConnectorContext ctx)
    {
        var fileName = http.Request.Query["filename"].ToString();
        if (string.IsNullOrWhiteSpace(fileName)) return Invalid("filename", "Filename: cannot be blank", "BlankValue");
        if (http.Request.ContentLength > MaxUploadBytes)
            return Results.Json(new { error = "RequestEntityTooLarge", description = "File size exceeds the 50 MB limit" }, Json, statusCode: 413);

        using var content = new MemoryStream();
        await http.Request.Body.CopyToAsync(content, http.RequestAborted);
        if (content.Length == 0) return Invalid("uploads", "Uploads: file cannot be empty", "BlankValue");
        if (content.Length > MaxUploadBytes)
            return Results.Json(new { error = "RequestEntityTooLarge", description = "File size exceeds the 50 MB limit" }, Json, statusCode: 413);

        var contentType = string.IsNullOrEmpty(http.Request.ContentType) ? "application/octet-stream" : http.Request.ContentType;
        var attachment = new Attachment(AttachmentBase + ctx.Store.All<ZendeskUpload>().Count + 1, fileName, contentType, content.Length);
        var upload = new ZendeskUpload { Token = Guid.NewGuid().ToString("N"), Attachment = attachment, ExpiresAt = ctx.Now.AddDays(3) };
        ctx.Save(upload.Token, upload);
        if (content.Length <= MaxStoredBytes)
            ctx.Save(attachment.Id.ToString(), new ZendeskAttachmentContent
            {
                Id = attachment.Id, ContentBase64 = Convert.ToBase64String(content.ToArray()),
            });

        var view = AttachmentView(http, attachment);
        return Results.Json(new { upload = new { token = upload.Token, expires_at = Ts(upload.ExpiresAt), attachments = new[] { view }, attachment = view } },
            Json, statusCode: 201);
    }

    /// The comment's upload tokens become its attachments; each token works once, within 3 days.
    /// Null when one is unknown, used or expired.
    private static List<Attachment>? TakeUploads(ConnectorContext ctx, JsonElement? comment)
    {
        if (comment is not { } c || !c.TryGetProperty("uploads", out var tokens) || tokens.ValueKind != JsonValueKind.Array) return [];
        var uploads = tokens.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? ctx.Store.Get<ZendeskUpload>(t.GetString()!) : null).ToList();
        if (uploads.Any(u => u is null || u.Used || u.ExpiresAt <= ctx.Now)) return null;
        foreach (var u in uploads)
        {
            u!.Used = true;
            ctx.Save(u.Token, u);
        }
        return uploads.Select(u => u!.Attachment).ToList();
    }

    /// Same origin as the panel: always a download, never rendered (the content type is the app's word).
    private static IResult AttachmentContent(string id, string fileName, HttpContext http, ConnectorContext ctx)
    {
        if (ctx.Store.Get<ZendeskAttachmentContent>(id) is not { } a) return NotFound();
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "sandbox";
        return Results.File(Convert.FromBase64String(a.ContentBase64), "application/octet-stream", fileDownloadName: fileName);
    }
}
