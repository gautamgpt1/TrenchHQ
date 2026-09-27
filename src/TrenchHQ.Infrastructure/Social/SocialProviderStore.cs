using TrenchHQ.Core.Social;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.Social
{
    internal static class SocialProviderStore
    {
        internal static async Task<SocialProviderSettings> LoadAsync(string path)
        {
            if (!File.Exists(path)) return new();
            var encrypted = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var settings = JsonSerializer.Deserialize<SocialProviderSettings>(clear);
                if (settings?.Version != 1) throw new InvalidDataException("Unsupported social provider settings.");
                return SocialFeedRules.OfficialOnly(settings);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }

        internal static async Task SaveAsync(string path, SocialProviderSettings settings)
        {
            var clear = JsonSerializer.SerializeToUtf8Bytes(SocialFeedRules.OfficialOnly(settings));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
    }
}
