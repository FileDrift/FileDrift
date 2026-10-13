// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FileDrift.Core.Cloud;
using FileDrift.Core.Configuration;
using FileDrift.Core.Interfaces;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// Exercises the upload path against a stubbed transport. Chunk alignment, Content-Range values, and
/// the fileSystemInfo write are precisely the things that fail obscurely (or silently wrongly) against
/// the real service, so they are worth pinning without needing a tenant.
/// </summary>
public class GraphFileTargetTests
{
    private const string UploadSessionUrl = "https://upload.example/session/abc";

    /// <summary>Records every request and replies with the minimum Graph would.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, long? From, long? To, long? Length, byte[] Body, bool HadAuth)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var range = request.Content?.Headers.ContentRange;

            Calls.Add((request.Method, request.RequestUri!.ToString(),
                range?.From, range?.To, range?.Length, body,
                request.Headers.Authorization is not null));

            // createUploadSession must hand back an upload URL.
            if (request.RequestUri!.ToString().Contains("createUploadSession", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        new JsonObject { ["uploadUrl"] = UploadSessionUrl }.ToJsonString(),
                        Encoding.UTF8, "application/json"),
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubTokens : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test-token");
        public Task<GraphAccount?> GetSignedInAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GraphAccount?>(new GraphAccount("tester@example.gov", "tenant", "env"));
    }

    private static (GraphFileTarget Target, RecordingHandler Handler) Build(string folderPath = "Base")
    {
        var handler = new RecordingHandler();
        var settings = new GraphSettings
        {
            ClientId = "11111111-2222-3333-4444-555555555555",
            TenantId = "66666666-7777-8888-9999-aaaaaaaaaaaa",
        };
        var client = new GraphClient(new StubTokens(), settings, new HttpClient(handler));
        var graphTarget = new GraphTarget("site-id", "Site", "drive-id", "Documents", folderPath, "https://x/y");
        return (new GraphFileTarget(client, graphTarget, settings), handler);
    }

    private static FileWriteRequest Request(string relativePath, byte[] content, Action<long>? onBytes = null) =>
        new(relativePath,
            _ => Task.FromResult<Stream>(new MemoryStream(content)),
            content.Length,
            new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            onBytes);

