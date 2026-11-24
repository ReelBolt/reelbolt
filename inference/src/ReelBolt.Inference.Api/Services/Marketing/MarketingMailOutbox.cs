using System.Threading.Channels;
using ReelBolt.Inference.Api.Services.Account;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>Where the consent service drops a confirmation mail; sending happens off the request path.</summary>
public interface IMarketingMailOutbox
{
    /// <summary>Queues the mail; false when it was shed because its lane is full.</summary>
    bool Enqueue(AccountMail mail, MarketingMailOrigin origin);
}

/// <summary>Who caused a queued mail, which decides its lane in <see cref="MarketingMailOutbox"/>.</summary>
public enum MarketingMailOrigin
{
    /// <summary>An unauthenticated request (the public subscribe endpoint): shed first.</summary>
    Anonymous,

    /// <summary>A signed-in account holder: its own lane, always sent before anonymous mail.</summary>
    Account,
}

/// <summary>
/// An in-process queue in front of the Go mail relay. The anonymous subscribe endpoint must answer
/// the same way, in about the same time, whether or not the address is known, so the relay round
/// trip (up to its 15 s timeout) is never on the request path. Best-effort like every other account
/// mail: a send that fails is logged, and a mail still queued at shutdown is lost (the visitor can
/// ask again after the resend window).
/// <para>
/// Two bounded lanes: anonymous mail has the smaller one and is dropped as soon as it is full, so a
/// flood of public subscribes is shed before it can delay or crowd out mail an account holder asked
/// for; the reader always drains the account lane first.
/// </para>
/// </summary>
public sealed class MarketingMailOutbox : BackgroundService, IMarketingMailOutbox
{
    public const int AnonymousCapacity = 200;
    public const int AccountCapacity = 1000;

    // FullMode Wait: TryWrite answers false on a full lane instead of silently dropping (DropWrite
    // reports success), so a shed mail is logged.
    private readonly Channel<AccountMail> _anonymous = Channel.CreateBounded<AccountMail>(
        new BoundedChannelOptions(AnonymousCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private readonly Channel<AccountMail> _account = Channel.CreateBounded<AccountMail>(
        new BoundedChannelOptions(AccountCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MarketingMailOutbox> _logger;

    public MarketingMailOutbox(IServiceScopeFactory scopes, ILogger<MarketingMailOutbox> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public bool Enqueue(AccountMail mail, MarketingMailOrigin origin)
    {
        Channel<AccountMail> lane = origin == MarketingMailOrigin.Account ? _account : _anonymous;
        if (lane.Writer.TryWrite(mail))
        {
            return true;
        }

        _logger.LogWarning("The {Origin} marketing mail lane is full; the {Template} mail was dropped", origin, mail.Template);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_account.Reader.TryRead(out AccountMail? mail) || _anonymous.Reader.TryRead(out mail))
                {
                    await SendAsync(mail, stoppingToken);
                    continue;
                }

                await Task.WhenAny(
                    _account.Reader.WaitToReadAsync(stoppingToken).AsTask(),
                    _anonymous.Reader.WaitToReadAsync(stoppingToken).AsTask());
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task SendAsync(AccountMail mail, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            IAccountMailClient client = scope.ServiceProvider.GetRequiredService<IAccountMailClient>();
            await client.SendAsync(mail, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never the exception object: its message can echo the recipient or the link (C4).
            _logger.LogWarning("Sending the {Template} mail failed: {Error}", mail.Template, MailLogRef.DescribeError(ex));
        }
    }
}
