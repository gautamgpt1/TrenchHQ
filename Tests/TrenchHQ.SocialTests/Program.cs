using TrenchHQ.Helpers;
using TrenchHQ.Models;
using System.Net;
using System.Text;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--live")
{
    await SocialLiveValidation.RunAsync(args.Skip(1).ToArray());
    return;
}

var passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
SocialPost? Parse(string json, string source) { using var doc = JsonDocument.Parse(json); return SocialPostParser.ParseJson(doc.RootElement, source); }
Task Done() => Task.CompletedTask;

await Check("handle normalization, dedupe, URL validation and 500-account bound", () =>
{
    Assert(SocialFeedRules.TryHandles("@OpenAI,https://x.com/openai\n_sama", out var handles, out _) && handles.SequenceEqual(new[] { "_sama", "openai" }));
    foreach (var invalid in new[] { "https://x.com/i/lists/1", "https://x.com/a/status/1", "https://evil.test/alice", "a/b", "https://x.com/alice?token=secret", "https://x.com:999/alice", "home" })
        Assert(!SocialFeedRules.TryHandles(invalid, out _, out _), invalid);
    Assert(SocialFeedRules.TryHandles(string.Join(',', Enumerable.Range(1, 500).Select(i => "u" + i)), out _, out _));
    Assert(!SocialFeedRules.TryHandles(string.Join(',', Enumerable.Range(1, 501).Select(i => "u" + i)), out _, out _));
    return Done();
});

await Check("X API author expansion and edit ordering", () =>
{
    var post = Parse("""{"data":{"id":"101","author_id":"9","text":"Edited","edit_history_tweet_ids":["100","101"],"referenced_tweets":[{"id":"7","type":"replied_to"}]},"includes":{"users":[{"id":"9","username":"alice","name":"Alice"}]}}""", "xapi")!;
    Assert(post.Handle == "alice" && post.CanonicalId == "100" && post.Kind == "reply");
    var cache = new SocialPostCache();
    Assert(cache.Apply(post)); Assert(!cache.Apply(post with { Id = "100", Text = "Older but longer text" }));
    return Done();
});

await Check("malformed optional X fields do not discard an otherwise valid post", () =>
{
    var post = Parse("""{"data":{"id":"101","author_id":"9","text":"Valid","edit_history_tweet_ids":[7],"referenced_tweets":[null,7]},"includes":{"users":[{"id":"9","username":"alice"}]}}""", "xapi")
        ?? throw new Exception("Valid post was discarded");
    Assert(post.CanonicalId == null && post.Kind == "post");
    return Done();
});

await Check("bounded stream line reading across chunks and blank keepalives", async () =>
{
    using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("\n{\"a\":1}\r\ntail")));
    var lines = new List<string>();
    await foreach (var line in SocialFeedSources.LinesAsync(reader, CancellationToken.None)) lines.Add(line);
    Assert(lines.SequenceEqual(new[] { "", "{\"a\":1}", "tail" }));
    using var oversized = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', SocialPostParser.MaximumFrameBytes + 1))));
    try { await foreach (var _ in SocialFeedSources.LinesAsync(oversized, CancellationToken.None)) { } throw new Exception("Oversized line accepted"); }
    catch (SocialSourceException) { }
});

await Check("oversized official rule responses are rejected before JSON parsing", async () =>
{
    using var source = new SocialFeedSources(new FakeHttp(_ => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new string('x', SocialPostParser.MaximumFrameBytes + 1))
    }));
    try
    {
        await source.RunAsync(
            new() { Id = "xapi", Secret = "test", InstallationId = "test" },
            ["alice"],
            _ => { },
            _ => { },
            CancellationToken.None);
        throw new Exception("Oversized response accepted");
    }
    catch (SocialSourceException error)
    {
        Assert(error.Message.Contains("size limit", StringComparison.Ordinal));
    }
});

