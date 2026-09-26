using TrenchHQ.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed record SavedWidgetCatalogLoadResult(
        SavedWidgetCatalog? Catalog,
        int SourceVersion,
        bool RequiresSave,
        bool WasRejected,
        string? BackupFileName);

    internal static class SavedWidgetCatalogStore
    {
        internal const string CatalogFileName = "widget_catalog.json";
        internal const string LastValidFileName = "widget_catalog.last-valid.json";
        internal const string RejectedFileName = "widget_catalog.rejected.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true
        };
        private static readonly SemaphoreSlim SaveLock = new(1, 1);

        internal static async Task<SavedWidgetCatalogLoadResult> LoadAsync(string folderPath)
        {
            var path = Path.Combine(folderPath, CatalogFileName);
            if (!File.Exists(path))
            {
                return new SavedWidgetCatalogLoadResult(null, 0, false, false, null);
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            catch
            {
                return new SavedWidgetCatalogLoadResult(null, 0, false, true, null);
            }

            if (!TryDeserializeSupported(json, out var catalog, out var sourceVersion))
            {
                var backedUp = await TryWriteBackupAsync(folderPath, RejectedFileName, json).ConfigureAwait(false);
                return new SavedWidgetCatalogLoadResult(
                    null,
                    sourceVersion,
                    false,
                    true,
                    backedUp ? RejectedFileName : null);
            }

            var normalized = SavedWidgetCatalogRules.Normalize(catalog!);
            return new SavedWidgetCatalogLoadResult(
                normalized.Catalog,
                sourceVersion,
                sourceVersion != SavedWidgetCatalogRules.CurrentVersion || normalized.WasChanged,
                false,
                null);
        }

        internal static async Task SaveAsync(string folderPath, SavedWidgetCatalog catalog)
        {
            await SaveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(folderPath);
                var path = Path.Combine(folderPath, CatalogFileName);
                if (File.Exists(path))
                {
                    try
                    {
                        var existingJson = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                        if (TryDeserializeSupported(existingJson, out _, out _))
                        {
                            await WriteAtomicallyAsync(
                                Path.Combine(folderPath, LastValidFileName),
                                existingJson).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                    }
                }

                var normalized = SavedWidgetCatalogRules.Normalize(catalog).Catalog;
                var json = JsonSerializer.Serialize(normalized, JsonOptions);
                await WriteAtomicallyAsync(path, json).ConfigureAwait(false);
            }
            finally
            {
                SaveLock.Release();
            }
        }

        private static bool TryDeserializeSupported(
            string json,
            out SavedWidgetCatalog? catalog,
            out int sourceVersion)
        {
            catalog = null;
            sourceVersion = 0;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!TryReadVersion(document.RootElement, out sourceVersion))
                {
                    return false;
                }
                catalog = sourceVersion switch
                {
                    SavedWidgetCatalogRules.CurrentVersion =>
                        JsonSerializer.Deserialize<SavedWidgetCatalog>(json, JsonOptions),
                    2 or 3 or 4 or 5 or 6 => JsonSerializer.Deserialize<SavedWidgetCatalog>(json, JsonOptions),
                    1 => MigrateVersionOne(json),
                    _ => null
                };
                if (catalog == null)
                {
                    return false;
                }

                catalog.Widgets ??= [];
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static SavedWidgetCatalog? MigrateVersionOne(string json)
        {
            var legacy = JsonSerializer.Deserialize<VersionOneCatalog>(json, JsonOptions);
            if (legacy == null)
            {
                return null;
            }
            return new SavedWidgetCatalog
            {
                Version = SavedWidgetCatalogRules.CurrentVersion,
                Widgets = (legacy.Widgets ?? [])
                    .Select(widget => new SavedWidgetDefinition
                    {
                        Id = widget.Id,
                        Name = widget.Name,
                        Type = widget.Type,
                        Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(widget.SelectedPairs)
                    })
                    .ToArray()
            };
        }

        private static bool TryReadVersion(JsonElement root, out int version)
        {
            version = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "Version", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.TryGetInt32(out version);
                }
            }

            return false;
        }

        private static async Task<bool> TryWriteBackupAsync(string folderPath, string fileName, string json)
        {
            try
            {
                Directory.CreateDirectory(folderPath);
                await WriteAtomicallyAsync(Path.Combine(folderPath, fileName), json).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
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

        private sealed class VersionOneCatalog
        {
            public VersionOneWidget[] Widgets { get; set; } = [];
        }

        private sealed class VersionOneWidget
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = PanelWidgetTypes.PriceTicker;
            public string[] SelectedPairs { get; set; } = [];
        }
    }
}
