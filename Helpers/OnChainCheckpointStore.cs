using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal static class OnChainCheckpointStore
    {
        private const string FileName = "onchain-checkpoints-v1.json";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        internal static async Task<ulong?> LoadAsync(
            string folderPath,
            string providerConfigurationId,
            CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(folderPath, FileName);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                await using var stream = File.OpenRead(path);
                var document = await JsonSerializer.DeserializeAsync<CheckpointDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                return document?.Version == 1
                       && document.ConfirmedSlots.TryGetValue(providerConfigurationId, out var slot)
                    ? slot
                    : null;
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

        internal static async Task SaveAsync(
            string folderPath,
            string providerConfigurationId,
            ulong confirmedSlot,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(folderPath);
            var path = Path.Combine(folderPath, FileName);
            CheckpointDocument document;
            try
            {
                await using var input = File.Exists(path) ? File.OpenRead(path) : null;
                document = input == null
                    ? new CheckpointDocument()
                    : await JsonSerializer.DeserializeAsync<CheckpointDocument>(
                          input,
                          JsonOptions,
                          cancellationToken).ConfigureAwait(false)
                      ?? new CheckpointDocument();
            }
            catch (JsonException)
            {
                document = new CheckpointDocument();
            }
            catch (IOException)
            {
                document = new CheckpointDocument();
            }

            document.Version = 1;
            document.ConfirmedSlots[providerConfigurationId] = confirmedSlot;
            var temporaryPath = path + ".tmp";
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, true);
        }

        private sealed class CheckpointDocument
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, ulong> ConfirmedSlots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
