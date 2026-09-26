using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace TrenchHQ.Helpers
{
    internal static class SocialPostParser
    {
        internal const int MaximumFrameBytes = 2 * 1024 * 1024;

        internal static string String(JsonElement root, params string[] path)
        {
            foreach (var part in path)
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(part, out root)) return "";
            return root.ValueKind == JsonValueKind.String ? root.GetString() ?? ""
                : root.ValueKind == JsonValueKind.Number ? root.GetRawText() : "";
        }

        internal static JsonElement Child(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var child) ? child : default;

        internal static SocialPost? ParseJson(JsonElement root, string provider, string fallbackHandle = "")
        {
            var type = String(root, "type");
            var tweet = Child(root, "tweet");
            if (provider != "xapi") return null;
            if (tweet.ValueKind != JsonValueKind.Object) tweet = Child(root, "data");
            if (tweet.ValueKind != JsonValueKind.Object) tweet = root;
            var id = First(String(tweet, "id"), String(tweet, "id_str"));
            var handle = First(String(tweet, "author", "handle"), String(tweet, "user", "screen_name"), String(tweet, "user", "username"), String(tweet, "handle"), String(tweet, "username"), fallbackHandle).TrimStart('@');
            var author = First(String(tweet, "author", "profile", "name"), String(tweet, "author", "name"), String(tweet, "user", "name"), String(tweet, "author"), handle);
            var text = First(String(tweet, "body", "text"), String(tweet, "full_text"), String(tweet, "text"), String(tweet, "content"));
            if (provider == "xapi")
            {
                var users = Child(Child(root, "includes"), "users");
                if (users.ValueKind == JsonValueKind.Array)
                    foreach (var user in users.EnumerateArray())
                        if (String(user, "id") == String(tweet, "author_id"))
                        { handle = String(user, "username"); author = First(String(user, "name"), handle); break; }
            }
            var kind = First(String(tweet, "kind"), String(tweet, "type")).ToLowerInvariant() switch
            { "retweet" or "repost" => "repost", "reply" => "reply", "quote" => "quote", _ => "post" };
            var references = Child(tweet, "referenced_tweets");
            if (references.ValueKind == JsonValueKind.Array)
                foreach (var reference in references.EnumerateArray())
                    kind = String(reference, "type") switch { "retweeted" => "repost", "quoted" => "quote", "replied_to" => "reply", _ => kind };
            var history = Child(tweet, "edit_history_tweet_ids");
            string? canonical = null;
            if (history.ValueKind == JsonValueKind.Array && history.GetArrayLength() > 0)
            {
                var firstId = history[0];
                if (firstId.ValueKind == JsonValueKind.String)
                {
                    var candidate = firstId.GetString();
                    if (candidate is { Length: >= 1 and <= 24 } && candidate.All(char.IsAsciiDigit))
                    {
                        canonical = candidate;
                    }
                }
            }
            return Create(id, handle, author, text, First(String(tweet, "created_at"), String(tweet, "timestamp")), provider, kind,
                type == "tweet.deleted", canonical);
        }

        internal static SocialPost? Create(string id, string handle, string author, string text, string time,
            string provider, string kind = "post", bool deleted = false, string? canonical = null)
        {
            if (id.Length is < 1 or > 24 || !id.All(char.IsAsciiDigit) || !SocialFeedRules.ValidHandle(handle)
                || text.Length > 100000 || (!deleted && text.Length == 0)) return null;
            return new SocialPost(id, handle.ToLowerInvariant(), author, text, ParseTime(time), DateTimeOffset.UtcNow, provider, kind, deleted, canonical);
        }

        internal static DateTimeOffset? ParseTime(string value)
        {
            if (long.TryParse(value, out var numeric))
            {
                try { return numeric > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(numeric) : DateTimeOffset.FromUnixTimeSeconds(numeric); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
        }

        private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }

    internal sealed class SocialPostCache
    {
        private readonly Dictionary<string, SocialPost> _posts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _deleted = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Id, string Digest, int Length)> _seen = new(StringComparer.Ordinal);

        internal bool Apply(SocialPost post)
        {
            Expire();
            var key = post.CanonicalId ?? post.Id;
            if (post.Deleted)
            {
                var existingKey = _posts.FirstOrDefault(p => p.Value.Id == post.Id).Key;
                if (existingKey != null) key = existingKey;
                _deleted[key] = DateTimeOffset.UtcNow;
                _deleted[post.Id] = DateTimeOffset.UtcNow;
                if (_deleted.Count > 5000) _deleted.Remove(_deleted.MinBy(p => p.Value).Key);
                return _posts.Remove(key);
            }
            if (_deleted.ContainsKey(key) || _deleted.ContainsKey(post.Id)) return false;
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(post.Text)));
            if (_seen.TryGetValue(key, out var existing))
            {
                // Older edit revisions and less complete vendor stages cannot undo a richer card.
                if (ulong.TryParse(post.Id, out var next) && ulong.TryParse(existing.Id, out var old) && next < old) return false;
                if (existing.Id == post.Id && (existing.Digest == digest || existing.Length > post.Text.Length)) return false;
            }
            _seen[key] = (post.Id, digest, post.Text.Length);
            if (_seen.Count > 5000) _seen.Remove(_seen.Keys.First());
            _posts[key] = post;
            if (_posts.Count > 500) _posts.Remove(_posts.MinBy(p => p.Value.ReceivedAt).Key);
            return true;
        }

        internal SocialPost[] Snapshot() { Expire(); return _posts.Values.OrderByDescending(p => p.CreatedAt ?? p.ReceivedAt).ToArray(); }
        internal void Clear() { _posts.Clear(); _deleted.Clear(); _seen.Clear(); }
        private void Expire()
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
            foreach (var key in _posts.Where(p => p.Value.ReceivedAt < cutoff).Select(p => p.Key).ToArray()) _posts.Remove(key);
            foreach (var key in _deleted.Where(p => p.Value < DateTimeOffset.UtcNow.AddHours(-24)).Select(p => p.Key).ToArray()) _deleted.Remove(key);
        }
    }
}
