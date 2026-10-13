// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileDrift.Core.Configuration;

namespace FileDrift.Core.Cloud;

/// <summary>
/// Minimal Microsoft Graph HTTP client — just the verbs FileDrift needs, over <see cref="HttpClient"/>.
/// <para>Deliberately not the generated Graph SDK: FileDrift uses a handful of endpoints, the SDK pulls
/// in Kiota plus generated models for a self-contained executable already near 137 MB, and throttling
/// and paging are things this tool wants to control explicitly rather than inherit.</para>
/// </summary>
public sealed class GraphClient : IDisposable
{
    private readonly IGraphTokenProvider _tokens;
    private readonly GraphSettings _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>The token provider backing this client, so callers can report which identity the
    /// requests are made as.</summary>
    public IGraphTokenProvider Tokens => _tokens;

    public GraphClient(IGraphTokenProvider tokens, GraphSettings settings, HttpClient? http = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    /// <summary>GETs a Graph endpoint and returns the parsed JSON body. <paramref name="url"/> may be
    /// relative (combined with the configured Graph base) or absolute — an <c>@odata.nextLink</c> comes
    /// back absolute and is passed straight through.</summary>
    public async Task<JsonNode> GetJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Resolve(url)),
            authenticate: true,
            cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(json)
               ?? throw new GraphApiException(response.StatusCode, "Graph returned an empty JSON body.");
    }

    /// <summary>Downloads content from a pre-authenticated URL (Graph's
    /// <c>@microsoft.graph.downloadUrl</c>).
    /// <para>The bearer token is deliberately NOT sent: that URL carries its own short-lived
    /// authorization and points at storage/CDN infrastructure rather than Graph, so attaching our
    /// access token would hand a credential to an endpoint that has no need of it.</para></summary>
    public async Task<Stream> DownloadAsync(string preAuthenticatedUrl, CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, preAuthenticatedUrl),
            authenticate: false,
            cancellationToken).ConfigureAwait(false);

        // The caller owns the stream; it must outlive the response, so don't dispose the response here.
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    private string Resolve(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{_settings.EffectiveGraphBaseUrl}/{url.TrimStart('/')}";

    /// <summary>Sends a request, retrying on throttling and transient failures.
    /// <para>Graph throttles aggressively on large libraries, so 429 is an expected operating condition,
    /// not an exception. <c>Retry-After</c> is honoured when present — guessing a backoff when the
    /// service has told you exactly how long to wait is how you get throttled harder.</para>
    /// <paramref name="requestFactory"/> is a factory rather than a request because a
    /// <see cref="HttpRequestMessage"/> cannot be resent.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        bool authenticate,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(0, _settings.MaxRetries) + 1;

        for (var attempt = 1; ; attempt++)
        {
            var request = requestFactory();
            if (authenticate)
            {
                var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                request.Dispose();
                await Task.Delay(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                continue;
            }
            finally
            {
                request.Dispose();
            }

            if (response.IsSuccessStatusCode) return response;

            if (IsRetryable(response.StatusCode) && attempt < attempts)
            {
                var delay = Backoff(attempt, response.Headers.RetryAfter);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Read a bounded slice of the body for diagnostics; Graph error bodies are small, but an
            // HTML error page from an intermediary is not.
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Length > 600) body = body[..600] + "…";
            }
            catch { body = "(response body unavailable)"; }

            var status = response.StatusCode;
            response.Dispose();
            throw new GraphApiException(status, Describe(status, body));
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests ||
        status == HttpStatusCode.RequestTimeout ||
        status == HttpStatusCode.InternalServerError ||
        status == HttpStatusCode.BadGateway ||
        status == HttpStatusCode.ServiceUnavailable ||
        status == HttpStatusCode.GatewayTimeout;

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is not null)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero) return Cap(delta);
            if (retryAfter.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) return Cap(wait);
            }
        }
        // Exponential with a floor, only when the service gave no guidance.
        return Cap(TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 6))));
    }

    private static TimeSpan Cap(TimeSpan value) =>
        value > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : value;

    private static string Describe(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized =>
            "Graph rejected the access token (401). The sign-in may have expired or been revoked — " +
            $"run \"FileDrift-CLI graph login\" again. Response: {body}",
        HttpStatusCode.Forbidden =>
            "Graph denied access (403). The signed-in account may lack permission on this item, or the " +
            $"required delegated scope was not consented. Response: {body}",
        HttpStatusCode.NotFound =>
            $"Graph could not find the requested item (404). Check the site, library, and path. Response: {body}",
        _ => $"Graph request failed ({(int)status} {status}). Response: {body}",
    };

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>A Graph request failed with a non-success status after any retries.</summary>
public sealed class GraphApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public GraphApiException(HttpStatusCode statusCode, string message) : base(message) =>
        StatusCode = statusCode;
}

/// <summary>Helpers for reading the bits of a Graph <c>driveItem</c> FileDrift cares about.</summary>
public static class DriveItemJson
{
    /// <summary>Reads a hash from <c>file.hashes</c>. SharePoint Online and OneDrive for Business
    /// report <c>quickXorHash</c>; some endpoints and on-premises SharePoint report SHA-1 or SHA-256
    /// instead, so which one is present has to be discovered rather than assumed.</summary>
    public static (string? QuickXor, string? Sha1, string? Sha256) Hashes(JsonNode? item)
    {
        var hashes = item?["file"]?["hashes"];
        return (
            hashes?["quickXorHash"]?.GetValue<string>(),
            hashes?["sha1Hash"]?.GetValue<string>(),
            hashes?["sha256Hash"]?.GetValue<string>());
    }

    public static bool IsFolder(JsonNode? item) => item?["folder"] is not null;

    public static string? Name(JsonNode? item) => item?["name"]?.GetValue<string>();

    public static string? DownloadUrl(JsonNode? item) =>
        item?["@microsoft.graph.downloadUrl"]?.GetValue<string>();

    public static long Size(JsonNode? item) =>
        item?["size"] is { } size && size.GetValueKind() == JsonValueKind.Number ? size.GetValue<long>() : 0L;

    /// <summary>Creation time from <c>fileSystemInfo</c>, preferring the original file time an
    /// uploader set over the item's own createdDateTime, for the same reason as
    /// <see cref="LastModifiedUtc"/>.</summary>
    public static DateTime? CreatedUtc(JsonNode? item)
    {
        var raw = item?["fileSystemInfo"]?["createdDateTime"]?.GetValue<string>()
                  ?? item?["createdDateTime"]?.GetValue<string>();
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Last-modified time from <c>fileSystemInfo</c> in preference to the item's own
    /// <c>lastModifiedDateTime</c>: the former carries the ORIGINAL file time when an uploader sets it,
    /// while the latter reflects when the item changed in SharePoint.</summary>
    public static DateTime? LastModifiedUtc(JsonNode? item)
    {
        var raw = item?["fileSystemInfo"]?["lastModifiedDateTime"]?.GetValue<string>()
                  ?? item?["lastModifiedDateTime"]?.GetValue<string>();
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
