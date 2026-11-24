
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Account;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// C9a's mail path: the Inference API has no SMTP settings at all (C0), so every mail it triggers is a
/// POST to the Go API's private endpoint. These tests pin that contract — the path, the bearer token,
/// the closed template list, and the "never fail the caller" behaviour.
/// </summary>
public class GoAccountMailClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly bool _throw;

        public Handler(HttpStatusCode status = HttpStatusCode.Accepted, bool throwing = false)
        {
            _status = status;
            _throw = throwing;
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throw)
                throw new HttpRequestException("go api unreachable");

            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(request);
            return new HttpResponseMessage(_status);
        }
    }

    private static (IAccountMailClient Client, Handler Handler) Build(string token, HttpStatusCode status = HttpStatusCode.Accepted, bool throwing = false)
    {
        var handler = new Handler(status, throwing);
        var options = new AccountOptions
        {
            InternalBaseUrl = "http://go-api:8080",
            InternalToken = token
        };
        var http = new HttpClient(handler);
        return (new GoAccountMailClient(http, Options.Create(options), NullLogger<GoAccountMailClient>.Instance), handler);
    }

    private static AccountMail ExportReady() => new(
        "ada@example.com",
        "export_ready",
        new AccountMailData { Name = "Ada", Link = "https://store.test/export.zip", ExpiresIn = "7 days" });

    [Fact]
    public async Task Posts_the_internal_endpoint_with_the_bearer_token_and_the_data()
    {
        var (client, handler) = Build("s3cret");

        (await client.SendAsync(ExportReady(), CancellationToken.None)).Should().BeTrue();

        handler.Requests.Should().ContainSingle();
        HttpRequestMessage request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("http://go-api:8080/internal/mail/send");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("s3cret");

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("to").GetString().Should().Be("ada@example.com");
        body.RootElement.GetProperty("template").GetString().Should().Be("export_ready");
        body.RootElement.GetProperty("data").GetProperty("expiresIn").GetString().Should().Be("7 days");
        body.RootElement.GetProperty("data").GetProperty("link").GetString().Should().Be("https://store.test/export.zip");
    }

    [Fact]
    public async Task Sends_nothing_when_no_internal_token_is_configured()
    {
        var (client, handler) = Build("");

        (await client.SendAsync(ExportReady(), CancellationToken.None)).Should().BeFalse(
            "mail is best-effort: an install without the shared secret logs and carries on");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_a_template_outside_the_closed_list()
    {
        var (client, handler) = Build("s3cret");

        AccountMail unknown = new("ada@example.com", "marketing_blast", new AccountMailData { Link = "https://x.test" });
        (await client.SendAsync(unknown, CancellationToken.None)).Should().BeFalse();
        handler.Requests.Should().BeEmpty("the endpoint only accepts the C2 templates, and so must the sender");
    }

    [Fact]
    public async Task Reports_a_refusal_and_an_unreachable_endpoint_without_throwing()
    {
        var (refused, _) = Build("s3cret", HttpStatusCode.NotFound);
        (await refused.SendAsync(ExportReady(), CancellationToken.None)).Should().BeFalse();

        var (unreachable, _) = Build("s3cret", throwing: true);
        (await unreachable.SendAsync(ExportReady(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public void The_closed_template_list_covers_the_mails_this_service_sends()
    {
        GoAccountMailClient.AllowedTemplates.Should().Contain("export_ready");
        GoAccountMailClient.AllowedTemplates.Should().Contain("marketing-confirm");
        GoAccountMailClient.AllowedTemplates.Should().NotContain("marketing_blast");
    }

    [Fact]
    public async Task Sends_the_marketing_confirmation_with_its_link()
    {
        var (client, handler) = Build("s3cret");

        AccountMail confirm = new("visitor@example.com", "marketing-confirm",
            new AccountMailData { Link = "https://app.test/app/marketing/confirm?token=c1.k1.x.1.y", ExpiresIn = "7 days" });
        (await client.SendAsync(confirm, CancellationToken.None)).Should().BeTrue();

        using JsonDocument body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        body.RootElement.GetProperty("template").GetString().Should().Be("marketing-confirm");
        body.RootElement.GetProperty("data").GetProperty("link").GetString()
            .Should().Be("https://app.test/app/marketing/confirm?token=c1.k1.x.1.y");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    public async Task SendWithStatusAsync_passes_the_relays_http_status_through(HttpStatusCode status, int expected)
    {
        var (client, _) = Build("s3cret", status);

        AccountMailResult result = await client.SendWithStatusAsync(ExportReady(), CancellationToken.None);

        result.Sent.Should().BeFalse();
        result.HttpStatus.Should().Be(expected, "a 4xx is a refusal, a 5xx is transient; the caller tells them apart by this");
    }

    [Fact]
    public async Task SendWithStatusAsync_reports_no_status_when_the_relay_is_unreachable_and_the_status_on_success()
    {
        var (down, _) = Build("s3cret", throwing: true);
        (await down.SendWithStatusAsync(ExportReady(), CancellationToken.None)).Should().Be(new AccountMailResult(false, null));

        var (up, _) = Build("s3cret");
        (await up.SendWithStatusAsync(ExportReady(), CancellationToken.None)).Should().Be(new AccountMailResult(true, 202));
    }
}