await Check("official stream mutates only owned rules and receives a normalized post", async () =>
{
    var requests = new List<string>();
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var source = new SocialFeedSources(new FakeHttp(request =>
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
        requests.Add(body);
        var content = request.RequestUri!.AbsolutePath.EndsWith("/rules")
            ? request.Method == HttpMethod.Get ? """{"data":[{"id":"foreign","tag":"someone-else","value":"from:bob"},{"id":"owned","tag":"trenchhq:test:bob","value":"from:bob"}]}""" : "{}"
            : """{"data":{"id":"101","text":"Hello","author_id":"9"},"includes":{"users":[{"id":"9","username":"alice"}]}}""" + "\n";
        return new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }));
    var posts = new List<SocialPost>();
    try { await source.RunAsync(new() { Id = "xapi", Secret = "test", InstallationId = "test" }, ["alice"], posts.Add, _ => { }, cancel.Token); }
    catch (SocialSourceException) { }
    Assert(posts.Count == 1);
    Assert(requests.Any(r => r.Contains("owned")) && requests.All(r => !r.Contains("foreign")));
    Assert(requests.Any(r => r.Contains("from:alice")));
});

await Check("renamed stream replaces only this installation's v1 tags without duplicating current rules", async () =>
{
    var writes = new List<string>();
    var v1Prefix = Encoding.ASCII.GetString(Convert.FromHexString("6E657875733A"));
    using var source = new SocialFeedSources(new FakeHttp(request =>
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        if (body != null) writes.Add(body);
        var rules = JsonSerializer.Serialize(new { data = new[] {
            new { id = "v1-owned", tag = v1Prefix + "test:alice", value = "from:alice" },
            new { id = "v1-foreign", tag = v1Prefix + "other:alice", value = "from:alice" },
            new { id = "current", tag = "trenchhq:test:bob", value = "from:bob" }
        } });
        return new(HttpStatusCode.OK) { Content = new StringContent(
            request.RequestUri!.AbsolutePath.EndsWith("/rules") && request.Method == HttpMethod.Get ? rules : "{}") };
    }));
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try { await source.RunAsync(new() { Id = "xapi", Secret = "test", InstallationId = "test" }, ["alice", "bob"], _ => { }, _ => { }, cancel.Token); }
    catch (SocialSourceException) { }
    Assert(writes.Count == 2);
    using var deletion = JsonDocument.Parse(writes[0]);
    Assert(deletion.RootElement.GetProperty("delete").GetProperty("ids").EnumerateArray()
        .Select(id => id.GetString()).SequenceEqual(new[] { "v1-owned" }));
    using var addition = JsonDocument.Parse(writes[1]);
    var added = addition.RootElement.GetProperty("add");
    Assert(added.GetArrayLength() == 1 && added[0].GetProperty("tag").GetString() == "trenchhq:test:alice");
});

await Check("expired cards do not reappear on identical polls and storage is bounded", () =>
{
    var cache = new SocialPostCache();
    var post = new SocialPost("123", "alice", "Alice", "hello", null, DateTimeOffset.UtcNow.AddMinutes(-16), "xapi");
    Assert(cache.Apply(post));
    Assert(cache.Snapshot().Length == 0);
    Assert(!cache.Apply(post with { ReceivedAt = DateTimeOffset.UtcNow }));
    for (var index = 1000; index < 1700; index++) cache.Apply(post with { Id = index.ToString(), ReceivedAt = DateTimeOffset.UtcNow });
    Assert(cache.Snapshot().Length == 500);
    return Done();
});

await Check("shared watch ownership deduplicates and leaves independent widgets intact", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "trenchhq-social-service-tests-" + Guid.NewGuid().ToString("N"));
    var service = new SocialFeedService(dir);
    await service.Initialization;
    var first = Guid.NewGuid(); var second = Guid.NewGuid();
    service.Watch(first, ["alice", "bob"]); service.Watch(second, ["alice"]);
    Assert(service.WatchedHandles().SequenceEqual(new[] { "alice", "bob" }));
    service.Watch(first, []); Assert(service.WatchedHandles().SequenceEqual(new[] { "alice" }));
    await service.StopAsync(); Assert(service.WatchedHandles().Length == 0);
});

await Check("source test verifies official stream permission without changing rules", async () =>
{
    var calls = 0;
    using var source = new SocialFeedSources(new FakeHttp(request =>
    {
        Assert(request.Method == HttpMethod.Get);
        calls++;
        return request.RequestUri!.AbsolutePath.EndsWith("/rules")
            ? new(HttpStatusCode.OK) { Content = new StringContent("{}") }
            : new(HttpStatusCode.Forbidden);
    }));
    try { await source.RunAsync(new() { Id = "xapi", Secret = "test" }, ["alice"], _ => { }, _ => { }, CancellationToken.None, true); throw new Exception("Accepted forbidden stream"); }
    catch (SocialSourceException e) { Assert(e.Fatal); }
    Assert(calls == 2);
});

