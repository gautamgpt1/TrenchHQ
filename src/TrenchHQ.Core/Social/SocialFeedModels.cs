using System;

namespace TrenchHQ.Core.Social
{
    internal sealed record SocialProviderPreset(string Id, string Name, string Endpoint, string Instructions);

    internal sealed class SocialProviderConfiguration
    {
        public string Id { get; set; } = "xapi";
        public string Endpoint { get; set; } = "https://api.x.com";
        public string Secret { get; set; } = string.Empty;
        public string InstallationId { get; set; } = Guid.NewGuid().ToString("N");
    }

    internal sealed class SocialProviderSettings
    {
        public int Version { get; set; } = 1;
        public string? ActiveProviderId { get; set; }
        public SocialProviderConfiguration[] Providers { get; set; } = [];
    }

    internal sealed record SocialPost(
        string Id, string Handle, string Author, string Text, DateTimeOffset? CreatedAt,
        DateTimeOffset ReceivedAt, string ProviderId, string Kind = "post", bool Deleted = false,
        string? CanonicalId = null)
    {
        public string Heading => $"{Author} (@{Handle})";
        public string Detail => $"{(CreatedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "Time unavailable")} · {ProviderId} · {Kind}";
        public Uri Url => new($"https://x.com/{Handle}/status/{Id}");
    }
}
