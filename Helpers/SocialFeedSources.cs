using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static TrenchHQ.Helpers.SocialPostParser;

namespace TrenchHQ.Helpers
{
    internal sealed class SocialSourceException(string message, bool fatal = false, TimeSpan? retryAfter = null) : Exception(message)
    {
        internal bool Fatal { get; } = fatal;
        internal TimeSpan? RetryAfter { get; } = retryAfter;
    }

    internal sealed class SocialFeedSources : IDisposable
    {
        private readonly HttpClient _http;
        internal SocialFeedSources(HttpMessageHandler? handler = null)
        {
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
            { Timeout = Timeout.InfiniteTimeSpan };
        }
        public void Dispose() => _http.Dispose();

        internal Task RunAsync(SocialProviderConfiguration config, string[] handles, Action<SocialPost> publish,
            Action<string> state, CancellationToken ct, bool probe = false)
        {
            var error = SocialFeedRules.Validate(config);
            if (error.Length > 0) throw new SocialSourceException(error, true);
            return RunXApiAsync(config, handles, publish, state, ct, probe);
        }

        private async Task RunXApiAsync(SocialProviderConfiguration config, string[] handles, Action<SocialPost> publish,
            Action<string> state, CancellationToken ct, bool probe)
        {
            var rulesUri = new Uri("https://api.x.com/2/tweets/search/stream/rules");
            using var rulesRequest = Request(config, rulesUri);
            using var json = JsonDocument.Parse(await FetchAsync(rulesRequest, ct));
            if (probe)
            {
                using var probeRequest = Request(config, new Uri("https://api.x.com/2/tweets/search/stream"));
                using var probeResponse = await OpenAsync(probeRequest, ct);
                state("X API authorized · rule read and stream connection succeeded");
                return;
            }
            var prefix = "trenchhq:" + config.InstallationId + ":";
            // Recognize this installation's v1 tags so a rename never leaves duplicate paid rules.
            var v1Prefix = Encoding.ASCII.GetString(Convert.FromHexString("6E657875733A")) + config.InstallationId + ":";
            var data = Child(json.RootElement, "data");
            var rules = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [];
            var owned = rules.Where(r => String(r, "tag").StartsWith(prefix, StringComparison.Ordinal)
                || String(r, "tag").StartsWith(v1Prefix, StringComparison.Ordinal)).ToArray();
            var wanted = handles.Select(h => "from:" + h).ToHashSet(StringComparer.Ordinal);
            var remove = owned.Where(r => !wanted.Contains(String(r, "value"))
                || String(r, "tag").StartsWith(v1Prefix, StringComparison.Ordinal)).Select(r => String(r, "id")).ToArray();
            // Only rules tagged by this installation are eligible for deletion.
            if (remove.Length > 0)
            {
                using var request = Request(config, rulesUri, new { delete = new { ids = remove } });
                await FetchAsync(request, ct);
            }
            var present = owned.Where(r => String(r, "tag").StartsWith(prefix, StringComparison.Ordinal))
                .Select(r => String(r, "value")).ToHashSet(StringComparer.Ordinal);
            foreach (var batch in handles.Where(h => !present.Contains("from:" + h)).Chunk(100))
            {
                using var request = Request(config, rulesUri, new { add = batch.Select(h => new { value = "from:" + h, tag = prefix + h }) });
                using var result = JsonDocument.Parse(await FetchAsync(request, ct));
                if (Child(result.RootElement, "errors").ValueKind == JsonValueKind.Array)
                    throw new SocialSourceException("X could not activate every watch rule. Check project capacity and access in API settings.", true);
            }
            using var streamRequest = Request(config, new Uri("https://api.x.com/2/tweets/search/stream?tweet.fields=created_at,author_id,referenced_tweets,edit_history_tweet_ids&expansions=author_id&user.fields=name,username"));
            using var response = await OpenAsync(streamRequest, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            state("Connected · X API stream · metered Post reads · reconnect gaps possible");
            await foreach (var line in LinesAsync(new StreamReader(stream), ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var frame = JsonDocument.Parse(line);
                if (Child(frame.RootElement, "errors").ValueKind == JsonValueKind.Array) throw new SocialSourceException("X reported a stream error; reconnecting with a possible coverage gap.");
                var post = ParseJson(frame.RootElement, "xapi");
                if (post != null) publish(post);
            }
            throw new SocialSourceException("X stream ended; posts during the gap may be missing.");
        }

        internal static async IAsyncEnumerable<string> LinesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken ct, int idleSeconds = 70)
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
                var count = await reader.ReadAsync(buffer.AsMemory(), idle.Token);
                if (count == 0) { if (line.Length > 0) yield return line.ToString(); yield break; }
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n') { yield return line.ToString().TrimEnd('\r'); line.Clear(); }
                    else { line.Append(buffer[index]); if (line.Length > MaximumFrameBytes) throw new SocialSourceException("Stream line exceeded the size limit."); }
                }
            }
        }

        private static HttpRequestMessage Request(SocialProviderConfiguration config, Uri uri, object? body = null)
        {
            var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (config.Secret.Length > 0)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.Secret);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return request;
        }

        private async Task<HttpResponseMessage> OpenAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.IsSuccessStatusCode) return response;
            var code = (int)response.StatusCode;
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            response.Dispose();
            if (code == 410) throw new SocialSourceException("This source endpoint is gone (HTTP 410). Check official X API availability.", true);
            if (code is >= 300 and < 400) throw new SocialSourceException($"This endpoint redirects (HTTP {code}). Configure the final service URL; TrenchHQ does not forward credentials through redirects.", true);
            throw new SocialSourceException($"Source returned HTTP {code}. " + (code == 429 ? "Rate limited; waiting before retry." : "Check the endpoint, credentials and source access."),
                code is 400 or 401 or 402 or 403 or 404, code == 429 ? retry ?? TimeSpan.FromMinutes(15) : null);
        }

        private async Task<string> FetchAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await OpenAsync(request, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + count > MaximumFrameBytes) throw new SocialSourceException("Source response exceeded the size limit.");
                output.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
