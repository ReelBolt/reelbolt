#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;

/// <summary>
/// Connecting an account and keeping its token usable (docs/design/cloud-drive-sync.md "Core services"):
/// the OAuth state is single-use, expires, is bound to the person who started it and to its provider;
/// the return path is a same-origin dashboard path or nothing; tokens refresh near expiry and a rotated
/// refresh token is kept; a refused refresh marks the connection.
/// </summary>
public class CloudStorageConnectionServiceTests
{
    private static string StateOf(string authorizeUrl)
    {
        string query = new Uri(authorizeUrl).Query.TrimStart('?');
        string pair = query.Split('&').Single(p => p.StartsWith("state=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair["state=".Length..]);
    }

    private static async Task<(CloudFixture F, string State)> StartedAsync(string? returnTo = "/app/projects/p1?tab=cloud")
    {
        CloudFixture f = new();
        CloudOAuthStart start = await f.Connections.StartAsync(f.Provider.Kind, f.UserId, f.OrganizationId, returnTo, CancellationToken.None);
        start.Error.Should().BeNull();
        return (f, StateOf(start.AuthorizeUrl!));
    }

    [Fact]
    public async Task Start_stores_only_a_hash_of_the_state_and_an_encrypted_verifier()
    {
        (CloudFixture f, string state) = await StartedAsync();

        CloudStorageOAuthState ticket = await f.Db.CloudStorageOAuthStates.SingleAsync();
        ticket.StateHash.Should().NotBe(state).And.HaveLength(64);
        ticket.ProtectedCodeVerifier.Should().StartWith(ReelBolt.WorkflowEngine.Tests.GitHub.FakeSecretProtector.Prefix);
        ticket.ExpiresAt.Should().Be(f.Clock.UtcNow + TimeSpan.FromMinutes(10));
        ticket.ReturnPath.Should().Be("/app/projects/p1?tab=cloud");
        ticket.UserId.Should().Be(f.UserId);
        ticket.OrganizationId.Should().Be(f.OrganizationId);
    }

    [Fact]
    public async Task Start_refuses_when_the_provider_or_the_public_base_url_is_not_configured()
    {
        CloudFixture unconfigured = new(configured: false);
        (await unconfigured.Connections.StartAsync(unconfigured.Provider.Kind, unconfigured.UserId, unconfigured.OrganizationId, null, CancellationToken.None))
            .Error.Should().Be(CloudOAuthStart.ProviderNotConfigured);

        CloudFixture noBaseUrl = new(publicBaseUrl: null);
        (await noBaseUrl.Connections.StartAsync(noBaseUrl.Provider.Kind, noBaseUrl.UserId, noBaseUrl.OrganizationId, null, CancellationToken.None))
            .Error.Should().Be(CloudOAuthStart.PublicBaseUrlMissing);
        (await noBaseUrl.Db.CloudStorageOAuthStates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Callback_connects_the_account_with_encrypted_tokens_and_returns_to_the_stored_path()
    {
        (CloudFixture f, string state) = await StartedAsync();

        CloudOAuthCompletion done = await f.Connections.CompleteAsync(f.Provider.Kind, state, "the-code", null, f.UserId, CancellationToken.None);

        done.Outcome.Should().Be(CloudOAuthOutcome.Connected);
        done.ReturnPath.Should().Be("/app/projects/p1?tab=cloud");
        CloudStorageConnection connection = await f.Db.CloudStorageConnections.SingleAsync();
        connection.UserId.Should().Be(f.UserId);
        connection.OrganizationId.Should().Be(f.OrganizationId);
        connection.AccountId.Should().Be("acct-1");
        connection.AccountEmail.Should().Be("person@example.com");
        connection.ProtectedAccessToken.Should().NotBe("issued-access");
        f.Secrets.TryUnprotect(connection.ProtectedAccessToken, out string access).Should().BeTrue();
        access.Should().Be("issued-access");
        f.Provider.CodesExchanged.Should().Equal("the-code");
    }

    [Fact]
    public async Task A_state_is_single_use()
    {
        (CloudFixture f, string state) = await StartedAsync();
        (await f.Connections.CompleteAsync(f.Provider.Kind, state, "code-1", null, f.UserId, CancellationToken.None))
            .Outcome.Should().Be(CloudOAuthOutcome.Connected);

        CloudOAuthCompletion replay = await f.Connections.CompleteAsync(f.Provider.Kind, state, "code-2", null, f.UserId, CancellationToken.None);

        replay.Outcome.Should().Be(CloudOAuthOutcome.InvalidState);
        f.Provider.CodesExchanged.Should().Equal("code-1");
    }

    [Fact]
    public async Task An_expired_state_is_refused_and_spent()
    {
        (CloudFixture f, string state) = await StartedAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(11));

        CloudOAuthCompletion done = await f.Connections.CompleteAsync(f.Provider.Kind, state, "code", null, f.UserId, CancellationToken.None);

        done.Outcome.Should().Be(CloudOAuthOutcome.ExpiredState);
        done.Code.Should().Be("expired");
        f.Provider.CodesExchanged.Should().BeEmpty();
        (await f.Db.CloudStorageConnections.CountAsync()).Should().Be(0);
        (await f.Db.CloudStorageOAuthStates.SingleAsync()).ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_callback_made_by_somebody_else_or_signed_out_is_refused_without_burning_the_state()
    {
        (CloudFixture f, string state) = await StartedAsync();

        (await f.Connections.CompleteAsync(f.Provider.Kind, state, "code", null, Guid.NewGuid(), CancellationToken.None))
            .Outcome.Should().Be(CloudOAuthOutcome.SessionMismatch);
        (await f.Connections.CompleteAsync(f.Provider.Kind, state, "code", null, null, CancellationToken.None))
            .Outcome.Should().Be(CloudOAuthOutcome.SessionMismatch);
        f.Provider.CodesExchanged.Should().BeEmpty();
        (await f.Db.CloudStorageConnections.CountAsync()).Should().Be(0);

        (await f.Connections.CompleteAsync(f.Provider.Kind, state, "code", null, f.UserId, CancellationToken.None))
            .Outcome.Should().Be(CloudOAuthOutcome.Connected, "the legitimate person's authorization survives the attempts");
    }

    [Fact]
    public async Task A_state_minted_for_one_provider_is_unknown_to_another()
    {
        (CloudFixture f, string state) = await StartedAsync();

        (await f.Connections.CompleteAsync(CloudStorageProviderKind.Dropbox, state, "code", null, f.UserId, CancellationToken.None))
            .Outcome.Should().NotBe(CloudOAuthOutcome.Connected);
        f.Provider.CodesExchanged.Should().BeEmpty();
    }

    [Fact]
    public async Task A_denied_consent_spends_the_state_and_returns_to_the_stored_path()
    {
        (CloudFixture f, string state) = await StartedAsync();

        CloudOAuthCompletion done = await f.Connections.CompleteAsync(f.Provider.Kind, state, null, "access_denied", f.UserId, CancellationToken.None);

        done.Outcome.Should().Be(CloudOAuthOutcome.Denied);
        done.ReturnPath.Should().Be("/app/projects/p1?tab=cloud");
        (await f.Db.CloudStorageOAuthStates.SingleAsync()).ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconnecting_the_same_account_updates_the_row_instead_of_adding_one()
    {
        (CloudFixture f, string first) = await StartedAsync();
        await f.Connections.CompleteAsync(f.Provider.Kind, first, "code-1", null, f.UserId, CancellationToken.None);
        CloudStorageConnection connection = await f.Db.CloudStorageConnections.SingleAsync();
        connection.LastError = "broken";
        await f.Db.SaveChangesAsync();

        f.Provider.TokensToIssue = new CloudTokenSet("second-access", null, f.Clock.UtcNow.AddHours(1), null);
        CloudOAuthStart again = await f.Connections.StartAsync(f.Provider.Kind, f.UserId, f.OrganizationId, null, CancellationToken.None);
        await f.Connections.CompleteAsync(f.Provider.Kind, StateOf(again.AuthorizeUrl!), "code-2", null, f.UserId, CancellationToken.None);

        CloudStorageConnection updated = await f.Db.CloudStorageConnections.SingleAsync();
        updated.Id.Should().Be(connection.Id);
        updated.LastError.Should().BeNull();
        f.Secrets.TryUnprotect(updated.ProtectedAccessToken, out string access).Should().BeTrue();
        access.Should().Be("second-access");
        f.Secrets.TryUnprotect(updated.ProtectedRefreshToken, out string refresh).Should().BeTrue();
        refresh.Should().Be("issued-refresh", "a re-consent that issues no refresh token keeps the stored one");
    }

    [Theory]
    [InlineData("/app/settings", "/app/settings")]
    [InlineData("/app", "/app")]
    [InlineData("/app/projects/0b5c3c1e-1111-2222-3333-444455556666?tab=cloud", "/app/projects/0b5c3c1e-1111-2222-3333-444455556666?tab=cloud")]
    [InlineData("https://evil.example/app/settings", null)]
    [InlineData("//evil.example/app", null)]
    [InlineData("/app//evil.example", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("/app/../admin", null)]
    [InlineData("/app/%2e%2e/admin", null)]
    [InlineData("/application", null)]
    [InlineData("/docs/intro", null)]
    [InlineData("/app/settings#frag", null)]
    [InlineData("/app/settings?x=1\n", "/app/settings?x=1")]
    [InlineData("/app/settings?x=<script>", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Return_paths_are_same_origin_dashboard_paths_or_nothing(string? raw, string? expected) =>
        CloudStorageConnectionService.NormalizeReturnPath(raw).Should().Be(expected);

    [Fact]
    public async Task A_token_far_from_expiry_is_used_as_is()
    {
        CloudFixture f = new();
        CloudStorageConnection connection = await f.AddConnectionAsync(expiresIn: TimeSpan.FromMinutes(30));

        (await f.Connections.GetAccessTokenAsync(connection, forceRefresh: false, CancellationToken.None)).Should().Be("access-1");
        f.Provider.RefreshTokensUsed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_near_expiry_is_refreshed_and_a_rotated_refresh_token_is_stored()
    {
        CloudFixture f = new();
        CloudStorageConnection connection = await f.AddConnectionAsync(expiresIn: TimeSpan.FromMinutes(1));
        f.Provider.TokensToRefresh = new CloudTokenSet("access-2", "refresh-2", f.Clock.UtcNow.AddHours(1), null);

        string token = await f.Connections.GetAccessTokenAsync(connection, forceRefresh: false, CancellationToken.None);

        token.Should().Be("access-2");
        f.Provider.RefreshTokensUsed.Should().Equal("refresh-1");
        await using var observer = f.NewContext();
        CloudStorageConnection stored = await observer.CloudStorageConnections.SingleAsync();
        f.Secrets.TryUnprotect(stored.ProtectedAccessToken, out string access).Should().BeTrue();
        access.Should().Be("access-2");
        f.Secrets.TryUnprotect(stored.ProtectedRefreshToken, out string refresh).Should().BeTrue();
        refresh.Should().Be("refresh-2");
        stored.AccessTokenExpiresAt.Should().Be(f.Clock.UtcNow.AddHours(1));
    }

    [Fact]
    public async Task A_provider_that_does_not_rotate_keeps_the_stored_refresh_token()
    {
        CloudFixture f = new();
        CloudStorageConnection connection = await f.AddConnectionAsync(expiresIn: TimeSpan.FromSeconds(30));
        f.Provider.TokensToRefresh = new CloudTokenSet("access-2", null, f.Clock.UtcNow.AddHours(1), null);

        await f.Connections.GetAccessTokenAsync(connection, forceRefresh: false, CancellationToken.None);

        f.Secrets.TryUnprotect(connection.ProtectedRefreshToken, out string refresh).Should().BeTrue();
        refresh.Should().Be("refresh-1");
    }

    [Fact]
    public async Task A_refused_refresh_sets_LastError_and_throws_unauthorized()
    {
        CloudFixture f = new();
        CloudStorageConnection connection = await f.AddConnectionAsync(expiresIn: TimeSpan.FromMinutes(-5));

        Func<Task> act = () => f.Connections.GetAccessTokenAsync(connection, forceRefresh: false, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);
        await using var observer = f.NewContext();
        (await observer.CloudStorageConnections.SingleAsync()).LastError.Should().Contain("Reconnect");
    }

    [Fact]
    public async Task A_transient_refresh_failure_does_not_mark_the_connection()
    {
        CloudFixture f = new();
        CloudStorageConnection connection = await f.AddConnectionAsync(expiresIn: TimeSpan.FromMinutes(-5));
        f.Provider.RefreshFailure = new CloudStorageApiException("503", CloudStorageErrorKind.ServerError);

        Func<Task> act = () => f.Connections.GetAccessTokenAsync(connection, forceRefresh: false, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.ServerError);
        connection.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_revokes_deletes_and_detaches_links_but_only_for_the_owner()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection connection = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection);

        (await f.Connections.DisconnectAsync(connection.Id, Guid.NewGuid(), f.OrganizationId, CancellationToken.None)).Should().BeFalse();
        (await f.Connections.DisconnectAsync(connection.Id, f.UserId, Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
        (await f.Db.CloudStorageConnections.CountAsync()).Should().Be(1);

        (await f.Connections.DisconnectAsync(connection.Id, f.UserId, f.OrganizationId, CancellationToken.None)).Should().BeTrue();

        f.Provider.Revoked.Should().ContainSingle().Which.AccessToken.Should().Be("access-1");
        (await f.Db.CloudStorageConnections.CountAsync()).Should().Be(0);
        (await f.Db.ProjectCloudLinks.SingleAsync(l => l.Id == link.Id)).ConnectionId.Should().BeNull("the link and its files stay");
    }
}
