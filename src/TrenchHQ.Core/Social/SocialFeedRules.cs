using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Core.Social
{
    internal static class SocialFeedRules
    {
        internal static readonly SocialProviderPreset[] Providers =
        [
            new("xapi", "Official X API · Filtered Stream", "https://api.x.com", "Enter your X developer app bearer token. This is a paid, persistent HTTP stream, not polling. Standard delivery is within seconds, not guaranteed subsecond. TrenchHQ manages only its own tagged account rules. Test may incur API charges; use a dedicated X project. Powerstream Enterprise access is not included.")
        ];

        internal static SocialProviderPreset Preset(string id) => Providers.First(p => p.Id == id);

        internal static bool TryHandles(string? input, out string[] handles, out string error)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in (input ?? "").Split([' ', '\r', '\n', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                var value = token.Trim().TrimStart('@');
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                {
                    if (uri.Scheme != "https" || uri.Host is not ("x.com" or "www.x.com" or "twitter.com" or "www.twitter.com")
                        || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
                    { handles = []; error = "Use handles or HTTPS X profile URLs."; return false; }
                    value = uri.AbsolutePath.Trim('/');
                }
                if (!ValidHandle(value))
                { handles = []; error = "Each account must be a handle (1–15 letters, digits or underscores), not a Post or List URL."; return false; }
                result.Add(value.ToLowerInvariant());
                if (result.Count > 500) { handles = []; error = "Use at most 500 accounts per tracker."; return false; }
            }
            handles = result.Order(StringComparer.Ordinal).ToArray();
            error = handles.Length == 0 ? "Add at least one account." : "";
            return handles.Length > 0;
        }

        internal static bool ValidHandle(string value) => value.Length is >= 1 and <= 15
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            && !new[] { "home", "i", "search", "explore", "settings", "messages", "intent", "compose", "notifications" }.Contains(value.ToLowerInvariant());

        internal static string Validate(SocialProviderConfiguration config)
        {
            if (config.Id != "xapi") return "This X source has been removed. Configure the official X API or create a Website widget.";
            if (config.Endpoint != "https://api.x.com") return "The official X API uses https://api.x.com.";
            if (string.IsNullOrWhiteSpace(config.Secret)) return "Enter your X developer app bearer token.";
            if (config.Secret.Length > 32768 || config.Secret.Any(char.IsWhiteSpace)) return "The bearer token must be a single token without whitespace.";
            if (string.IsNullOrWhiteSpace(config.InstallationId)) return "Missing installation identity.";
            return "";
        }

        internal static SocialProviderSettings OfficialOnly(SocialProviderSettings settings) => new()
        {
            ActiveProviderId = settings.ActiveProviderId == "xapi" ? "xapi" : null,
            Providers = (settings.Providers ?? []).Where(p => p != null && p.Id == "xapi").Take(1).ToArray()
        };

    }
}
