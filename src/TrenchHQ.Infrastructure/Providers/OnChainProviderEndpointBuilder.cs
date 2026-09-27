using TrenchHQ.Core.Providers;
using System;

namespace TrenchHQ.Infrastructure.Providers
{
    internal static class OnChainProviderEndpointBuilder
    {
        internal static Uri Build(
            string endpoint,
            string? apiKey,
            OnChainEndpointAuthenticationMode authenticationMode)
        {
            var uri = new Uri(endpoint, UriKind.Absolute);
            if (authenticationMode == OnChainEndpointAuthenticationMode.None)
            {
                return uri;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("This provider requires an API key.", nameof(apiKey));
            }

            var credential = Uri.EscapeDataString(apiKey.Trim());
            var builder = new UriBuilder(uri);
            switch (authenticationMode)
            {
                case OnChainEndpointAuthenticationMode.ApiKeyPathSegment:
                    builder.Path = uri.AbsolutePath.TrimEnd('/') + "/" + credential;
                    break;
                case OnChainEndpointAuthenticationMode.ApiKeyQuery:
                    builder.Query = AppendQuery(builder.Query, "api-key=" + credential);
                    break;
                case OnChainEndpointAuthenticationMode.ApiKeyQueryUnderscore:
                    builder.Query = AppendQuery(builder.Query, "api_key=" + credential);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(authenticationMode));
            }
            return builder.Uri;
        }

        private static string AppendQuery(string existingQuery, string item)
        {
            var existing = existingQuery.TrimStart('?');
            return string.IsNullOrEmpty(existing) ? item : existing + "&" + item;
        }
    }
}
