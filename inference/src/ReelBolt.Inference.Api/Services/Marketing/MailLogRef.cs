using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>Mail logging settings. Section <c>Mail</c> (compose: <c>Mail__LogRefKey</c> from <c>MAIL_LOG_REF_KEY</c>).</summary>
public sealed class MailOptions
{
    public const string SectionName = "Mail";

    /// <summary>
    /// The secret of the recipient log reference (contract C4), the same value as the Go API's
    /// <c>MAIL_LOG_REF_KEY</c> so both services log the same reference for one mailbox. Unset: a
    /// random per-process key (references then match nothing across services or restarts) and one
    /// startup warning. Never the JWT key.
    /// </summary>
    public string? LogRefKey { get; set; }
}

/// <summary>
/// What a log line may say about a mail recipient (contract C4): <c>rcpt=</c> + the first 12 hex
/// characters of HMAC-SHA256(key = UTF-8 secret, message = <see cref="MarketingEmail.CanonicalMailbox"/>).
/// Keyed, so a dictionary of candidate addresses cannot reverse it without the secret; never unkeyed.
/// Also the one place error text on the mail path is made safe to log.
/// </summary>
public sealed partial class MailLogRef
{
    private readonly byte[] _key;

    public MailLogRef(IOptions<MailOptions> options)
    {
        string? secret = options.Value.LogRefKey;
        KeyConfigured = !string.IsNullOrEmpty(secret);
        _key = KeyConfigured ? Encoding.UTF8.GetBytes(secret!) : RandomNumberGenerator.GetBytes(32);
    }

    private MailLogRef(byte[] key)
    {
        _key = key;
        KeyConfigured = true;
    }

    /// <summary>A reference under the given secret (tests, and the shared C4 vectors).</summary>
    public static MailLogRef ForKey(string secret) => new(Encoding.UTF8.GetBytes(secret));

    /// <summary>A reference under a random key: safe to log, correlates nothing. The fallback when none is injected.</summary>
    public static MailLogRef Ephemeral() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>False when <c>Mail:LogRefKey</c> was unset and a random per-process key is in use.</summary>
    public bool KeyConfigured { get; }

    /// <summary><c>rcpt=</c> + 12 hex characters of the keyed hash of the canonical mailbox.</summary>
    public string For(string? address)
    {
        byte[] mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(MarketingEmail.CanonicalMailbox(address)));
        return "rcpt=" + Convert.ToHexStringLower(mac)[..12];
    }

    /// <summary>
    /// Text that may be logged: anything shaped like an address (<c>\S+@\S+</c>) and any
    /// <c>token=</c> value replaced, so an SMTP reply (<c>550 5.1.1 &lt;x@y&gt;</c>), a relay error
    /// body or a tokenised link in an exception message never reaches the logs.
    /// </summary>
    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : TokenPattern().Replace(AddressPattern().Replace(text, "[address]"), "${name}=[redacted]");

    /// <summary>
    /// An exception as one safe log field: its class (and inner classes), the socket or HTTP error
    /// code when there is one, and the redacted message. Log this instead of the exception object,
    /// whose message and stack data a sink would print verbatim.
    /// </summary>
    public static string DescribeError(Exception ex)
    {
        StringBuilder text = new(ex.GetType().Name);
        switch (ex)
        {
            case HttpRequestException { StatusCode: { } status }:
                text.Append(" status=").Append((int)status);
                break;
            case HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } http:
                text.Append(" error=").Append(http.HttpRequestError);
                break;
            case SocketException socket:
                text.Append(" socket=").Append(socket.SocketErrorCode);
                break;
        }

        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text.Append(" <- ").Append(inner.GetType().Name);
        }

        string message = Redact(ex.Message);
        return message.Length == 0 ? text.ToString() : text.Append(": ").Append(message).ToString();
    }

    [GeneratedRegex(@"\S+@\S+")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"(?<name>token|code|key)=[^&\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