    [Fact]
    public async Task A_small_file_uses_a_single_put_and_replaces_on_conflict()
    {
        var (target, handler) = Build();
        var result = await target.WriteFileAsync(Request(@"folder\small.txt", Encoding.UTF8.GetBytes("hello")));

        Assert.Equal(5, result.BytesWritten);
        var put = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Put);
        Assert.Contains("Base/folder/small.txt", Uri.UnescapeDataString(put.Url));
        Assert.Contains("conflictBehavior=replace", put.Url);
        Assert.Equal("hello", Encoding.UTF8.GetString(put.Body));
        // No upload session for small content.
        Assert.DoesNotContain(handler.Calls, c => c.Url.Contains("createUploadSession"));
    }

    [Fact]
    public async Task Source_timestamps_are_written_back_as_filesysteminfo()
    {
        // THE critical behaviour: without this the destination records the UPLOAD time, and a
        // Standard-depth verify would then report every migrated file as timestamp-different.
        var (target, handler) = Build();
        await target.WriteFileAsync(Request("a.txt", Encoding.UTF8.GetBytes("x")));

        var patch = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Patch);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(patch.Body))!;
        var info = json["fileSystemInfo"]!;

        Assert.StartsWith("2024-03-04T05:06:07", info["lastModifiedDateTime"]!.GetValue<string>());
        Assert.StartsWith("2023-01-02T03:04:05", info["createdDateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_large_file_uses_a_session_with_aligned_chunks_and_correct_ranges()
    {
        // Two full chunks plus a remainder, so alignment and the final short chunk are both exercised.
        var size = GraphFileTarget.ChunkSize * 2 + 1234;
        var content = new byte[size];
        Random.Shared.NextBytes(content);

        var (target, handler) = Build();
        var result = await target.WriteFileAsync(Request("big.bin", content));

        Assert.Equal(size, result.BytesWritten);
        Assert.Contains(handler.Calls, c => c.Url.Contains("createUploadSession"));

        var chunks = handler.Calls.Where(c => c.Url == UploadSessionUrl).ToList();
        Assert.Equal(3, chunks.Count);

        // Every chunk but the last must be a multiple of 320 KiB -- Graph rejects a misaligned range.
        foreach (var chunk in chunks.Take(chunks.Count - 1))
            Assert.Equal(0, chunk.Body.Length % GraphFileTarget.ChunkAlignment);

        // Ranges must be contiguous, zero-based, inclusive, and declare the true total.
        long expectedFrom = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(expectedFrom, chunk.From);
            Assert.Equal(expectedFrom + chunk.Body.Length - 1, chunk.To);
            Assert.Equal(size, chunk.Length);
            expectedFrom += chunk.Body.Length;
        }
        Assert.Equal(size, expectedFrom);
    }

    [Fact]
    public void The_chunk_size_is_a_multiple_of_the_required_alignment()
    {
        // A constant that silently violates this would fail only at run time, against the real service.
        Assert.Equal(0, GraphFileTarget.ChunkSize % GraphFileTarget.ChunkAlignment);
        Assert.Equal(320 * 1024, GraphFileTarget.ChunkAlignment);
    }

    [Fact]
    public async Task Upload_session_chunks_do_not_carry_the_bearer_token()
    {
        // The session URL is pre-authorised and is not a Graph endpoint, so attaching our access token
        // would hand a credential to infrastructure that has no need of it.
        var content = new byte[GraphFileTarget.ChunkSize + 10];
        var (target, handler) = Build();
        await target.WriteFileAsync(Request("big.bin", content));

        Assert.All(handler.Calls.Where(c => c.Url == UploadSessionUrl), c => Assert.False(c.HadAuth));
        // ...while ordinary Graph calls do.
        Assert.All(handler.Calls.Where(c => c.Url.Contains("graph.microsoft.com")), c => Assert.True(c.HadAuth));
    }

    [Fact]
    public async Task An_illegal_name_is_sanitised_and_the_final_path_is_reported()
    {
        var (target, handler) = Build();
        var result = await target.WriteFileAsync(Request(@"sub\CON.txt", Encoding.UTF8.GetBytes("x")));

        // The caller needs the real landing place so the rename can be disclosed.
        Assert.Equal(@"sub\CON_.txt", result.FinalRelativePath);
        Assert.True(target.MayTransformNames);

        var put = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Put);
        Assert.Contains("Base/sub/CON_.txt", Uri.UnescapeDataString(put.Url));
    }

    [Fact]
    public async Task Progress_is_reported_and_reaches_the_full_size()
    {
        var content = new byte[GraphFileTarget.ChunkSize + 500];
        var reported = new List<long>();

        var (target, _) = Build();
        await target.WriteFileAsync(Request("big.bin", content, reported.Add));

        Assert.NotEmpty(reported);
        Assert.Equal(content.Length, reported[^1]);
        // Monotonic: a rate readout fed by a value that went backwards would misbehave.
        Assert.Equal(reported.OrderBy(x => x).ToList(), reported);
    }

    [Fact]
    public async Task Creating_a_directory_walks_each_level()
    {
        // Graph has no mkdir -p: creating a leaf whose parents are absent fails.
        var (target, handler) = Build();
        await target.CreateDirectoryAsync(@"one\two\three");

        var posts = handler.Calls.Where(c => c.Method == HttpMethod.Post).ToList();
        Assert.Equal(3, posts.Count);
        Assert.Collection(posts.Select(p => JsonNode.Parse(Encoding.UTF8.GetString(p.Body))!["name"]!.GetValue<string>()),
            n => Assert.Equal("one", n),
            n => Assert.Equal("two", n),
            n => Assert.Equal("three", n));
    }

    [Fact]
    public async Task Deleting_a_partial_never_throws()
    {
        // Runs on the cancellation path, where an exception would mask why we stopped.
        var (target, _) = Build();
        Assert.True(await target.TryDeletePartialAsync("whatever.txt"));
    }
}