await Check("only the official source remains and legacy sources cannot start", async () =>
{
    Assert(SocialFeedRules.Providers.Length == 1 && SocialFeedRules.Providers[0].Id == "xapi");
    var requests = 0;
    using var source = new SocialFeedSources(new FakeHttp(_ => { requests++; return new(HttpStatusCode.OK); }));
    foreach (var id in new[] { "twikit", "nitter", "xrss", "rsshub", "xactions", "desearch", "1322" })
    {
        var config = new SocialProviderConfiguration { Id = id, Secret = "test" };
        Assert(SocialFeedRules.Validate(config).Length > 0);
        try { await source.RunAsync(config, ["alice"], _ => { }, _ => { }, CancellationToken.None); throw new Exception("Retired source accepted"); }
        catch (SocialSourceException error) { Assert(error.Fatal); }
    }
    Assert(requests == 0);
});

await Check("official endpoint and bearer token are constrained", () =>
{
    Assert(SocialFeedRules.Validate(new() { Secret = "test" }) == "");
    foreach (var endpoint in new[] { "http://api.x.com", "https://evil.test", "https://api.x.com@evil.test", "https://api.x.com/?token=test" })
        Assert(SocialFeedRules.Validate(new() { Endpoint = endpoint, Secret = "test" }).Length > 0);
    foreach (var secret in new[] { "", "test\r\nInjected: value", "a b" })
        Assert(SocialFeedRules.Validate(new() { Secret = secret }).Length > 0);
    return Done();
});

await Check("removed active source pauses on migration without enabling paid X", () =>
{
    var next = SocialFeedRules.OfficialOnly(new()
    {
        ActiveProviderId = "twikit",
        Providers = [new() { Id = "twikit", Secret = "old-session" }, new() { Id = "xapi", Secret = "official", InstallationId = "retained" }]
    });
    Assert(next.ActiveProviderId == null && next.Providers.Length == 1);
    Assert(next.Providers[0].Secret == "official" && next.Providers[0].InstallationId == "retained");
    Assert(SocialFeedRules.OfficialOnly(new() { ActiveProviderId = "xapi", Providers = next.Providers }).ActiveProviderId == "xapi");
    return Done();
});

await Check("DPAPI roundtrip retains official token but drops removed credentials", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "trenchhq-social-tests-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "settings.dpapi");
    try
    {
        await SocialProviderStore.SaveAsync(path, new() { ActiveProviderId = "xapi", Providers =
            [new() { Secret = "test-secret" }, new() { Id = "nitter", Secret = "removed" }] });
        Assert(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("test-secret"));
        var read = await SocialProviderStore.LoadAsync(path);
        Assert(read.Providers.Length == 1 && read.Providers[0].Secret == "test-secret" && read.ActiveProviderId == "xapi");
    }
    finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(dir)) Directory.Delete(dir); }
});

await Check("rate limiting and redirects fail safely without leaking credentials", async () =>
{
    foreach (var code in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.Found, HttpStatusCode.Unauthorized })
    {
        using var source = new SocialFeedSources(new FakeHttp(_ =>
        {
            var response = new HttpResponseMessage(code);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        }));
        try { await source.RunAsync(new() { Secret = "do-not-print" }, ["alice"], _ => { }, _ => { }, CancellationToken.None); throw new Exception("HTTP failure accepted"); }
        catch (SocialSourceException error)
        {
            Assert(!error.Message.Contains("do-not-print"));
            Assert(code == HttpStatusCode.TooManyRequests ? error.RetryAfter == TimeSpan.FromSeconds(90) : error.Fatal);
        }
    }
});

await Check("cancellation terminates an idle official stream", async () =>
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
    using var reader = new StreamReader(new WaitingStream());
    try { await foreach (var _ in SocialFeedSources.LinesAsync(reader, cancel.Token)) { } throw new Exception("Idle stream did not cancel"); }
    catch (OperationCanceledException) { Assert(cancel.IsCancellationRequested); }
});

Console.WriteLine($"{passed} social checks passed.");

sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
}

sealed class WaitingStream : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
