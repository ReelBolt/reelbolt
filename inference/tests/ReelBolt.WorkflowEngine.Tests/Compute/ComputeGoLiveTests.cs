using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The narrow pool switch: with <c>Compute:Mode=Fabric</c> the pool can be opened to chosen job types only,
/// desktop runners can be left out, and nothing goes to the pool while the object store address is one a pool
/// node cannot reach. Defaults are today's behaviour.
/// </summary>
public class ComputeGoLiveTests
{
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-000000000002");

    private static async Task<ComputeDecision> Decide(
        Action<ComputeOptions> tweak,
        RunnerJobType jobType,
        ComputePreference preference = ComputePreference.Auto,
        bool poolOnline = true,
        bool desktopOnline = false)
    {
        List<(string Kind, Guid? OrgId, string[] JobTypes)> runners = [];
        if (poolOnline)
        {
            runners.Add(("CloudPool", null, ComputeTestFactory.AllJobTypes));
        }

        if (desktopOnline)
        {
            runners.Add(("Desktop", Org, ComputeTestFactory.AllJobTypes));
        }

        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.RunnersBody([.. runners]))));
        ComputeOptions options = ComputeTestFactory.Options();
        tweak(options);
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(preference, true, true)),
            ComputeTestFactory.Gateway(handler, options),
            options);
        return await selector.ResolveAsync(new ComputeTargetRequest(
            Guid.NewGuid(), Org, jobType, Guid.NewGuid(), "VideoCompile", 0, "video.compile"));
    }

    [Fact]
    public async Task Defaults_RouteEveryJobTypeToTheOnlinePool()
    {
        foreach (RunnerJobType t in Enum.GetValues<RunnerJobType>())
        {
            ComputeDecision d = await Decide(_ => { }, t);
            d.Target.Should().Be(ComputeTargetKind.CloudPool, t.ToString());
        }
    }

    [Fact]
    public async Task JobTypeList_SendsListedTypesToThePool_AndKeepsTheRestLocal()
    {
        void Open(ComputeOptions o) => o.CloudPoolJobTypes = " video.compile , video.analyze.extract ";

        (await Decide(Open, RunnerJobType.VideoCompile)).Target.Should().Be(ComputeTargetKind.CloudPool);
        (await Decide(Open, RunnerJobType.VideoAnalyzeExtract)).Target.Should().Be(ComputeTargetKind.CloudPool);

        ComputeDecision session = await Decide(Open, RunnerJobType.RemotionWorkspaceSession);
        session.Target.Should().Be(ComputeTargetKind.Local);
        session.Reason.Should().Be(ComputeDecisionReasons.PoolJobTypeNotEnabled);
    }

    [Fact]
    public async Task JobTypeList_AlsoNarrowsAWakeAndWaitDecision()
    {
        ComputeDecision d = await Decide(
            o => { o.CloudPoolJobTypes = "video.compile"; o.PoolWaker.Enabled = true; },
            RunnerJobType.VideoAnalyzeExtract, poolOnline: false);
        d.Target.Should().Be(ComputeTargetKind.Local);
        d.Reason.Should().Be(ComputeDecisionReasons.PoolJobTypeNotEnabled);
    }

    [Fact]
    public async Task WakeAndWait_StillHappensForAnAllowedTypeOnPublicStorage_AndIsBlockedOtherwise()
    {
        static string Body(bool healthy) => ComputeTestFactory.RunnersBodyWithPool(healthy);
        async Task<ComputeDecision> Go(Action<ComputeOptions> tweak, RunnerJobType t)
        {
            StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(Body(true))));
            ComputeOptions options = ComputeTestFactory.Options();
            options.PoolWaker.Enabled = true;
            tweak(options);
            ComputeTargetSelector selector = ComputeTestFactory.Selector(
                new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.Auto, true, true)),
                ComputeTestFactory.Gateway(handler, options), options);
            return await selector.ResolveAsync(new ComputeTargetRequest(Guid.NewGuid(), Org, t, Guid.NewGuid(), "VideoCompile", 0, "video.compile"));
        }

        ComputeDecision ok = await Go(o => { o.CloudPoolJobTypes = "video.compile"; o.PoolStorageEndpoint = "https://s3.example.net"; }, RunnerJobType.VideoCompile);
        ok.IsPoolWakeAndWait.Should().BeTrue();
        ComputeDecision blocked = await Go(o => o.PoolStorageEndpoint = "http://10.0.0.2:9000", RunnerJobType.VideoCompile);
        blocked.Reason.Should().Be(ComputeDecisionReasons.PoolStorageUnreachable);
    }

    [Fact]
    public async Task OrgRunnersDisabled_RunnerOnlyStaysLocal_EvenWhenTheGatewayIsDown()
    {
        StubHttpMessageHandler down = new(_ => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));
        ComputeOptions options = ComputeTestFactory.Options();
        options.OrgRunnersEnabled = false;
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(ComputePreference.RunnerOnly, true, true)),
            ComputeTestFactory.Gateway(down, options), options);
        ComputeDecision d = await selector.ResolveAsync(new ComputeTargetRequest(Guid.NewGuid(), Org, RunnerJobType.VideoCompile, Guid.NewGuid(), "VideoCompile", 0, "video.compile"));
        d.IsFailure.Should().BeFalse();
        d.Target.Should().Be(ComputeTargetKind.Local);
    }

    [Fact]
    public async Task OrgRunnersDisabled_AnOnlineDesktopRunnerIsIgnored()
    {
        ComputeDecision on = await Decide(_ => { }, RunnerJobType.VideoCompile, desktopOnline: true);
        on.Target.Should().Be(ComputeTargetKind.OrgRunner);

        ComputeDecision off = await Decide(o => o.OrgRunnersEnabled = false, RunnerJobType.VideoCompile, desktopOnline: true);
        off.Target.Should().Be(ComputeTargetKind.CloudPool);
    }

    [Fact]
    public async Task OrgRunnersDisabled_RunnerOnlyOrganizationStaysLocal_NeverTheCloud()
    {
        ComputeDecision d = await Decide(o => o.OrgRunnersEnabled = false, RunnerJobType.VideoCompile, ComputePreference.RunnerOnly, desktopOnline: true);
        d.Target.Should().Be(ComputeTargetKind.Local);
        d.Reason.Should().Be(ComputeDecisionReasons.OrgRunnersDisabled);
        d.IsFailure.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://10.0.0.2:9000")]
    [InlineData("https://10.0.0.2:9000")]
    [InlineData("http://garage:3900")]
    [InlineData("")]
    public async Task PoolStorage_NotPublicHttps_KeepsPoolJobsLocal(string endpoint)
    {
        bool expectPool = false; // none of these rows is a public https address
        ComputeDecision d = await Decide(o => o.PoolStorageEndpoint = endpoint, RunnerJobType.VideoCompile);
        if (expectPool)
        {
            d.Target.Should().Be(ComputeTargetKind.CloudPool);
        }
        else
        {
            d.Target.Should().Be(ComputeTargetKind.Local);
            d.Reason.Should().Be(ComputeDecisionReasons.PoolStorageUnreachable);
        }
    }

    [Fact]
    public async Task PoolStorage_PublicHttps_OrCheckOff_OrUnknown_AllowsThePool()
    {
        (await Decide(o => o.PoolStorageEndpoint = "https://s3.eu.example.net", RunnerJobType.VideoCompile)).Target.Should().Be(ComputeTargetKind.CloudPool);
        (await Decide(o => { o.PoolStorageEndpoint = "http://10.0.0.2:9000"; o.RequirePublicStorageForPool = false; }, RunnerJobType.VideoCompile)).Target.Should().Be(ComputeTargetKind.CloudPool);
        (await Decide(o => o.PoolStorageEndpoint = null, RunnerJobType.VideoCompile)).Target.Should().Be(ComputeTargetKind.CloudPool);
    }

    [Fact]
    public async Task LocalMode_IsUnchangedByEveryNewSetting()
    {
        ComputeDecision d = await Decide(
            o => { o.Mode = ComputeMode.Local; o.CloudPoolJobTypes = "video.compile"; o.OrgRunnersEnabled = false; o.PoolStorageEndpoint = "http://x:1"; },
            RunnerJobType.VideoCompile);
        d.Reason.Should().Be(ComputeDecisionReasons.LocalMode);
    }

    [Theory]
    [InlineData("https://s3.eu-central-1.example.com", true)]
    [InlineData("https://1.2.3.4:9000", true)]
    [InlineData("https://[2606:4700::1]", true)]
    [InlineData("http://s3.example.com", false)]
    [InlineData("https://localhost:9000", false)]
    [InlineData("https://garage", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("https://10.0.0.2:9000", false)]
    [InlineData("https://172.16.4.1", false)]
    [InlineData("https://172.32.4.1", true)]
    [InlineData("https://192.168.1.5", false)]
    [InlineData("https://169.254.169.254", false)]
    [InlineData("https://100.64.0.1", false)]
    [InlineData("https://[::1]", false)]
    [InlineData("https://[fd00::1]", false)]
    [InlineData("https://[::ffff:10.0.0.2]", false)]
    [InlineData("https://minio.internal", false)]
    [InlineData("https://nas.local", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsPublicHttps_Table(string? endpoint, bool expected)
        => PoolStorageReachability.IsPublicHttps(endpoint).Should().Be(expected);
}

/// <summary>The compose files must carry every go-live setting with the default the code uses.</summary>
public class ComposePoolWakerWiringTests
{
    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")) && Directory.Exists(Path.Combine(dir.FullName, "inference")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("repo root not found above " + AppContext.BaseDirectory);
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

    [Fact]
    public void Engine_ReceivesTheSwitches_WithTheCodeDefaults()
    {
        string compose = Read("docker-compose.yml");
        ComputeOptions o = new();
        compose.Should().Contain("Compute__PoolWaker__Enabled: ${POOL_WAKER_ENGINE_ENABLED:-false}");
        o.PoolWaker.Enabled.Should().BeFalse();
        compose.Should().Contain("Compute__PoolWaker__WaitSeconds: ${POOL_WAKER_WAIT_SECONDS:-" + o.PoolWaker.WaitSeconds + "}");
        compose.Should().Contain("Compute__CloudPoolJobTypes: ${COMPUTE_CLOUD_POOL_JOB_TYPES:-}");
        o.CloudPoolJobTypes.Should().BeEmpty();
        compose.Should().Contain("Compute__OrgRunnersEnabled: ${COMPUTE_ORG_RUNNERS_ENABLED:-" + o.OrgRunnersEnabled.ToString().ToLowerInvariant() + "}");
        compose.Should().Contain("Compute__RequirePublicStorageForPool: ${COMPUTE_REQUIRE_PUBLIC_STORAGE_FOR_POOL:-" + o.RequirePublicStorageForPool.ToString().ToLowerInvariant() + "}");
        compose.Should().Contain("Compute__Mode: ${COMPUTE_MODE:-Local}");
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.media.yml")]
    public void Gateway_ReceivesThePoolTokenKey(string file)
        => Read(file).Should().Contain("RUNNER_POOL_TOKEN_KEY: ${RUNNER_POOL_TOKEN_KEY:-}");

    [Fact]
    public void EveryNewSetting_IsDocumentedInEnvExample()
    {
        string env = Read(".env.example");
        foreach (string name in new[]
        {
            "RUNNER_POOL_TOKEN_KEY", "POOL_WAKER_ENGINE_ENABLED", "POOL_WAKER_WAIT_SECONDS", "COMPUTE_CLOUD_POOL_JOB_TYPES",
            "COMPUTE_ORG_RUNNERS_ENABLED", "COMPUTE_REQUIRE_PUBLIC_STORAGE_FOR_POOL",
        })
        {
            env.Should().MatchRegex("(?m)^" + name + "=", name);
        }

        // Safe defaults: a copied .env.example turns nothing on.
        env.Should().MatchRegex("(?m)^POOL_WAKER_ENGINE_ENABLED=false$");
        env.Should().MatchRegex("(?m)^COMPUTE_MODE=Local$");
    }

    /// <summary>The pool-waker service block of the control compose file (the waker left the media host).</summary>
    private static string WakerBlock(out string control)
    {
        control = Read("docker-compose.control.yml");
        int start = control.IndexOf("\n  pool-waker:", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the pool waker is a service of docker-compose.control.yml");
        int end = control.IndexOf("\n  # ----", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return control[start..end];
    }

    [Fact]
    public void PoolWaker_IsBehindItsOwnProfile_AndHoldsNoSecretAsAnEnvValue()
    {
        string block = WakerBlock(out string control);

        block.Should().Contain("profiles: [\"poolwaker\"]");
        block.Should().Contain("target: pool-waker");
        // The provider token is a file mount, never an environment value.
        block.Should().Contain("HCLOUD_TOKEN_FILE: /run/secrets/hcloud_token");
        block.Should().Contain("- ${POOL_WAKER_TOKEN_FILE:-/dev/null}:/run/secrets/hcloud_token:ro");
        block.Should().NotMatchRegex("(?m)^\\s+HCLOUD_TOKEN:");
        block.Should().Contain("REELBOLT_ROLE: control");
        block.Should().Contain("pool-waker-state:/var/lib/pool-waker");
        block.Should().Contain("read_only: true");
        block.Should().Contain("cap_drop: [ALL]");
        block.Should().Contain("no-new-privileges:true");
        block.Should().NotContain("docker.sock");
        block.Should().NotContain("ports:");
        block.Should().NotContain("network_mode");
        // Compose interpolates every service, profile or not: a :? on a setting only the waker needs would break
        // every command without the waker's settings. ENGINE_PRIVATE_IP is the one exception: the Inference API
        // in the same file already requires it, so the waker adds no new required variable.
        Regex.Matches(block, @"\$\{(\w+):\?").Select(m => m.Groups[1].Value).Distinct().Should().BeEquivalentTo(new[] { "ENGINE_PRIVATE_IP" });
        control.Replace(block, string.Empty).Should().Contain("${ENGINE_PRIVATE_IP:?", "a service outside the waker's profile already requires it");
        control.Should().Contain("pool-waker-state:\n");
    }

    [Fact]
    public void PoolWaker_SitsOnItsOwnNetwork_AndReachesTheGatewayOverMutualTls()
    {
        string block = WakerBlock(out string control);

        // Its own network only: no path to Postgres, RabbitMQ or the API containers.
        Regex.IsMatch(block, @"(?m)^    networks:\s*\n\s+- pool-waker\s*$").Should().BeTrue("the waker joins only its own network");
        Regex.Matches(block, @"(?m)^    networks:\s*\n(\s+- \S+\s*\n?)+").Single().Value.Should().NotContain("- reelbolt");
        Regex.IsMatch(control, @"(?m)^networks:\s*\n(.*\n)*?  pool-waker:").Should().BeTrue("the pool-waker network is declared");

        // The gateway URL is https, and the client certificate, its key and the CA are mounted files.
        block.Should().MatchRegex(@"(?m)^\s+RUNNER_GATEWAY_URL: https://");
        block.Should().NotMatchRegex(@"(?m)^\s+RUNNER_GATEWAY_URL: http://");
        block.Should().Contain("RUNNER_GATEWAY_CA_FILE: /run/gateway-tls/ca.pem");
        block.Should().Contain("RUNNER_GATEWAY_CLIENT_CERT_FILE: /run/gateway-tls/client.pem");
        block.Should().Contain("RUNNER_GATEWAY_CLIENT_KEY_FILE: /run/gateway-tls/client-key.pem");
        block.Should().MatchRegex(@"(?m)^\s+- \$\{POOL_WAKER_GATEWAY_TLS_DIR:-[^}]+\}:/run/gateway-tls:ro$");
        block.Should().NotMatchRegex("(?m)^\\s+RUNNER_GATEWAY_CLIENT_KEY:");

        block.Should().Contain("HCLOUD_NETWORK_SUBNET: ${HCLOUD_NETWORK_SUBNET:-}");
        Read(".env.example").Should().Contain("HCLOUD_NETWORK_SUBNET").And.Contain("POOL_EDGE_PORT").And.Contain("POOL_SUBNET");
    }

    [Fact]
    public void MediaCompose_HasNoPoolWaker_AndNoProviderTokenReference()
    {
        // The media host runs untrusted render workloads: neither the waker nor its cloud provider token belongs there.
        string media = Read("docker-compose.media.yml");

        media.Should().NotMatchRegex(@"(?m)^  pool-waker:");
        media.Should().NotContain("target: pool-waker");
        media.Should().NotMatchRegex("(?i)hcloud");
        media.Should().NotMatchRegex("(?i)hetzner[ _-]?token");
        media.Should().NotContain("POOL_WAKER_TOKEN");
        media.Should().NotMatchRegex(@"(?m)^\s+- pool-waker-state:");
        media.Should().NotMatchRegex(@"(?m)^  pool-waker-state:");
    }

    [Fact]
    public void PoolEdge_PublishesOnlyOnThePrivateAddress_AndForwardsOnlyTheConnectPath()
    {
        string media = Read("docker-compose.media.yml");
        int start = media.IndexOf("\n  pool-edge:", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        int end = media.IndexOf("\n  # The pool waker is NOT on this host", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        string block = media[start..end];

        block.Should().Contain("\"${ENGINE_PRIVATE_IP:?set ENGINE_PRIVATE_IP in .env}:${POOL_EDGE_PORT:-8091}:8080\"");
        Regex.Matches(block, "(?m)^      - \"[^\"]*\\d+\"$").Count.Should().Be(1, "exactly one published port");
        block.Should().NotContain("0.0.0.0").And.NotContain("docker.sock").And.NotContain("network_mode");
        block.Should().Contain("profiles: [\"poolwaker\"]");
        block.Should().Contain("read_only: true");
        block.Should().Contain("cap_drop: [ALL]");
        block.Should().Contain("no-new-privileges:true");
        block.Should().Contain("healthcheck:");
        block.Should().Contain("- reelbolt");
        block.Should().Contain("./infra/compose/pool-edge:/etc/nginx/conf.d:ro");

        string conf = Read("infra/compose/pool-edge/default.conf");
        var locations = Regex.Matches(conf, @"(?m)^\s*location\s+(.+?)\s*\{").Select(m => m.Groups[1].Value).ToList();
        locations.Should().BeEquivalentTo(new[] { "= /edge-health", "= /v1/connect", "/" });
        conf.Should().Contain("proxy_pass $gateway;").And.Contain("set $gateway http://runner-gateway:8090/v1/connect;");
        Regex.Matches(conf, "proxy_pass").Count.Should().Be(1);
        conf.Should().Contain("return 404");
        conf.Should().Contain("limit_conn pool_conn");
        conf.Should().Contain("proxy_read_timeout 120s;").And.Contain("proxy_send_timeout 120s;");
        conf.Should().Contain("proxy_set_header X-Real-IP $remote_addr;").And.Contain("proxy_set_header X-Forwarded-For $remote_addr;");
        conf.Should().NotContain("/internal").And.NotContain("/metrics");
    }

    [Theory]
    [InlineData("infra/scripts/release.sh")]
    [InlineData("infra/scripts/deploy-two-box.sh")]
    public void DeployScripts_NeverNameTheWakerProfile(string script)
    {
        Read(script).Should().NotContain("poolwaker").And.NotContain("pool-waker");
    }

    [Fact]
    public void PoolWakerImageTarget_HasNoDockerSocketOrWorker()
    {
        string docker = Read("sandbox/Dockerfile");
        int a = docker.IndexOf("FROM alpine:3.24 AS pool-waker", StringComparison.Ordinal);
        a.Should().BeGreaterThan(0);
        int b = docker.IndexOf("\n# ──", a, StringComparison.Ordinal);
        string stage = docker[a..b];
        stage.Should().Contain("USER 10001:10001");
        stage.Should().Contain("COPY --from=gobuild /app/reelbolt-scaler");
        stage.Should().NotContain("reelbolt-worker");
        stage.Should().NotContain("docker-cli");
        stage.Should().NotContain("ffmpeg");
    }
}
