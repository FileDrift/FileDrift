// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileDrift.Core.Configuration;
using FileDrift.Core.Interfaces;

namespace FileDrift.Core.Cloud;

/// <summary>
/// Writes files into a SharePoint Online / OneDrive document library through Microsoft Graph.
/// </summary>
/// <remarks>
/// <para><b>Two upload paths.</b> Content at or below <see cref="SimpleUploadLimit"/> goes in a single
/// PUT. Anything larger uses a resumable upload session, sending byte ranges whose length must be a
/// multiple of 320 KiB — a Graph requirement, not a tuning choice; a misaligned chunk is rejected.</para>
/// <para><b>Timestamps are set explicitly, and that is not optional.</b> A plain upload leaves
/// <c>lastModifiedDateTime</c> at the moment of upload, which would make a follow-up Standard-depth
/// verify report every single file as timestamp-different — turning a successful migration into
/// apparent total drift. The original times are sent in <c>fileSystemInfo</c> so the destination
/// carries the source's own timestamps.</para>
/// <para><b>Names are sanitised, and the change is reported.</b> SharePoint rejects names Windows
/// permits. The alternative — refusing the file — would push the operator into renaming content on the
/// source, altering the very data being attested. So the name is transformed deterministically and the
/// result returns where the file actually landed, for disclosure on the certificate.</para>
/// </remarks>
public sealed class GraphFileTarget : IFileTarget
{
    /// <summary>Graph's ceiling for a single-request upload. Larger content needs a session.</summary>
    public const long SimpleUploadLimit = 4L * 1024 * 1024;

    /// <summary>Graph requires every upload-session chunk except the last to be a multiple of this.</summary>
    public const int ChunkAlignment = 320 * 1024;

    /// <summary>Chunk size for session uploads: aligned, and large enough that a 5–10 GB migration is
    /// not dominated by per-request overhead.</summary>
    public const int ChunkSize = ChunkAlignment * 25; // 8 MiB

    private readonly GraphClient _client;
    private readonly GraphTarget _target;
    private readonly GraphSettings _settings;

    public GraphFileTarget(GraphClient client, GraphTarget target, GraphSettings settings)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Description =>
        _target.WebUrl ?? $"{_target.SiteName}/{_target.LibraryName}";

    /// <summary>True: SharePoint rejects names Windows allows, so callers must check the returned path.</summary>
    public bool MayTransformNames => true;

    public async Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var sanitized = SharePointNaming.Sanitize(relativePath);
        if (string.IsNullOrEmpty(sanitized)) return;

        // Create each level in turn. Graph has no mkdir -p, and creating a leaf whose parents are
        // absent fails, so walk down. Conflict is treated as success -- a folder that already exists
        // is the desired end state, and racing another writer should not fail a migration.
        var parts = sanitized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var walked = new StringBuilder();

