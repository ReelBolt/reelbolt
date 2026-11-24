using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace ReelBolt.Inference.Api.Controllers.Filters;

/// <summary>
/// Conditional GET for polled JSON reads: the body is serialized once with the MVC JSON options, a weak ETag is derived from
/// those bytes only, and a matching <c>If-None-Match</c> gets a 304 with no body. Responses are <c>private, no-cache</c>, so a
/// browser revalidates every time but never re-downloads an unchanged body. Only a 200 <see cref="ObjectResult"/> from a GET is
/// touched; everything else (errors, files, streams, other verbs) passes through unchanged.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConditionalGetAttribute : Attribute, IAsyncResultFilter
{
    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        HttpContext http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method)
            || context.Result is not ObjectResult { Value: not null } result
            || (result.StatusCode ?? StatusCodes.Status200OK) != StatusCodes.Status200OK
            || result.Value is Stream or IAsyncEnumerable<object>)
        {
            await next();
            return;
        }

        JsonSerializerOptions options = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(result.Value, result.DeclaredType ?? result.Value.GetType(), options);
        string etag = ComputeETag(body);

        IHeaderDictionary headers = http.Response.Headers;
        headers[HeaderNames.ETag] = etag;
        // An action that chose its own freshness window (assistant activity's two-second one) keeps it; the
        // validator still saves the body once that window is over.
        if (StringValues.IsNullOrEmpty(headers[HeaderNames.CacheControl])) headers[HeaderNames.CacheControl] = "private, no-cache";
        headers.Append(HeaderNames.Vary, new StringValues(["Cookie", "Authorization"]));

        if (Matches(http.Request.Headers[HeaderNames.IfNoneMatch].ToString(), etag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.ContentLength = body.Length;
        await http.Response.Body.WriteAsync(body, http.RequestAborted);
    }

    /// <summary>Weak ETag from the body bytes alone: the first 16 bytes of SHA-256, base64url.</summary>
    internal static string ComputeETag(ReadOnlySpan<byte> body)
    {
        byte[] hash = SHA256.HashData(body);
        string token = Convert.ToBase64String(hash, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"W/\"{token}\"";
    }

    /// <summary>If-None-Match with weak comparison: <c>*</c>, or any listed tag equal to <paramref name="etag"/> ignoring the W/ prefix.</summary>
    internal static bool Matches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        string opaque = StripWeak(etag);
        foreach (string part in ifNoneMatch.Split(','))
        {
            string candidate = part.Trim();
            if (candidate == "*" || StripWeak(candidate) == opaque) return true;
        }
        return false;
    }

    private static string StripWeak(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
