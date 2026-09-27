using TrenchHQ.Core.Social;
using TrenchHQ.Infrastructure.Social;
using System.Diagnostics;
using System.Text.Json;

internal static class SocialLiveValidation
{
    internal static async Task RunAsync(string[] args)
    {
        Console.WriteLine("Live X acquisition validation " + DateTimeOffset.UtcNow.ToString("O"));
        if (args.Length > 0 && args[0] == "--settings")
        {
            if (args.Length != 2) throw new ArgumentException("Use --live --settings <social_providers.dpapi path>.");
            if (!File.Exists(args[1])) { Console.WriteLine("BLOCKED: No saved social provider settings."); return; }
            var settings = await SocialProviderStore.LoadAsync(args[1]);
            foreach (var config in settings.Providers)
            {
                var error = SocialFeedRules.Validate(config);
                if (error.Length > 0) { Console.WriteLine(JsonSerializer.Serialize(new { source = config.Id, outcome = "invalid_configuration", reason = error })); continue; }
                if (config.Id == "xapi")
                {
                    // Verification must not reconcile/delete a running application's rules.
                    await ProbeAsync(config, ["whale_alert"], true, 25, "configured X API connection");
                }
            }
            return;
        }

        Console.WriteLine("Use --live --settings <social_providers.dpapi path> after configuring the official X API. No public RSS probes remain.");
    }

    private static async Task ProbeAsync(SocialProviderConfiguration config, string[] handles, bool probe, int seconds, string label)
    {
        var timer = Stopwatch.StartNew();
        var unique = new HashSet<string>();
        var count = 0;
        var matched = 0;
        var newlyCreated = 0;
        var started = DateTimeOffset.UtcNow;
        var outcome = "no_posts";
        var reason = "";
        var receivedHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? newest = null;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var source = new SocialFeedSources();
        try
        {
            await source.RunAsync(config, handles, post =>
            {
                count++;
                unique.Add(post.Id);
                receivedHandles.Add(post.Handle);
                if (handles.Contains(post.Handle, StringComparer.OrdinalIgnoreCase)) matched++;
                if (post.CreatedAt > newest || newest == null) newest = post.CreatedAt;
                if (post.CreatedAt >= started) newlyCreated++;
            }, _ => { }, cancel.Token, probe);
            outcome = count > 0 ? "posts_received" : "connected_no_posts";
        }
        catch (SocialSourceException error) { outcome = "source_error"; reason = error.Message; }
        catch (OperationCanceledException) { outcome = count > 0 ? "observation_completed_with_posts" : "timeout_no_posts"; }
        catch (Exception error) { outcome = "transport_or_parse_error"; reason = error.GetType().Name; }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            source = config.Id, target = label, outcome, reason, durationMs = timer.ElapsedMilliseconds,
            received = count, distinctPostIds = unique.Count, matchingPosts = matched, createdDuringObservation = newlyCreated,
            handles = receivedHandles, newestPostUtc = newest
        }));
    }
}
