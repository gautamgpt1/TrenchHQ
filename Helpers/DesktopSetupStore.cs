using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal static class DesktopSetupStore
    {
        internal const string SettingsFileName = "panel_settings.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true
        };

        internal static async Task<DesktopSetup?> LoadAsync(string folderPath)
        {
            var path = Path.Combine(folderPath, SettingsFileName);
            if (!File.Exists(path)) return null;
            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("Version", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var number)
                    || number != DesktopSetupRules.CurrentVersion) return null;

                var settings = JsonSerializer.Deserialize<DesktopSetup>(json, JsonOptions);
                return settings == null ? null : DesktopSetupRules.Normalize(settings);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        internal static async Task SaveAsync(string folderPath, DesktopSetup settings)
        {
            Directory.CreateDirectory(folderPath);
            var json = JsonSerializer.Serialize(DesktopSetupRules.Normalize(settings), JsonOptions);
            await WriteAtomicallyAsync(Path.Combine(folderPath, SettingsFileName), json).ConfigureAwait(false);
        }

        private static async Task WriteAtomicallyAsync(string path, string contents)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, contents).ConfigureAwait(false);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
