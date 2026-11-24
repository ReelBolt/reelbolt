namespace ReelBolt.Inference.Api.Services.ApiKeys;

/// <summary>Configuration of the public API (<c>PublicApi:*</c>, env <c>PublicApi__*</c>).</summary>
public sealed class PublicApiOptions
{
    public const string SectionName = "PublicApi";

    /// <summary>Master switch (env <c>PUBLIC_API_ENABLED</c>). Off: keys fail, the key routes answer 404.</summary>
    public bool Enabled { get; set; }

    /// <summary>Active (not revoked, not expired) keys one organization may hold.</summary>
    public int MaxKeysPerOrg { get; set; } = 50;

    /// <summary>The longest lifetime the server accepts for a new key.</summary>
    public int MaxLifetimeDays { get; set; } = 365;

    /// <summary>Daily spend cap, in credits, given to a new key that does not choose one. Raising or removing it needs confirmation.</summary>
    public long DefaultDailyCapCredits { get; set; } = 200;

    /// <summary>The per-key fixed one-minute request and write caps.</summary>
    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>How long the key audit trail is kept (<c>api_key_audit_events</c>).</summary>
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>How long a stored <c>Idempotency-Key</c> response is replayed.</summary>
    public int IdempotencyHours { get; set; } = 24;

    /// <summary><c>PublicApi:RunQueue:*</c>.</summary>
    public sealed class RunQueueOptions
    {
        /// <summary>Queued (not yet started) run requests one key may hold; past it the start answers 429 <c>run_queue_full</c>.</summary>
        public int MaxQueuedPerKey { get; set; } = 20;

        /// <summary>How often the dispatcher looks for due requests.</summary>
        public int PollSeconds { get; set; } = 15;

        /// <summary>Requests claimed per tick.</summary>
        public int BatchSize { get; set; } = 5;

        /// <summary>A claimed (<c>starting</c>) request older than this is reconciled by its execution's correlation id.</summary>
        public int StaleStartingMinutes { get; set; } = 5;
    }

    /// <summary>The public API's per-key fixed-window caps (requests/min and writes/min).</summary>
    public sealed class RateLimitOptions
    {
        /// <summary>Every request counts, reads and writes alike.</summary>
        public int RequestsPerMinute { get; set; } = 300;

        /// <summary>POST/PUT/PATCH/DELETE also count here.</summary>
        public int WritesPerMinute { get; set; } = 60;
    }

    /// <summary>Queued run starts: the per-key bound and the dispatcher's cadence.</summary>
    public RunQueueOptions RunQueue { get; set; } = new();

    /// <summary>Outbound webhooks (<c>PublicApi:Webhooks:*</c>).</summary>
    public WebhookOptions Webhooks { get; set; } = new();

    /// <summary>The 7-day and 1-day key expiry warnings (<c>PublicApi:ExpiryWarning:*</c>).</summary>
    public ExpiryWarningOptions ExpiryWarning { get; set; } = new();
}

/// <summary><c>PublicApi:Webhooks:*</c> (public-api T-009).</summary>
public sealed class WebhookOptions
{
    /// <summary>Endpoints one organization may hold.</summary>
    public int MaxEndpointsPerOrg { get; set; } = 10;

    /// <summary>An endpoint with no success for this long is disabled and the organization's admins are told.</summary>
    public int DisableAfterHours { get; set; } = 72;

    /// <summary>Hours the previous secret keeps signing after a rotation.</summary>
    public int PreviousSecretHours { get; set; } = 24;

    /// <summary>Deliveries are deleted after this many days.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>How often the worker looks for due deliveries.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Due rows claimed per tick.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Deliveries in flight at once across all endpoints (one per endpoint).</summary>
    public int Concurrency { get; set; } = 8;

    /// <summary>Per-POST timeout.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class ExpiryWarningOptions
{
    /// <summary>Off: <see cref="ApiKeyExpirySweeper"/> sends nothing.</summary>
    public bool Enabled { get; set; } = true;
}
