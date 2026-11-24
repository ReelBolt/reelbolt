using System.Text;
using System.Text.Json;
using ReelBolt.Inference.Api.Services.Uploads;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// The public <c>uploadId</c>: an opaque, URL-safe token carrying the direct-upload ticket's fields (the
/// same ones the dashboard's browser echoes back). The MAC inside it is bound to the caller and the project,
/// so a forged or foreign id fails <see cref="IDirectUploadService.Verify"/>; nothing here is trusted on its own.
/// </summary>
public static class PublicUploadId
{
    public const int MaxLength = 2048;

    private sealed record Payload(
        string T, int M, Guid F, string K, string N, string? R, string C, long S, long E, string? U, long P, int Pc);

    public static string Encode(DirectUploadTicket ticket)
    {
        Payload payload = new(ticket.UploadToken, (int)ticket.Mode, ticket.FileId, ticket.Key, ticket.FileName,
            ticket.RelativePath, ticket.ContentType, ticket.SizeBytes, ticket.ExpiresAtUnix, ticket.UploadId,
            ticket.PartSizeBytes, ticket.PartCount);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The echo for <paramref name="projectId"/>, or null when the id is not well formed.</summary>
    public static DirectUploadTicketEcho? TryDecode(string? uploadId, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || uploadId.Length > MaxLength) return null;
        try
        {
            string padded = uploadId.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            Payload? p = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            if (p is null || !Enum.IsDefined((DirectUploadMode)p.M)) return null;
            return new DirectUploadTicketEcho(p.T, (DirectUploadMode)p.M, projectId, p.F, p.K, p.N, p.R, p.C, p.S,
                p.E, p.U, p.P, p.Pc, null, null, null, null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
