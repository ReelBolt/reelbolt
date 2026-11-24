namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// Configuration for the GitHub App integration (section <c>GitHub</c>, see <c>docs/github-app.md</c>).
///
/// <para>
/// <b>The integration is inert until every credential below is present.</b> With no app id, no private
/// key or no webhook secret, the webhook endpoint answers <c>503</c> rather than accepting anything, and
/// link creation is refused — there is no "allow when unconfigured" path anywhere, because the whole
/// trust story is "the signature verified against this secret" and an absent secret has nothing to
/// verify against.
/// </para>
/// </summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>The GitHub App's id (<c>GITHUB_APP_ID</c>). Its numeric id, not its slug.</summary>
    public string? AppId { get; set; }

    /// <summary>The app's slug, used only to build the install URL in the docs and the UI hint.</summary>
    public string? AppSlug { get; set; }

    /// <summary>
    /// The PEM private key GitHub generates for the app (<c>GITHUB_APP_PRIVATE_KEY</c>). A literal
    /// <c>\n</c> sequence is accepted as a newline, because that is what happens to a multi-line PEM
    /// pasted into a single-line environment variable.
    /// </summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// Path to the PEM private key on disk (<c>GITHUB_APP_PRIVATE_KEY_PATH</c>). The file is read once,
    /// lazily, and never logged — a mounted secret is the usual deployment shape.
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// The app's webhook secret (<c>GITHUB_WEBHOOK_SECRET</c>). The owner picks it when creating the
    /// app; it is the only credential the webhook endpoint trusts.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// The app's <b>client id</b> (<c>GITHUB_APP_CLIENT_ID</c>) — the <c>Iv1.…</c> value on the app's
    /// settings page, which is <b>not</b> <see cref="AppId"/>. Required for the user-to-server OAuth
    /// flow that connects a person's GitHub account; without it the account-connection endpoints answer
    /// <c>503</c> and the repository picker is unavailable.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The app's <b>client secret</b> (<c>GITHUB_APP_CLIENT_SECRET</c>), generated under "Client
    /// secrets" on the app's settings page. A secret, never logged. It is what makes ReelBolt a
    /// confidential OAuth client: the authorization code is exchanged server-to-server and the client
    /// secret never reaches the browser.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The absolute URL GitHub redirects to after a person authorizes the app
    /// (<c>GITHUB_APP_OAUTH_CALLBACK_URL</c>), e.g.
    /// <c>https://reelbolt.example.com/api/v1/github/oauth/callback</c>. It must match one of the
    /// app's registered "Callback URL" values <b>byte for byte</b>, which is why it is configuration
    /// rather than derived from the request: a callback built from the <c>Host</c> header would be
    /// attacker-influenced, and a redirect target is exactly the wrong thing to take from a header.
    /// </summary>
    public string? OAuthCallbackUrl { get; set; }

    /// <summary>
    /// Where the browser is sent to authorize (<c>GitHub:OAuthAuthorizeUrl</c>). Overridable for a
    /// GitHub Enterprise install and for tests; there is no reason to change it on github.com.
    /// </summary>
    public string OAuthAuthorizeUrl { get; set; } = "https://github.com/login/oauth/authorize";

    /// <summary>
    /// Where an authorization code is exchanged for a user access token
    /// (<c>GitHub:OAuthTokenUrl</c>). A different host from <see cref="ApiBaseUrl"/>: the OAuth
    /// endpoints live on github.com, not on api.github.com.
    /// </summary>
    public string OAuthTokenUrl { get; set; } = "https://github.com/login/oauth/access_token";

    /// <summary>
    /// How long a started authorization stays completable. Ten minutes is long enough to sign in to
    /// GitHub and short enough that a harvested <c>state</c> is worthless.
    /// </summary>
    public int OAuthStateTtlSeconds { get; set; } = 600;

    /// <summary>Most repositories one picker response will carry, across every installation.</summary>
    public int MaxRepositoriesListed { get; set; } = 500;

    /// <summary>
    /// True when a person's GitHub account can be connected: a client id, a client secret and the
    /// exact callback URL GitHub will redirect to. All three, or the flow is refused <c>503</c> —
    /// never half-enabled, for the same reason <see cref="HasWebhookSecret"/> is all-or-nothing.
    /// </summary>
    public bool HasUserOAuthCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(OAuthCallbackUrl);

    /// <summary>GitHub's REST base. Overridable so a test or a GitHub Enterprise install can point elsewhere.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>Most files one pull will materialize. A larger tree is refused whole, never half-applied.</summary>
    public int MaxFilesPerPull { get; set; } = 2000;

    /// <summary>Largest single file a pull will store.</summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Largest total payload one pull will store.</summary>
    public long MaxTotalBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How often the pull worker looks for an unprocessed delivery.</summary>
    public int WorkerIntervalSeconds { get; set; } = 15;

    /// <summary>Attempts before a delivery is parked as failed rather than retried forever.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    private readonly object _keyLock = new();
    private string? _cachedKey;
    private bool _keyRead;

    /// <summary>True when the app can authenticate to GitHub as itself (app id + private key).</summary>
    public bool HasAppCredentials => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(ResolvePrivateKeyPem());

    /// <summary>True when a webhook can be verified. This, not <see cref="HasAppCredentials"/>, gates the endpoint.</summary>
    public bool HasWebhookSecret => HasAppCredentials && !string.IsNullOrWhiteSpace(WebhookSecret);

    /// <summary>
    /// The private key PEM, read from <see cref="PrivateKeyPath"/> once. Returns null when nothing is
    /// configured or the file is unreadable — callers treat that as "not configured", which is the
    /// fail-closed direction, and never as an authentication failure to paper over.
    /// </summary>
    public string? ResolvePrivateKeyPem()
    {
        lock (_keyLock)
        {
            if (_keyRead) return _cachedKey;
            _keyRead = true;

            if (!string.IsNullOrWhiteSpace(PrivateKeyPem))
            {
                _cachedKey = PrivateKeyPem.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
                return _cachedKey;
            }

            if (!string.IsNullOrWhiteSpace(PrivateKeyPath))
            {
                try
                {
                    _cachedKey = File.ReadAllText(PrivateKeyPath).Trim();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _cachedKey = null;
                }
            }

            return _cachedKey;
        }
    }
}