        foreach (var part in parts)
        {
            var parent = walked.Length == 0 ? _target.FolderPath : $"{_target.FolderPath}/{walked}".Trim('/');
            await CreateChildFolderAsync(parent, part, cancellationToken).ConfigureAwait(false);

            if (walked.Length > 0) walked.Append('/');
            walked.Append(part);
        }
    }

    public async Task<FileWriteResult> WriteFileAsync(
        FileWriteRequest request, CancellationToken cancellationToken = default)
    {
        var sanitized = SharePointNaming.Sanitize(request.RelativePath);
        var destination = Combine(_target.FolderPath, sanitized);

        await using var source = await request.OpenSource(cancellationToken).ConfigureAwait(false);

        var written = request.SizeBytes <= SimpleUploadLimit
            ? await SimpleUploadAsync(destination, source, request, cancellationToken).ConfigureAwait(false)
            : await SessionUploadAsync(destination, source, request, cancellationToken).ConfigureAwait(false);

        return new FileWriteResult(written, sanitized);
    }

    public async Task<bool> TryDeletePartialAsync(
        string relativePath, CancellationToken cancellationToken = default)
    {
        // An aborted session upload leaves no item behind, so there is usually nothing to remove; an
        // aborted simple upload either landed whole or not at all. This exists for the case where a
        // write completed and the run was then cancelled, and must never throw -- it runs on the
        // cancellation path, where an exception would mask why we stopped.
        try
        {
            var destination = Combine(_target.FolderPath, SharePointNaming.Sanitize(relativePath));
            var url = $"drives/{_target.DriveId}/root:/{GraphSites.EncodePath(destination)}";
            await _client.SendAsync(HttpMethod.Delete, url, content: null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task CreateChildFolderAsync(
        string parentPath, string name, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrEmpty(parentPath)
            ? $"drives/{_target.DriveId}/root/children"
            : $"drives/{_target.DriveId}/root:/{GraphSites.EncodePath(parentPath)}:/children";

        var body = new JsonObject
        {
            ["name"] = name,
            ["folder"] = new JsonObject(),
            // Return the existing folder rather than erroring or creating "name 1".
            ["@microsoft.graph.conflictBehavior"] = "replace",
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        await _client.SendAsync(HttpMethod.Post, url, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> SimpleUploadAsync(
        string destination, Stream source, FileWriteRequest request, CancellationToken cancellationToken)
    {
        var url = $"drives/{_target.DriveId}/root:/{GraphSites.EncodePath(destination)}:/content" +
                  "?@microsoft.graph.conflictBehavior=replace";

        // Buffer so the request can be retried after a throttle: a network stream cannot be rewound,
        // and at 4 MiB or less this is a bounded cost.
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await _client.SendAsync(HttpMethod.Put, url, content, cancellationToken).ConfigureAwait(false);

        request.OnBytesWritten?.Invoke(bytes.Length);
        await SetFileSystemInfoAsync(destination, request, cancellationToken).ConfigureAwait(false);
        return bytes.Length;
    }

    private async Task<long> SessionUploadAsync(
        string destination, Stream source, FileWriteRequest request, CancellationToken cancellationToken)
    {
        var sessionUrl = await CreateUploadSessionAsync(destination, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[ChunkSize];
        long offset = 0;
        var total = request.SizeBytes;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fill the buffer completely before sending: a short read mid-file would produce a
            // misaligned chunk, which Graph rejects outright.
            var read = await ReadFullyAsync(source, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            var isFinal = read < buffer.Length || offset + read >= total;
            await PutChunkAsync(sessionUrl, buffer, read, offset, total, cancellationToken).ConfigureAwait(false);

            offset += read;
            request.OnBytesWritten?.Invoke(offset);

            if (isFinal) break;
        }

        // The session commits the item on the final chunk; timestamps are applied afterwards.
        await SetFileSystemInfoAsync(destination, request, cancellationToken).ConfigureAwait(false);
        return offset;
    }

    private async Task<string> CreateUploadSessionAsync(string destination, CancellationToken cancellationToken)
    {
        var url = $"drives/{_target.DriveId}/root:/{GraphSites.EncodePath(destination)}:/createUploadSession";
        var body = new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["@microsoft.graph.conflictBehavior"] = "replace",
            },
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(HttpMethod.Post, url, content, cancellationToken).ConfigureAwait(false);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        response.Dispose();

        return json?["uploadUrl"]?.GetValue<string>()
            ?? throw new GraphApiException(HttpStatusCode.BadGateway,
                $"Graph did not return an uploadUrl for \"{destination}\".");
    }

    private async Task PutChunkAsync(
        string sessionUrl, byte[] buffer, int length, long offset, long total, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(buffer, 0, length);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total);

        // The session URL is pre-authorised, so no bearer token is attached -- same reasoning as a
        // download URL: it is not a Graph endpoint and has no need of our credential.
        var response = await _client.SendAsync(
            HttpMethod.Put, sessionUrl, content, cancellationToken, authenticate: false).ConfigureAwait(false);
        response.Dispose();
    }

    /// <summary>Applies the source's timestamps to the uploaded item.
    /// <para>Without this the destination records the UPLOAD time, and a Standard-depth verify would
    /// then report every migrated file as timestamp-different. Best-effort: the content is already
    /// uploaded, and a Full-depth verify compares hashes regardless.</para></summary>
    private async Task SetFileSystemInfoAsync(
        string destination, FileWriteRequest request, CancellationToken cancellationToken)
    {
        var url = $"drives/{_target.DriveId}/root:/{GraphSites.EncodePath(destination)}";
        var body = new JsonObject
        {
            ["fileSystemInfo"] = new JsonObject
            {
                ["createdDateTime"] = Iso(request.CreatedTimeUtc),
                ["lastModifiedDateTime"] = Iso(request.LastWriteTimeUtc),
            },
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            var response = await _client.SendAsync(
                HttpMethod.Patch, url, content, cancellationToken).ConfigureAwait(false);
            response.Dispose();
        }
        catch (GraphApiException)
        {
            // Some libraries refuse metadata writes (retention, required columns). The bytes are
            // there, which is what matters most; a timestamp difference will show up honestly in the
            // next verify rather than being silently papered over.
        }
    }

    /// <summary>Reads until the buffer is full or the stream ends, so chunk lengths stay aligned.</summary>
    private static async Task<int> ReadFullyAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }
        return filled;
    }

    private static string Combine(string folderPath, string relativePath)
    {
        var tail = relativePath.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(folderPath) ? tail : $"{folderPath.Trim('/')}/{tail}";
    }

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
}
