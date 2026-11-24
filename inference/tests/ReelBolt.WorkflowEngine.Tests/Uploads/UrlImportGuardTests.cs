#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The pieces of <see cref="GeneratedClipUrlGuard"/> the user-URL import builds on: the handler that
/// never follows a redirect by itself (so every hop is re-validated by the importer), never goes through
/// an ambient proxy (the connect-time IP check must see the real destination) and never sends cookies;
/// the typed rebinding refusal; and the public streaming cap.
/// </summary>
public class UrlImportGuardTests
{
    [Fact]
    public void The_user_url_handler_follows_no_redirect_uses_no_proxy_and_sends_no_cookies()
    {
        using SocketsHttpHandler handler = GeneratedClipUrlGuard.CreateUserUrlHandler();

        handler.AllowAutoRedirect.Should().BeFalse("each redirect target is re-validated by the importer");
        handler.UseProxy.Should().BeFalse("a proxy would make the connect-time check validate the proxy, not the target");
        handler.UseCookies.Should().BeFalse();
        handler.ConnectCallback.Should().NotBeNull("the dialled IP is checked on every connection");
        handler.AutomaticDecompression.Should().Be(DecompressionMethods.None, "the byte cap counts what is stored, not a compressed stream");
    }

    [Fact]
    public void A_private_resolution_is_a_typed_refusal_that_existing_callers_still_catch_as_HttpRequestException()
    {
        Action act = () => GeneratedClipUrlGuard.RequirePublic([IPAddress.Parse("10.1.2.3")]);

        act.Should().Throw<NonPublicAddressException>()
            .Which.Should().BeAssignableTo<HttpRequestException>();
    }

    [Fact]
    public async Task The_limited_stream_is_public_and_throws_a_typed_error_past_its_limit()
    {
        using MemoryStream inner = new(new byte[10]);
        using LimitedReadStream limited = new(inner, 4, new MemoryStream());
        byte[] buffer = new byte[10];

        Func<Task> act = async () => { while (await limited.ReadAsync(buffer) > 0) { } };

        (await act.Should().ThrowAsync<StreamLimitExceededException>())
            .Which.Should().BeAssignableTo<InvalidOperationException>("FalAi/MiniMax/ComfyUI callers catch InvalidOperationException");
    }
}
