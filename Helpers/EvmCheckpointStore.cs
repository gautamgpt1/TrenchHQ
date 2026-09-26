using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class EvmCheckpoint
    {
        public string ChainId { get; set; } = "1";
        public ulong SafeBlockNumber { get; set; }
        public string SafeBlockHash { get; set; } = string.Empty;
        public long ObservedAtUnixMs { get; set; }
    }

    internal static class EvmCheckpointStore
    {
        internal const string FileName = "evm-checkpoints-v1.json";

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        internal static async Task<EvmCheckpoint?> LoadAsync(
            string folderPath,
            string providerProfileId,
            CancellationToken cancellationToken = default)
        {
            ValidateProfileId(providerProfileId);
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var document = await LoadDocumentAsync(folderPath, cancellationToken).ConfigureAwait(false);
                return document.Checkpoints.TryGetValue(providerProfileId, out var checkpoint)
                       && EvmAddress.IsHash(checkpoint.SafeBlockHash)
                    ? checkpoint
                    : null;
            }
            finally
            {
                Gate.Release();
            }
        }

        internal static async Task SaveAsync(
            string folderPath,
            string providerProfileId,
            EvmCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            ValidateProfileId(providerProfileId);
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (!EvmAddress.IsHash(checkpoint.SafeBlockHash))
            {
                throw new ArgumentException("The EVM checkpoint hash is invalid.", nameof(checkpoint));
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(folderPath);
                var document = await LoadDocumentAsync(folderPath, cancellationToken).ConfigureAwait(false);
                document.Checkpoints[providerProfileId] = checkpoint;
                var json = JsonSerializer.Serialize(document, JsonOptions);
                var path = Path.Combine(folderPath, FileName);
                var temporaryPath = path + ".tmp";
                await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<EvmCheckpointDocument> LoadDocumentAsync(
            string folderPath,
            CancellationToken cancellationToken)
        {
            var path = Path.Combine(folderPath, FileName);
            if (!File.Exists(path))
            {
                return new EvmCheckpointDocument();
            }
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<EvmCheckpointDocument>(json, JsonOptions);
                return document is { Version: 1, Checkpoints: not null }
                    ? document
                    : new EvmCheckpointDocument();
            }
            catch (JsonException)
            {
                return new EvmCheckpointDocument();
            }
        }

        private static void ValidateProfileId(string providerProfileId)
        {
            if (string.IsNullOrWhiteSpace(providerProfileId)
                || providerProfileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("The provider profile ID is invalid.", nameof(providerProfileId));
            }
        }

        private sealed class EvmCheckpointDocument
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, EvmCheckpoint> Checkpoints { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
