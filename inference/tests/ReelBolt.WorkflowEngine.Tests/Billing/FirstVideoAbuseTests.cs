using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using System.Reflection;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>First-video grant abuse checks: disposable/MX, Tor exit, ipinfo privacy. Mocks only; every unknown fails open.</summary>
public sealed class FirstVideoAbuseTests : IDisposable
{
    private const string Token = "test-token-not-real";
    private const string Ip = "203.0.113.7";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FirstVideoAbuseTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            await Task.Yield();
            return respond(request);
        }
    }

    private sealed class FakeDns(bool? answer) : IMailDomainResolver
    {
        public Task<bool?> CanReceiveMailAsync(string domain, CancellationToken ct) => Task.FromResult(answer);
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static (FirstVideoEligibility Eligibility, StubHandler Handler, TorExitList Tor) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, bool? mx = true, bool withToken = true)
    {
        StubHandler handler = new(respond);
        TorExitList tor = new();
        IConfiguration config = withToken ? Config((FirstVideoAbuseKeys.IpInfoToken, Token)) : Config();
        IpInfoPrivacyClient client = new(new HttpClient(handler), config);
        return (new FirstVideoEligibility(new FakeDns(mx), tor, client, config), handler, tor);
    }

    [Fact]
    public async Task Disposable_email_domain_is_refused_including_subdomains()
    {
        var (e, handler, _) = Build(_ => Json("{}"));
        (await e.DeniedReasonAsync("a@mailinator.com", Ip, default)).Should().Be("disposable_email");
        (await e.DeniedReasonAsync("a@x.mailinator.com", Ip, default)).Should().Be("disposable_email");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_domain_with_no_mail_server_is_refused_but_a_dns_failure_is_not()
    {
        (await Build(_ => Json("{}"), mx: false).Eligibility.DeniedReasonAsync("a@nomail.example", null, default)).Should().Be("disposable_email");
        (await Build(_ => Json("{}"), mx: null).Eligibility.DeniedReasonAsync("a@nomail.example", null, default)).Should().BeNull();
        (await Build(_ => Json("{}"), mx: true).Eligibility.DeniedReasonAsync("a@fine.example", null, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_tor_exit_ip_is_refused_without_calling_ipinfo()
    {
        var (e, handler, tor) = Build(_ => Json("{}"));
        tor.Replace(["# comment", Ip, "198.51.100.1"]);
        (await e.DeniedReasonAsync("a@fine.example", Ip, default)).Should().Be("tor_exit");
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"vpn":true,"proxy":false,"tor":false,"relay":false,"hosting":false}""", "vpn_proxy_hosting")]
    [InlineData("""{"vpn":false,"proxy":true,"tor":false,"relay":false,"hosting":false}""", "vpn_proxy_hosting")]
    [InlineData("""{"vpn":false,"proxy":false,"tor":true,"relay":false,"hosting":false}""", "vpn_proxy_hosting")]
    [InlineData("""{"vpn":false,"proxy":false,"tor":false,"relay":false,"hosting":true}""", "vpn_proxy_hosting")]
    [InlineData("""{"vpn":false,"proxy":false,"tor":false,"relay":true,"hosting":false}""", null)]
    [InlineData("""{"vpn":false,"proxy":false,"tor":false,"relay":false,"hosting":false}""", null)]
    public async Task Ipinfo_flags_refuse_except_relay_alone(string body, string? expected)
    {
        var (e, handler, _) = Build(_ => Json(body));
        (await e.DeniedReasonAsync("a@fine.example", Ip, default)).Should().Be(expected);
        handler.Requests.Single().Headers.Authorization!.Parameter.Should().Be(Token);
        handler.Requests.Single().RequestUri!.ToString().Should().NotContain(Token);
    }

    [Fact]
    public async Task The_ipinfo_request_is_marked_origin_only_so_a_trace_span_never_carries_the_ip()
    {
        var (e, handler, _) = Build(_ => Json("{}"));
        await e.DeniedReasonAsync("a@fine.example", Ip, default);

        HttpRequestMessage request = handler.Requests.Single();
        request.RequestUri!.AbsolutePath.Should().Contain(Ip);
        request.Options.TryGetValue(ReelBolt.Shared.Observability.ReelBoltTelemetry.OriginOnlyUrl, out string? origin).Should().BeTrue();
        origin.Should().Be("https://ipinfo.io");
    }

    [Fact]
    public async Task Ipinfo_is_called_once_per_ip_then_cached()
    {
        var (e, handler, _) = Build(_ => Json("""{"vpn":true}"""));
        (await e.DeniedReasonAsync("a@fine.example", Ip, default)).Should().Be("vpn_proxy_hosting");
        (await e.DeniedReasonAsync("b@fine.example", Ip, default)).Should().Be("vpn_proxy_hosting");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Ipinfo_failures_and_a_missing_token_and_a_missing_ip_fail_open()
    {
        long failures = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reelbolt.billing.first_video_ipinfo_failures") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failures, value));
        listener.Start();

        var (error, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        (await error.DeniedReasonAsync("a@fine.example", Ip, default)).Should().BeNull();
        var (garbage, _, _) = Build(_ => Json("not json"));
        (await garbage.DeniedReasonAsync("a@fine.example", Ip, default)).Should().BeNull();
        failures.Should().Be(2);

        var (noToken, handler, _) = Build(_ => Json("""{"vpn":true}"""), withToken: false);
        (await noToken.DeniedReasonAsync("a@fine.example", Ip, default)).Should().BeNull();
        var (noIp, handler2, _) = Build(_ => Json("""{"vpn":true}"""));
        (await noIp.DeniedReasonAsync("a@fine.example", null, default)).Should().BeNull();
        handler.Calls.Should().Be(0);
        handler2.Calls.Should().Be(0);
        failures.Should().Be(2);
    }

    [Fact]
    public async Task An_ipinfo_timeout_or_cancelled_request_fails_open_and_counts_a_failure()
    {
        long failures = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reelbolt.billing.first_video_ipinfo_failures") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failures, value));
        listener.Start();

        IpInfoPrivacyClient thrown = new(new HttpClient(new ThrowingHandler()), Config((FirstVideoAbuseKeys.IpInfoToken, Token)));
        (await thrown.IsVpnProxyOrHostingAsync(Ip, default)).Should().BeNull();

        // A hung server: the 2 s budget cancels the request, the caller's token is untouched.
        IpInfoPrivacyClient hung = new(new HttpClient(new HangingHandler()), Config((FirstVideoAbuseKeys.IpInfoToken, Token)));
        (await hung.IsVpnProxyOrHostingAsync("203.0.113.8", default)).Should().BeNull();

        failures.Should().Be(2);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("simulated timeout");
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task An_oversized_tor_list_is_refused_and_the_old_set_is_kept()
    {
        TorExitList list = new();
        list.Replace(["198.51.100.9"]);
        StubHandler handler = new(_ => Json(new string('1', (int)TorExitListRefresher.MaxListBytes + 1024)));
        TorExitListRefresher refresher = new(list, new FakeFactory(handler), Config(), NullLogger<TorExitListRefresher>.Instance);

        (await refresher.RefreshOnceAsync(default)).Should().BeFalse();
        list.Contains("198.51.100.9").Should().BeTrue();
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(Lines);
        public void Dispose()
        {
        }

        private sealed class Capture(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception) + " " + string.Join(' ', (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(kv => $"{kv.Key}={kv.Value}") ?? []));
                }
            }
        }
    }

    private static ServiceProvider ProductionWiring(CapturingProvider logs, StubHandler? ipinfo = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(Config(("Billing:Enabled", "true"), (FirstVideoAbuseKeys.IpInfoToken, Token), (FirstVideoAbuseKeys.IpInfoBaseUrl, "http://ipinfo.test")));
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddFirstVideoGrant();
        if (ipinfo is not null)
        {
            services.AddHttpClient(IpInfoPrivacyClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ipinfo);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_production_ipinfo_client_never_logs_the_ip_or_the_token()
    {
        CapturingProvider logs = new();
        StubHandler ipinfo = new(_ => Json("""{"vpn":true}"""));
        await using ServiceProvider provider = ProductionWiring(logs, ipinfo);

        (await provider.GetRequiredService<IIpReputationClient>().IsVpnProxyOrHostingAsync(Ip, default)).Should().BeTrue();

        ipinfo.Calls.Should().Be(1);
        logs.Lines.Should().NotContain(l => l.Contains(Ip, StringComparison.Ordinal) || l.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_provisioner_resolved_from_the_production_wiring_has_its_eligibility_check()
    {
        await using ServiceProvider provider = ProductionWiring(new CapturingProvider());

        ICreditAccountProvisioner provisioner = provider.GetRequiredService<ICreditAccountProvisioner>();

        typeof(CreditAccountProvisioner).GetField("_eligibility", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provisioner).Should().BeAssignableTo<IFirstVideoEligibility>("a broken registration must not silently skip the abuse checks");
    }

    [Fact]
    public async Task The_tor_list_refresh_replaces_the_set_and_a_failure_keeps_the_old_one()
    {
        TorExitList list = new();
        bool fail = false;
        StubHandler handler = new(_ => fail ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json("198.51.100.9\n198.51.100.10\n"));
        TorExitListRefresher refresher = new(list, new FakeFactory(handler), Config(), NullLogger<TorExitListRefresher>.Instance);

        (await refresher.RefreshOnceAsync(default)).Should().BeTrue();
        list.Contains("198.51.100.9").Should().BeTrue();

        fail = true;
        (await refresher.RefreshOnceAsync(default)).Should().BeFalse();
        list.Contains("198.51.100.9").Should().BeTrue();
        handler.Requests.Last().RequestUri!.ToString().Should().Be(FirstVideoAbuseKeys.DefaultTorListUrl);
    }

    [Fact]
    public void The_vendored_list_is_embedded_and_matches_the_go_copy()
    {
        DisposableEmailDomains.Count.Should().BeGreaterThan(1000);
        DisposableEmailDomains.IsDisposable("x@mailinator.com").Should().BeTrue();
        DisposableEmailDomains.IsDisposable("x@gmail.com").Should().BeFalse();

        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "api", "services", "disposable_domains.txt")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            return;
        }

        static HashSet<string> Domains(IEnumerable<string> lines)
            => lines.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0 && l[0] != '#').ToHashSet();
        HashSet<string> go = Domains(File.ReadLines(Path.Combine(dir, "api", "services", "disposable_domains.txt")));
        go.Count.Should().Be(DisposableEmailDomains.Count, "api/services/disposable_domains.txt and the .NET copy must be updated together");
    }

    // ------------------------------------------------------------------ provisioner

    private sealed class CapturingLogger : ILogger<CreditAccountProvisioner>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private async Task<(Guid User, Guid Org)> Seed(string email, string? signupIp)
    {
        await using InferenceApiDbContext db = NewDb();
        Guid user = Guid.NewGuid();
        Guid org = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = email, EmailVerifiedAt = DateTime.UtcNow, SignupIp = signupIp });
        db.Organizations.Add(new Organization { Id = org, Name = "w", Slug = "w-" + org.ToString("N"), Kind = OrganizationKind.Personal });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = user, Role = OrganizationRole.Owner });
        await db.SaveChangesAsync();
        return (user, org);
    }

    private CreditAccountProvisioner Provisioner(IFirstVideoEligibility eligibility, CapturingLogger logger)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options));
        return new CreditAccountProvisioner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Config(("Billing:Enabled", "true")),
            logger,
            eligibility: eligibility);
    }

    private async Task<long> Balance(Guid org)
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditAccounts.Where(a => a.OrganizationId == org).Select(a => a.BalanceMillis).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task A_refused_account_still_gets_the_trial_the_reason_is_stored_and_the_ip_is_never_logged()
    {
        var (eligibility, _, tor) = Build(_ => Json("{}"));
        tor.Replace([Ip]);
        CapturingLogger logger = new();
        (Guid user, Guid org) = await Seed("fine@example.com", Ip);
        CreditAccountProvisioner p = Provisioner(eligibility, logger);

        (await p.EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.Granted);
        (await p.EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.AlreadyClaimed);

        (await Balance(org)).Should().Be(100_000);
        await using InferenceApiDbContext db = NewDb();
        db.CreditGrants.Count(g => g.OrganizationId == org && g.Source == CreditGrantSource.Promo).Should().Be(0);
        TrialClaim claim = db.TrialClaims.Single(c => c.OrganizationId == org);
        claim.FirstVideoDeniedReason.Should().Be("tor_exit");
        logger.Messages.Should().NotContain(m => m.Contains(Ip, StringComparison.Ordinal) || m.Contains(Token, StringComparison.Ordinal));
        logger.Messages.Should().Contain(m => m.Contains("tor_exit") && m.Contains(org.ToString()));
    }

    [Fact]
    public async Task A_clean_account_gets_the_grant_with_no_reason_and_a_dns_or_ipinfo_failure_grants_normally()
    {
        var (eligibility, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout), mx: null);
        (Guid user, Guid org) = await Seed("clean@example.com", Ip);

        (await Provisioner(eligibility, new CapturingLogger()).EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.Granted);

        (await Balance(org)).Should().Be(850_000);
        await using InferenceApiDbContext db = NewDb();
        db.TrialClaims.Single(c => c.OrganizationId == org).FirstVideoDeniedReason.Should().BeNull();
    }

    [Fact]
    public async Task A_disposable_email_is_refused_the_grant_but_keeps_the_trial()
    {
        var (eligibility, _, _) = Build(_ => Json("{}"));
        (Guid user, Guid org) = await Seed("burner@mailinator.com", null);

        await Provisioner(eligibility, new CapturingLogger()).EnsureTrialGrantAsync(user, org);

        (await Balance(org)).Should().Be(100_000);
        await using InferenceApiDbContext db = NewDb();
        db.TrialClaims.Single(c => c.OrganizationId == org).FirstVideoDeniedReason.Should().Be("disposable_email");
    }
}
