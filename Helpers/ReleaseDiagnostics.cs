using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TrenchHQ.Helpers
{
    internal static class ReleaseDiagnostics
    {
        // Explicit projection only: never serialize settings, errors, URLs, logs or provider objects.
        internal static string Create(Version version, OnChainPipelineDiagnosticsSnapshot pipeline)
        {
            using var process = Process.GetCurrentProcess();
            return JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Product = "TrenchHQ",
                Version = version.ToString(),
                CreatedUtc = DateTimeOffset.UtcNow,
                WindowsVersion = Environment.OSVersion.Version.ToString(),
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                DotnetVersion = Environment.Version.ToString(),
                WorkingSetBytes = process.WorkingSet64,
                ProcessorTimeMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                Pipeline = pipeline
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
