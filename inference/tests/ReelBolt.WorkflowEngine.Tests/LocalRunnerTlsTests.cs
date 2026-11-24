using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>TLS trust for the LocalRunner relay against a real loopback TLS server (all certificates generated in memory).</summary>
public class LocalRunnerTlsTests : IDisposable
{
    private const string Completion =
        """{"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"secure hello"},"finish_reason":"stop"}]}""";

    private readonly string _dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "lr-tls-" + Guid.NewGuid().ToString("N"));

    public LocalRunnerTlsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static X509Certificate2 NewCa(string name)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest req = new($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static X509Certificate2 NewLeaf(X509Certificate2 ca, string name, bool server, IPAddress? sanIp = null)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest req = new($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            SubjectAlternativeNameBuilder san = new();
            san.AddIpAddress(sanIp ?? IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
        }

        byte[] serial = Guid.NewGuid().ToByteArray();
        using X509Certificate2 signed = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), serial);
        return X509CertificateLoader.LoadPkcs12(signed.CopyWithPrivateKey(key).Export(X509ContentType.Pfx), null);
    }

    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private readonly bool _requireClient;
        public string Url => $"https://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}";
        public List<string?> ClientSubjects { get; } = [];
        public TlsServer(X509Certificate2 cert, bool requireClient)
        {
            _cert = cert; _requireClient = requireClient;
            _l.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using TcpClient tc = c;
                    try
                    {
                        using SslStream ssl = new(tc.GetStream(), false, (_, cert, _, _) => !_requireClient || cert is not null);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _cert,
                            ClientCertificateRequired = _requireClient,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                        });
                        lock (ClientSubjects) ClientSubjects.Add(ssl.RemoteCertificate?.Subject);
                        byte[] buf = new byte[65536];
                        StringBuilder sb = new();
                        while (!sb.ToString().Contains("\r\n\r\n"))
                        {
                            int n = await ssl.ReadAsync(buf);
                            if (n <= 0) return;
                            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        }

                        // Drain a small body if announced, then answer.
                        string head = sb.ToString();
                        int idx = head.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                        int cl = 0;
                        foreach (string line in head[..idx].Split("\r\n"))
                            if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)) cl = int.Parse(line[15..].Trim());
                        int have = head.Length - idx;
                        while (have < cl) { int n = await ssl.ReadAsync(buf); if (n <= 0) break; have += n; }
                        byte[] body = Encoding.UTF8.GetBytes(Completion);
                        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                        await ssl.WriteAsync(body);
                        await ssl.FlushAsync();
                    }
                    catch { /* handshake refused */ }
                });
            }
        }

        public void Dispose() => _l.Stop();
    }

    private static ResolvedInferenceProvider Row(Guid org) => new(
        Guid.NewGuid(), "laptop", InferenceProviderKind.LocalRunner, Guid.NewGuid().ToString(), "m", string.Empty, 10,
        OrganizationId: org, IsPlatformManaged: false);

    private static LocalRunnerGatewayOptions Opts(string url) => new() { GatewayBaseUrl = url, InternalToken = "t0ken" };

    private string Pem(string name, X509Certificate2 c)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, c.ExportCertificatePem());
        return p;
    }

    private (string cert, string key) PemPair(string name, X509Certificate2 c, string? password = null)
    {
        string cp = Path.Combine(_dir, name + ".crt"), kp = Path.Combine(_dir, name + ".key");
        File.WriteAllText(cp, c.ExportCertificatePem());
        using RSA rsa = c.GetRSAPrivateKey()!;
        File.WriteAllText(kp, password is null
            ? rsa.ExportPkcs8PrivateKeyPem()
            : rsa.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));
        return (cp, kp);
    }

    private static async Task<ChatResponse> Chat(LocalRunnerGatewayOptions o)
    {
        IChatClient client = ChatClientFactory.BuildLocalRunner(Row(Guid.NewGuid()), o);
        return await client.GetResponseAsync("hi");
    }

    [Fact]
    public async Task Right_ca_is_trusted_and_the_call_succeeds()
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using TlsServer srv = new(leaf, false);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("ca.pem", ca);
        (await Chat(o)).Text.Should().Be("secure hello");
    }

    [Fact]
    public async Task Embedding_path_uses_the_same_trust()
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using TlsServer srv = new(leaf, false);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("ca.pem", ca);
        var provider = new ResolvedEmbeddingProvider(Guid.NewGuid(), "e", InferenceProviderKind.LocalRunner, Guid.NewGuid().ToString(), "m", string.Empty, 10,
            OrganizationId: Guid.NewGuid(), IsPlatformManaged: false);
        var gen = EmbeddingClientFactory.BuildLocalRunner(provider, o);
        // The fake answers with a chat body; only the completed TLS handshake and request matter here.
        try { await gen.GenerateAsync(["x"]); } catch (LocalRunnerUnavailableException) { /* body shape */ }
        srv.ClientSubjects.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handler_is_built_once_per_options_snapshot()
    {
        using X509Certificate2 ca = NewCa("good");
        LocalRunnerGatewayOptions o = Opts("https://127.0.0.1:1"); o.GatewayCaFile = Pem("ca.pem", ca);
        o.Tls.GetHandler().Should().BeSameAs(o.Tls.GetHandler());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Wrong_ca_fails_with_the_typed_unavailable_error()
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 other = NewCa("other");
        using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using TlsServer srv = new(leaf, false);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("other.pem", other);
        Func<Task> act = () => Chat(o);
        (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.GatewayCode.Should().Be("tls_handshake_failed");
    }

    [Fact]
    public async Task Untrusted_self_signed_server_is_refused()
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 self = NewCa("self");
        using X509Certificate2 selfWithKey = NewLeaf(self, "gw", true);
        using TlsServer srv = new(selfWithKey, false);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("ca.pem", ca);
        Func<Task> act = () => Chat(o);
        (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.GatewayCode.Should().Be("tls_handshake_failed");
    }

    [Fact]
    public async Task Without_a_ca_file_a_private_ca_is_not_trusted()
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using TlsServer srv = new(leaf, false);
        Func<Task> act = () => Chat(Opts(srv.Url));
        (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.GatewayCode.Should().Be("tls_handshake_failed");
    }

    [Fact]
    public async Task Server_demanding_a_client_cert_refuses_a_caller_without_one()
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using TlsServer srv = new(leaf, true);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("ca.pem", ca);
        Func<Task> act = () => Chat(o);
        // With TLS 1.3 the server's refusal of a missing client certificate arrives after the client's handshake
        // completed, as a dropped connection (no AuthenticationException); either typed code is a correct refusal.
        (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.GatewayCode.Should().BeOneOf("tls_handshake_failed", "gateway_unreachable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_cert_is_presented_for_mutual_tls(bool encrypted)
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using X509Certificate2 client = NewLeaf(ca, "relay-client", false);
        using TlsServer srv = new(leaf, true);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("ca.pem", ca);
        string? pw = encrypted ? "pw-" + Guid.NewGuid().ToString("N") : null;
        (o.GatewayClientCertFile, o.GatewayClientKeyFile) = PemPair("client", client, pw);
        if (pw is not null) o.GatewayClientKeyPassword = pw;
        (await Chat(o)).Text.Should().Be("secure hello");
        srv.ClientSubjects.Should().Contain("CN=relay-client");
    }

    [Fact]
    public void Require_tls_refuses_an_http_url_with_the_typed_not_set_up_error()
    {
        LocalRunnerGatewayOptions o = Opts("http://127.0.0.1:9"); o.GatewayRequireTls = true;
        Action act = () => ChatClientFactory.BuildLocalRunner(Row(Guid.NewGuid()), o);
        LocalRunnerUnavailableException ex = act.Should().Throw<LocalRunnerUnavailableException>().Which;
        ex.Code.Should().Be("LOCAL_RUNNER_UNAVAILABLE"); ex.GatewayCode.Should().Be("tls_required");
        ex.Message.Should().Contain("not secured").And.NotContain("t0ken");
    }

    [Fact]
    public void Require_tls_allows_https()
    {
        using X509Certificate2 ca = NewCa("good");
        LocalRunnerGatewayOptions o = Opts("https://gw.example:8443"); o.GatewayRequireTls = true; o.GatewayCaFile = Pem("good.pem", ca);
        ChatClientFactory.BuildLocalRunner(Row(Guid.NewGuid()), o).Should().NotBeNull();
    }

    [Fact]
    public void Missing_or_invalid_files_give_a_typed_failure_without_path_or_secret()
    {
        string missing = Path.Combine(_dir, "nope-secret-path.pem");
        LocalRunnerGatewayOptions o = Opts("https://127.0.0.1:9"); o.GatewayCaFile = missing;
        Action a = () => ChatClientFactory.BuildLocalRunner(Row(Guid.NewGuid()), o);
        a.Should().Throw<LocalRunnerUnavailableException>().Which.Message.Should().NotContain("nope-secret-path");

        string junk = Path.Combine(_dir, "junk.pem"); File.WriteAllText(junk, "not a certificate");
        LocalRunnerGatewayOptions o2 = Opts("https://127.0.0.1:9"); o2.GatewayCaFile = junk;
        Action b = () => ChatClientFactory.BuildLocalRunner(Row(Guid.NewGuid()), o2);
        b.Should().Throw<LocalRunnerUnavailableException>();

        LocalRunnerGatewayOptions o3 = Opts("https://127.0.0.1:9"); o3.GatewayClientCertFile = missing; o3.GatewayClientKeyFile = missing; o3.GatewayClientKeyPassword = "hunter2-pw";
        Action c = () => ChatClientFactory.BuildLocalRunner(Row(Guid.NewGuid()), o3);
        c.Should().Throw<LocalRunnerUnavailableException>().Which.Message.Should().NotContain("hunter2-pw").And.NotContain("nope-secret-path");
    }

    [Fact]
    public async Task Server_certificate_name_mismatch_is_refused_with_tls_handshake_failed()
    {
        using X509Certificate2 ca = NewCa("good");
        using X509Certificate2 leaf = NewLeaf(ca, "gw", true, IPAddress.Parse("10.99.99.99"));
        using TlsServer srv = new(leaf, false);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("good.pem", ca);
        Func<Task> act = () => Chat(o);
        LocalRunnerUnavailableException ex = (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which;
        ex.GatewayCode.Should().Be("tls_handshake_failed");
        ex.Message.Should().Contain("secure connection").And.Contain("certificates").And.NotContain(_dir);
        ex.InnerException.Should().NotBeNull();
    }

    [Fact]
    public async Task Tls_failure_is_logged_at_warning_with_the_exception()
    {
        using X509Certificate2 ca = NewCa("good"); using X509Certificate2 other = NewCa("other");
        using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using TlsServer srv = new(leaf, false);
        LocalRunnerGatewayOptions o = Opts(srv.Url); o.GatewayCaFile = Pem("other.pem", other);
        CapturingLogger log = new();
        IChatClient client = ChatClientFactory.BuildLocalRunner(Row(Guid.NewGuid()), o, null, log);
        Func<Task> act = () => client.GetResponseAsync("hi");
        await act.Should().ThrowAsync<LocalRunnerUnavailableException>();
        log.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Ex != null);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Ex)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, exception));
        }
    }

    [Fact]
    public async Task Ec_prime256v1_openssl_style_pem_pair_is_accepted_for_mutual_tls()
    {
        using X509Certificate2 ca = NewCa("good");
        using X509Certificate2 leaf = NewLeaf(ca, "gw", true);
        using TlsServer srv = new(leaf, true);
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest req = new("CN=relay-client", ec, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using X509Certificate2 signed = req.Create(ca.SubjectName, X509SignatureGenerator.CreateForRSA(ca.GetRSAPrivateKey()!, RSASignaturePadding.Pkcs1), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid().ToByteArray());
        string cp = Path.Combine(_dir, "ec.crt"), kp = Path.Combine(_dir, "ec.key");
        File.WriteAllText(cp, signed.ExportCertificatePem());
        string keyPem = ec.ExportECPrivateKeyPem();
        keyPem.Should().StartWith("-----BEGIN EC " + "PRIVATE KEY-----");
        File.WriteAllText(kp, keyPem);
        LocalRunnerGatewayOptions o = Opts(srv.Url);
        o.GatewayCaFile = Pem("good.pem", ca); o.GatewayClientCertFile = cp; o.GatewayClientKeyFile = kp;
        (await Chat(o)).Text.Should().Be("secure hello");
        srv.ClientSubjects.Should().Contain("CN=relay-client");
    }

    [Theory]
    [InlineData("internal_tls_required")]
    [InlineData("tls_handshake_failed")]
    public void New_gateway_codes_are_known_with_their_own_sentence(string code)
    {
        LocalRunnerUnavailableException.IsGatewayCode(code).Should().BeTrue();
        string reason = LocalRunnerUnavailableException.ReasonFor(HttpStatusCode.Forbidden, code);
        reason.Should().Contain("administrator").And.Contain("secure");
        if (code == "internal_tls_required") reason.Should().Contain("secure address");
    }

    [Fact]
    public void Startup_validator_rules()
    {
        static IReadOnlyList<string> V(Action<LocalRunnerGatewayOptions> f) { LocalRunnerGatewayOptions o = new(); f(o); return LocalRunnerGatewayOptionsValidator.Check(o); }
        V(_ => { }).Should().BeEmpty();
        V(o => { o.GatewayBaseUrl = "https://gw:8443"; o.GatewayRequireTls = true; o.GatewayCaFile = "/x/ca.pem"; }).Should().BeEmpty();
        V(o => { o.GatewayRequireTls = true; o.GatewayCaFile = "/x/ca.pem"; }).Should().Contain(m => m.Contains("RequireTls") && m.Contains("https"));
        V(o => { o.GatewayBaseUrl = "https://gw"; o.GatewayRequireTls = true; }).Should().ContainSingle().Which.Should().Contain("GatewayCaFile");
        V(o => { o.GatewayBaseUrl = "https://gw"; o.GatewayClientCertFile = "/x/c"; }).Should().ContainSingle().Which.Should().Contain("together");
        V(o => { o.GatewayBaseUrl = "https://gw"; o.GatewayClientKeyFile = "/x/k"; }).Should().ContainSingle().Which.Should().Contain("together");
        V(o => { o.GatewayBaseUrl = "https://gw"; o.GatewayClientKeyPassword = "pw-secret"; }).Should().ContainSingle().Which.Should().Contain("KeyFile").And.NotContain("pw-secret");
        V(o => { o.GatewayBaseUrl = "http://gw"; o.GatewayCaFile = "/x/ca.pem"; }).Should().ContainSingle().Which.Should().Contain("http");
        V(o => { o.GatewayBaseUrl = "https://gw"; o.GatewayCaFile = "/x/ca.pem"; o.GatewayClientCertFile = "/x/c"; o.GatewayClientKeyFile = "/x/k"; o.GatewayClientKeyPassword = "p"; }).Should().BeEmpty();
        new LocalRunnerGatewayOptionsValidator().Validate(null, new LocalRunnerGatewayOptions { GatewayRequireTls = true }).Failed.Should().BeTrue();
    }
}
